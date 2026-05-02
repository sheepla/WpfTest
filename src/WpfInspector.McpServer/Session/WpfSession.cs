using System.ComponentModel;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json.Nodes;
using WpfInspector.Injector;
using WpfInspector.Protocol;

namespace WpfInspector.McpServer.Session;

internal sealed class WpfSession : IAsyncDisposable
{
    private const int ConnectionTimeoutSeconds = 30;

    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _sessionDirectory;

    public int ProcessId { get; }
    public bool IsConnected => _pipe.IsConnected;

    private WpfSession(int processId, NamedPipeServerStream pipe, StreamReader reader, StreamWriter writer, string sessionDirectory)
    {
        ProcessId = processId;
        _pipe = pipe;
        _reader = reader;
        _writer = writer;
        _sessionDirectory = sessionDirectory;
    }

    public static async Task<WpfSession> CreateAsync(int pid, string baseDirectory, CancellationToken ct = default)
    {
        var nativeDllPath = InjectionAssets.FindNativeBootstrap(baseDirectory);

        var sessionId = Guid.NewGuid().ToString("N");
        var sessionDirectory = Path.Combine(
            Path.GetTempPath(),
            "WpfInspector",
            "mcp-sessions",
            $"pid-{pid}",
            sessionId);

        Directory.CreateDirectory(sessionDirectory);

        var sessionNativeDllPath = Path.Combine(
            sessionDirectory,
            $"WpfInspector.NativeBootstrap.{sessionId}.dll");
        File.Copy(nativeDllPath, sessionNativeDllPath, overwrite: true);

        foreach (var fileName in InjectionAssets.ManagedPayloadFiles)
        {
            var sourcePath = Path.Combine(baseDirectory, fileName);
            if (!File.Exists(sourcePath))
            {
                if (IsRequiredManagedAsset(fileName))
                    throw new FileNotFoundException($"注入に必要なファイルが見つかりません: {fileName}", sourcePath);
                continue;
            }
            File.Copy(sourcePath, Path.Combine(sessionDirectory, fileName), overwrite: true);
        }

        var pipeName = $"wpfinspector-{pid}-{sessionId}";

        // サーバーを先に作成してからDLLを注入する。
        // TestHelperがクライアントとして接続してくる。
        var pipe = NamedPipeServerStreamAcl.Create(
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
                ProcessInjector.Inject(pid, sessionNativeDllPath);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                throw new InvalidOperationException(
                    "対象プロセスの権限が McpServer より高いため DLL を注入できません。管理者権限で起動してください。", ex);
            }

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(ConnectionTimeoutSeconds));
            try { await pipe.WaitForConnectionAsync(connectCts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("TestHelper が 30 秒以内に接続しませんでした。注入に失敗したか、対象プロセスで DLL がすでに読み込まれている可能性があります。");
            }

            var reader = new StreamReader(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readyCts.CancelAfter(TimeSpan.FromSeconds(ConnectionTimeoutSeconds));
            try
            {
                var ready = await reader.ReadLineAsync(readyCts.Token);
                if (ready != "READY")
                    throw new InvalidOperationException($"TestHelper から予期しない応答を受信しました: {ready}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("READY 受信がタイムアウトしました。");
            }

            return new WpfSession(pid, pipe, reader, writer, sessionDirectory);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException($"PID {ProcessId} への接続が切断されています。");

        await _lock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(command.AsMemory(), ct);
            return await _reader.ReadLineAsync(ct) ?? "ERROR:No response";
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<PipeResponse> SendRequestAsync(string operation, JsonObject? arguments = null, CancellationToken ct = default)
    {
        var request = new PipeRequest
        {
            Operation = operation,
            Arguments = arguments ?? []
        };

        var payload = await SendCommandAsync(PipeProtocol.EncodeRequest(request), ct);
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
        try { await _writer.WriteLineAsync("EXIT"); } catch { }
        _writer.Dispose();
        _reader.Dispose();
        await _pipe.DisposeAsync();
        _lock.Dispose();
        try { Directory.Delete(_sessionDirectory, recursive: true); } catch { }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        // McpServer が管理者権限で動いていても、中整合性の対象プロセスが
        // 同一ユーザーとして接続できるようにする。
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm(
            "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;AU)");
        return security;
    }

    private static bool IsRequiredManagedAsset(string fileName) =>
        InjectionAssets.ManagedPayloadFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase);
}
