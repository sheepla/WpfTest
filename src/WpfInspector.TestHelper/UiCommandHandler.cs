using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfInspector.InspectionPayload;

namespace WpfInspector.TestHelper;

/// <summary>
/// 注入先プロセス内でテストコードと名前付きパイプ通信を行い、
/// WPF ビジュアルツリーを操作・検査するハンドラ。
///
/// プロトコル（テキスト行区切り）:
///   [注入完了後] → "READY" を送信
///   GET_TREE              → ビジュアルツリー JSON を返す
///   GET_TITLE             → ウィンドウタイトルを返す
///   GET_ITEMS:{name}      → DataGrid の行数を返す
///   GET_STATUS            → StatusMessage TextBlock の Text を返す
///   GET_TEXT:{name}       → 要素のテキスト内容を返す
///   CLICK:{name}          → Button を押す
///   TYPE:{name}:{text}    → TextBox にテキストを入力する
///   EXIT                  → 接続を終了する
/// </summary>
internal static class UiCommandHandler
{
    private static readonly Lock LogSync = new();

    internal static async Task RunAsync(string fullPipeName)
    {
        // "\\\\.\\pipe\\wpfinspector-{PID}" の "wpfinspector-{PID}" 部分だけ取り出す
        var pipeName = fullPipeName.Replace(@"\\.\pipe\", string.Empty)
                                   .TrimStart('\\').TrimStart('.');
        Log($"RunAsync start full='{fullPipeName}' pipe='{pipeName}'");

        while (true)
        {
            try
            {
                // インスペクター側がサーバーを立てているので、クライアントとして接続する。
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                using var cts = new CancellationTokenSource(30_000);
                Log($"Connecting to server pipe='{pipeName}'");
                await pipe.ConnectAsync(cts.Token);
                Log($"Connected pipe='{pipeName}'");

                using var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024,
                    leaveOpen: true);
                await using var writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                await writer.WriteLineAsync("READY");
                Log($"Sent READY pipe='{pipeName}'");

                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    Log($"Received command='{line}' pipe='{pipeName}'");
                    if (line == "EXIT")
                    {
                        Log($"Received EXIT pipe='{pipeName}'");
                        break;
                    }

                    var response = Dispatch(line);
                    Log($"Sending response length={response.Length} pipe='{pipeName}'");
                    await writer.WriteLineAsync(response);
                }
            }
            catch (Exception exception)
            {
                // 接続失敗・切断は無視し、同じプロセスに対する次回の解析要求を待つ
                Log($"Loop exception pipe='{pipeName}' error={exception}");
            }

            await Task.Delay(250);
        }
    }

    private static string Dispatch(string command)
    {
        try
        {
            if (command == "GET_TREE")
                return Inspector.CollectJson(Environment.ProcessId);
            if (command == "GET_TITLE")
                return GetWindowTitle();
            if (command == "GET_STATUS")
                return GetStatusText();
            if (command.StartsWith("GET_ITEMS:", StringComparison.Ordinal))
                return GetDataGridItemCount(command[10..]);
            if (command.StartsWith("GET_TEXT:", StringComparison.Ordinal))
                return GetText(command[9..]);
            if (command.StartsWith("CLICK:", StringComparison.Ordinal))
                return ClickButton(command[6..]);
            if (command.StartsWith("TYPE:", StringComparison.Ordinal))
                return TypeText(command[5..]);
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

    private static string GetText(string elementName)
    {
        string? text = null;
        InvokeOnUiThread(() =>
        {
            var window = Application.Current?.MainWindow;
            if (window is null) return;
            var tb = FindDescendantByName<TextBlock>(window, elementName);
            if (tb is not null) { text = tb.Text; return; }
            var tbx = FindDescendantByName<TextBox>(window, elementName);
            if (tbx is not null) { text = tbx.Text; return; }
        });
        return text ?? "ERROR:Not found";
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

    private static string TypeText(string arg)
    {
        // arg = "SearchBox:hello world"
        var sep = arg.IndexOf(':');
        if (sep < 0) return "ERROR:Invalid format";
        var name = arg[..sep];
        var text = arg[(sep + 1)..];

        bool done = false;
        InvokeOnUiThread(() =>
        {
            var window = Application.Current?.MainWindow;
            if (window is null) return;
            var tb = FindDescendantByName<TextBox>(window, name)
                  ?? FindDescendant<TextBox>(window, _ => true);
            if (tb is null) return;
            tb.Text = text;
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            done = true;
        });
        return done ? "OK" : "ERROR:TextBox not found";
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

    private static void Log(string message)
    {
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), $"wpfi_helper_{Environment.ProcessId}.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (LogSync)
            {
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // ログ出力失敗は無視
        }
    }
}
