using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfInspector.InspectionPayload;

/// <summary>
/// hostfxr の load_assembly_and_get_function_pointer から呼ばれるエントリポイント。
/// ビジュアルツリーを収集して JSON を名前付きパイプへ送信する。
/// </summary>
public static class EntryPoint
{
    public static int Run(IntPtr arg, int argSize)
    {
        var fullPipeName = Marshal.PtrToStringUTF8(arg, argSize - 1);
        if (string.IsNullOrEmpty(fullPipeName))
            return -1;

        // バックグラウンドで実行して DllMain スレッドをすぐ解放する
        _ = Task.Run(() => SendInspectionDataAsync(fullPipeName));
        return 0;
    }

    private static async Task SendInspectionDataAsync(string fullPipeName)
    {
        try
        {
            var pipeName = ParsePipeName(fullPipeName);
            var processId = Environment.ProcessId;

            // 収集（UI スレッドに Dispatcher.Invoke するので少し待つ）
            await Task.Delay(500);
            var json = Inspector.CollectJson(processId);

            // サーバー（WpfInspector）へ送信
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(cts.Token);

            // BOM なし UTF-8 で 1 行送る
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
            await writer.WriteLineAsync(json);
            await writer.FlushAsync();
        }
        catch
        {
            // 接続失敗・例外はサイレントに無視
        }
    }

    private static string ParsePipeName(string fullPipeName)
    {
        // "\\.\pipe\wpfinspector-{PID}" → "wpfinspector-{PID}"
        const string prefix = @"\\.\pipe\";
        return fullPipeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fullPipeName[prefix.Length..]
            : fullPipeName;
    }
}
