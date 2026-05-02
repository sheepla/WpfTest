using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json.Nodes;
using WpfInspector.Injector;
using WpfInspector.Protocol;

namespace WpfInspector.LlmAgent;

/// <summary>
/// 対象 WPF プロセスに NativeBootstrap.dll を注入し、TestHelper との
/// 双方向パイプ通信でコマンドを送受信する。
/// </summary>
internal sealed class WpfPipeClient : IAsyncDisposable
{
    private const int ConnectionTimeoutSeconds = 30;

    private readonly int _pid;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string? _sessionDirectory;

    internal WpfPipeClient(int pid) => _pid = pid;

    internal async Task InjectAndConnectAsync()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var nativeDllPath = InjectionAssets.FindNativeBootstrap(baseDirectory);
        var sessionId = Guid.NewGuid().ToString("N");
        _sessionDirectory = Path.Combine(
            Path.GetTempPath(),
            "WpfInspector",
            "llm-sessions",
            $"pid-{_pid}",
            sessionId);
        Directory.CreateDirectory(_sessionDirectory);

        var sessionNativeDllPath = Path.Combine(
            _sessionDirectory,
            $"WpfInspector.NativeBootstrap.{sessionId}.dll");
        File.Copy(nativeDllPath, sessionNativeDllPath, overwrite: true);

        foreach (var fileName in InjectionAssets.ManagedPayloadFiles)
        {
            var sourcePath = Path.Combine(baseDirectory, fileName);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"注入に必要なファイルが見つかりません: {fileName}", sourcePath);
            }

            File.Copy(sourcePath, Path.Combine(_sessionDirectory, fileName), overwrite: true);
        }

        var pipeName = $"wpfinspector-{_pid}-{sessionId}";
        _pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            CreatePipeSecurity(),
            HandleInheritability.None,
            additionalAccessRights: 0);

        try
        {
            try
            {
                ProcessInjector.Inject(_pid, sessionNativeDllPath);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                throw new InvalidOperationException(
                    "対象プロセスの権限が LlmAgent より高いため DLL を注入できません。管理者権限で起動してください。", ex);
            }

            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(ConnectionTimeoutSeconds));
            await _pipe.WaitForConnectionAsync(connectCts.Token);

            _reader = new StreamReader(_pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            var ready = await _reader.ReadLineAsync(connectCts.Token);
            if (ready != "READY")
            {
                throw new InvalidOperationException($"予期しない応答: {ready}");
            }
        }
        catch
        {
            await DisposePipeAsync();
            TryDeleteSessionDirectory();
            throw;
        }
    }

    internal async Task<string> SendCommandAsync(string command)
    {
        if (_writer is null || _reader is null)
            throw new InvalidOperationException("未接続。InjectAndConnectAsync を先に呼び出してください。");

        await _lock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(command);
            return await _reader.ReadLineAsync() ?? "ERROR:No response";
        }
        finally
        {
            _lock.Release();
        }
    }

    internal async Task<PipeResponse> SendRequestAsync(string operation, JsonObject? arguments = null)
    {
        var request = new PipeRequest
        {
            Operation = operation,
            Arguments = arguments ?? []
        };

        var payload = await SendCommandAsync(PipeProtocol.EncodeRequest(request));
        if (!PipeProtocol.TryDecodeResponse(payload, out var response) || response is null)
        {
            return PipeProtocol.Error(
                errorCode: "invalid_response",
                errorMessage: "TestHelper から RPC 応答を解析できませんでした。",
                requestId: request.RequestId,
                data: JsonValue.Create(payload));
        }

        return response;
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            try { await _writer.WriteLineAsync("EXIT"); } catch { }
        }

        await DisposePipeAsync();
        _lock.Dispose();
        TryDeleteSessionDirectory();
    }

    private async Task DisposePipeAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync();
            _writer = null;
        }

        _reader?.Dispose();
        _reader = null;

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync();
            _pipe = null;
        }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm(
            "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;AU)");
        return security;
    }

    private void TryDeleteSessionDirectory()
    {
        if (string.IsNullOrWhiteSpace(_sessionDirectory))
        {
            return;
        }

        try { Directory.Delete(_sessionDirectory, recursive: true); } catch { }
        _sessionDirectory = null;
    }
}
