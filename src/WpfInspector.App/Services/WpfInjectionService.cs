using System.IO;
using System.IO.Pipes;
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
/// </summary>
public sealed class WpfInjectionService : IWpfInjectionService
{
    private readonly string _nativeDllPath;

    public WpfInjectionService()
    {
        var dir = AppContext.BaseDirectory;
        _nativeDllPath = Path.Combine(dir, "WpfInspector.NativeBootstrap.dll");
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

        var pipeName = $"wpfinspector-{processId}";

        // サーバー (受信専用) を注入前に作成する
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        // 注入実行
        ProcessInjector.Inject(processId, _nativeDllPath);

        // ペイロードが接続して JSON を送ってくるまで待つ（最大 30 秒）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await pipe.WaitForConnectionAsync(cts.Token);

        // BOM なし UTF-8 で 1 行読む
        using var reader = new StreamReader(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var json = await reader.ReadLineAsync(cts.Token);

        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("InspectionPayload から空のレスポンスを受信しました。");

        return JsonSerializer.Deserialize<WpfInspectionResult>(json)
            ?? throw new InvalidOperationException("JSON のデシリアライズに失敗しました。");
    }
}
