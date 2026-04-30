using System.IO;
using System.IO.Pipes;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using WpfInspector.Core.Models;
using WpfInspector.Core.Services;
using WpfInspector.Injector;
using WpfInspector.InspectionPayload;

namespace WpfInspector.App.Services;

/// <summary>
/// 対象 WPF プロセスを走査し、自己プロセスは直接、外部プロセスは
/// NativeBootstrap.dll 注入経由で WpfInspectionResult を取得する。
/// TestHelper を経由して双方向パイプで GET_TREE コマンドを送受信する。
/// </summary>
public sealed class WpfInjectionService : IWpfInjectionService
{
    private static readonly Lock LogSync = new();
    private static readonly string[] ManagedPayloadFiles =
    [
        "WpfInspector.TestHelper.dll",
        "WpfInspector.TestHelper.deps.json",
        "WpfInspector.TestHelper.runtimeconfig.json",
        "WpfInspector.InspectionPayload.dll",
        "WpfInspector.InspectionPayload.deps.json",
        "WpfInspector.InspectionPayload.runtimeconfig.json",
        "WpfInspector.Core.dll"
    ];

    private readonly string _baseDirectory;
    private readonly string _nativeDllPath;
    private readonly string _logPath;

    public WpfInjectionService()
    {
        _baseDirectory = AppContext.BaseDirectory;
        _nativeDllPath = Path.Combine(_baseDirectory, "WpfInspector.NativeBootstrap.dll");
        _logPath = Path.Combine(Path.GetTempPath(), "wpfi_service.log");
    }

    public async Task<WpfInspectionResult> InspectAsync(int processId)
    {
        if (processId == Environment.ProcessId)
        {
            return await Task.Run(SelfInspection.InspectCurrentProcess);
        }

        if (!File.Exists(_nativeDllPath))
            throw new FileNotFoundException(
                "WpfInspector.NativeBootstrap.dll が見つかりません。ビルドを確認してください。",
                _nativeDllPath);

        EnsurePrivilegeCompatibility(processId);

        var session = CreateInjectionSession(processId);
        var pipeName = session.PipeName;
        Log($"InspectAsync start pid={processId} pipe={pipeName} dll={session.NativeBootstrapPath}");

        // 注入実行
        try
        {
            ProcessInjector.Inject(processId, session.NativeBootstrapPath);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            Log($"Inject access denied pid={processId} error={exception}");
            throw new InvalidOperationException(
                "対象プロセスの権限が WpfInspector より高いため DLL を注入できません。WpfInspector を対象と同等以上の権限で起動してください。",
                exception);
        }
        Log($"Injected pid={processId}; waiting for server pipe={pipeName}");

        // TestHelper 側がサーバーになり、WpfInspector はクライアントとして接続する
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        // TestHelper が接続して "READY" を送ってくるまで待つ（最大 30 秒）
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await pipe.ConnectAsync(connectCts.Token);
            Log($"Connected pipe={pipeName}");
        }
        catch (OperationCanceledException)
        {
            Log($"Timeout waiting for connection pipe={pipeName}");
            throw new TimeoutException("TestHelper が 30 秒以内に接続しませんでした。注入に失敗したか、対象プロセスで DLL がすでに読み込まれている可能性があります（対象プロセスを再起動してください）。");
        }

        using var reader = new StreamReader(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        // READY と GET_TREE 応答に別タイムアウトを設ける（UI 走査は最大 60 秒）
        using var exchangeCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var ready = await reader.ReadLineAsync(exchangeCts.Token);
            Log($"Received ready='{ready}' pipe={pipeName}");
            if (ready != "READY")
                throw new InvalidOperationException($"TestHelper から予期しない応答を受信しました: {ready}");

            // GET_TREE コマンドでビジュアルツリー JSON を取得
            await writer.WriteLineAsync("GET_TREE");
            var json = await reader.ReadLineAsync(exchangeCts.Token);
            Log($"Received tree response length={(json?.Length ?? 0)} pipe={pipeName}");

            await writer.WriteLineAsync("EXIT");

            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("TestHelper から空のレスポンスを受信しました。");

            if (json.StartsWith("ERROR:", StringComparison.Ordinal))
                throw new InvalidOperationException($"TestHelper でエラーが発生しました: {json}");

            return JsonSerializer.Deserialize<WpfInspectionResult>(json)
                ?? throw new InvalidOperationException("JSON のデシリアライズに失敗しました。");
        }
        catch (OperationCanceledException)
        {
            Log($"Timeout waiting for response pipe={pipeName}");
            throw new TimeoutException("UI ツリーの取得が 60 秒以内に完了しませんでした。対象プロセスの UI スレッドがビジーか、デッドロックしている可能性があります。");
        }
        catch (Exception exception)
        {
            Log($"InspectAsync failed pipe={pipeName} error={exception}");
            throw;
        }
    }

    private InjectionSession CreateInjectionSession(int processId)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var sessionDirectory = Path.Combine(
            Path.GetTempPath(),
            "WpfInspector",
            "sessions",
            $"pid-{processId}",
            sessionId);

        Directory.CreateDirectory(sessionDirectory);

        var nativeBootstrapPath = Path.Combine(
            sessionDirectory,
            $"WpfInspector.NativeBootstrap.{sessionId}.dll");
        File.Copy(_nativeDllPath, nativeBootstrapPath, overwrite: true);

        foreach (var fileName in ManagedPayloadFiles)
        {
            var sourcePath = Path.Combine(_baseDirectory, fileName);
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"注入に必要なファイルが見つかりません: {fileName}", sourcePath);

            var destinationPath = Path.Combine(sessionDirectory, fileName);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        return new InjectionSession(
            $"wpfinspector-{processId}-{sessionId}",
            nativeBootstrapPath);
    }

    private void EnsurePrivilegeCompatibility(int processId)
    {
        if (!ProcessPrivilegeInspector.TryGetProcessContext(Environment.ProcessId, out var currentContext, out var currentError))
        {
            Log($"Privilege probe failed for self pid={Environment.ProcessId} error={currentError}");
            return;
        }

        if (!ProcessPrivilegeInspector.TryGetProcessContext(processId, out var targetContext, out var targetError))
        {
            Log($"Privilege probe failed for target pid={processId} error={targetError}");
            return;
        }

        Log($"Privilege current={currentContext.Describe()} target={targetContext.Describe()}");
        if (targetContext.IntegrityLevel > currentContext.IntegrityLevel)
        {
            throw new InvalidOperationException(
                $"権限レベルが一致しません。対象プロセスは {targetContext.Describe()}、WpfInspector は {currentContext.Describe()} です。WpfInspector を対象と同等以上の権限で起動してください。");
        }
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
        lock (LogSync)
        {
            File.AppendAllText(_logPath, line, Encoding.UTF8);
        }
    }

    private sealed record InjectionSession(
        string PipeName,
        string NativeBootstrapPath);
}
