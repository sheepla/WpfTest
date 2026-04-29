using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WpfInspector.TestHelper;

/// <summary>
/// 注入先プロセス内でテストコードと名前付きパイプ通信を行い、
/// WPF ビジュアルツリーを操作・検査するハンドラ。
///
/// プロトコル（テキスト行区切り）:
///   [注入完了後] → "READY" を送信
///   GET_TITLE             → ウィンドウタイトルを返す
///   GET_ITEMS:{name}      → DataGrid の行数を返す
///   GET_STATUS            → StatusMessage TextBlock の Text を返す
///   CLICK:{name}          → Button を押す
///   EXIT                  → 接続を終了する
/// </summary>
internal static class UiCommandHandler
{
    internal static async Task RunAsync(string fullPipeName)
    {
        // "\\\\.\\pipe\\wpfinspector-{PID}" の "wpfinspector-{PID}" 部分だけ取り出す
        var pipeName = fullPipeName.Replace(@"\\.\pipe\", string.Empty)
                                   .TrimStart('\\').TrimStart('.');

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            using var cts = new CancellationTokenSource(15_000);
        await pipe.ConnectAsync(cts.Token);

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            await writer.WriteLineAsync("READY");

            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (line == "EXIT") break;
                var response = Dispatch(line);
                await writer.WriteLineAsync(response);
            }
        }
        catch
        {
            // 接続失敗は無視（テストが既に終了している場合など）
        }
    }

    private static string Dispatch(string command)
    {
        try
        {
            if (command == "GET_TITLE")
                return GetWindowTitle();
            if (command == "GET_STATUS")
                return GetStatusText();
            if (command.StartsWith("GET_ITEMS:", StringComparison.Ordinal))
                return GetDataGridItemCount(command[10..]);
            if (command.StartsWith("CLICK:", StringComparison.Ordinal))
                return ClickButton(command[6..]);
            return "ERROR:Unknown command";
        }
        catch (Exception ex)
        {
            return $"ERROR:{ex.GetType().Name}:{ex.Message}";
        }
    }

    // ── UI 操作メソッド ────────────────────────────────────────────────────

    private static string GetWindowTitle()
    {
        string? title = null;
        InvokeOnUiThread(() => title = Application.Current?.MainWindow?.Title);
        return title ?? "ERROR:No window";
    }

    private static string GetStatusText()
    {
        string? text = null;
        InvokeOnUiThread(() =>
        {
            var window = Application.Current?.MainWindow;
            if (window is null) return;
            var tb = FindDescendant<TextBlock>(window, e => e.Text?.StartsWith("準備", StringComparison.Ordinal) == true
                                                          || e.Text?.Length > 0);
            text = tb?.Text;
        });
        return text ?? "ERROR:Not found";
    }

    private static string GetDataGridItemCount(string gridName)
    {
        int count = -1;
        InvokeOnUiThread(() =>
        {
            var window = Application.Current?.MainWindow;
            if (window is null) return;
            var grid = FindDescendantByName<DataGrid>(window, gridName);
            count = grid?.Items.Count ?? -1;
        });
        return count >= 0 ? count.ToString() : "ERROR:Grid not found";
    }

    private static string ClickButton(string buttonName)
    {
        bool found = false;
        InvokeOnUiThread(() =>
        {
            var window = Application.Current?.MainWindow;
            if (window is null) return;
            var btn = FindDescendantByName<Button>(window, buttonName)
                   ?? FindDescendant<Button>(window, b => b.Content?.ToString() == buttonName);
            if (btn is null) return;
            btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            found = true;
        });
        return found ? "OK" : "ERROR:Button not found";
    }

    // ── ビジュアルツリー探索 ────────────────────────────────────────────────

    private static T? FindDescendantByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
        => FindDescendant<T>(root, e => e.Name == name);

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && predicate(typed)) return typed;
            var found = FindDescendant(child, predicate);
            if (found is not null) return found;
        }
        return null;
    }

    // ── スレッドマーシャリング ───────────────────────────────────────────────

    private static void InvokeOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }
}
