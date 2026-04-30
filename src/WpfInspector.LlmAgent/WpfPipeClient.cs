using System.IO;
using System.IO.Pipes;
using System.Text;
using WpfInspector.Injector;

namespace WpfInspector.LlmAgent;

/// <summary>
/// 対象 WPF プロセスに NativeBootstrap.dll を注入し、TestHelper との
/// 双方向パイプ通信でコマンドを送受信する。
/// </summary>
internal sealed class WpfPipeClient : IAsyncDisposable
{
    private readonly int _pid;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    internal WpfPipeClient(int pid) => _pid = pid;

    internal async Task InjectAndConnectAsync()
    {
        var nativeDllPath = InjectionAssets.FindNativeBootstrap(AppContext.BaseDirectory);
        var pipeName = $"wpfinspector-{_pid}";

        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        ProcessInjector.Inject(_pid, nativeDllPath);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _pipe.WaitForConnectionAsync(cts.Token);

        _reader = new StreamReader(_pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        // TestHelper が送る "READY" を受信してから操作を開始する
        var ready = await _reader.ReadLineAsync(cts.Token);
        if (ready != "READY")
            throw new InvalidOperationException($"予期しない応答: {ready}");
    }

    internal async Task<string> SendCommandAsync(string command)
    {
        if (_writer is null || _reader is null)
            throw new InvalidOperationException("未接続。InjectAndConnectAsync を先に呼び出してください。");

        await _writer.WriteLineAsync(command);
        return await _reader.ReadLineAsync() ?? "ERROR:No response";
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            try { await _writer.WriteLineAsync("EXIT"); } catch { }
            await _writer.DisposeAsync();
        }
        _reader?.Dispose();
        if (_pipe is not null)
            await _pipe.DisposeAsync();
    }
}
