using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfInspector.McpServer.Session;

namespace WpfInspector.McpServer.Tools;

[McpServerToolType]
internal sealed class UiTools(WpfSessionManager sessionManager)
{
    [McpServerTool(Name = "get_visual_tree"), Description("WPFビジュアルツリーをJSONで取得する")]
    public async Task<string> GetVisualTreeAsync([Description("対象プロセスID")] int pid, CancellationToken ct)
        => await SendCommandAsync(pid, "GET_TREE", ct);

    [McpServerTool(Name = "get_window_title"), Description("メインウィンドウのタイトルを取得する")]
    public async Task<string> GetWindowTitleAsync([Description("対象プロセスID")] int pid, CancellationToken ct)
        => await SendCommandAsync(pid, "GET_TITLE", ct);

    [McpServerTool(Name = "get_element_text"), Description("名前指定した要素のテキストを取得する")]
    public async Task<string> GetElementTextAsync(
        [Description("対象プロセスID")] int pid,
        [Description("要素名(Name属性)")] string elementName,
        CancellationToken ct)
        => await SendCommandAsync(pid, $"GET_TEXT:{elementName}", ct);

    [McpServerTool(Name = "click_button"), Description("名前またはContent文字列でボタンをクリックする")]
    public async Task<string> ClickButtonAsync(
        [Description("対象プロセスID")] int pid,
        [Description("ボタン名またはContent文字列")] string buttonName,
        CancellationToken ct)
        => await SendCommandAsync(pid, $"CLICK:{buttonName}", ct);

    [McpServerTool(Name = "type_text"), Description("名前指定したTextBoxにテキストを入力する")]
    public async Task<string> TypeTextAsync(
        [Description("対象プロセスID")] int pid,
        [Description("TextBox名")] string elementName,
        [Description("入力テキスト")] string text,
        CancellationToken ct)
        => await SendCommandAsync(pid, $"TYPE:{elementName}:{text}", ct);

    [McpServerTool(Name = "get_data_grid_item_count"), Description("DataGridの行数を取得する")]
    public async Task<string> GetDataGridItemCountAsync(
        [Description("対象プロセスID")] int pid,
        [Description("DataGrid名")] string gridName,
        CancellationToken ct)
        => await SendCommandAsync(pid, $"GET_ITEMS:{gridName}", ct);

    private async Task<string> SendCommandAsync(int pid, string command, CancellationToken ct)
    {
        try
        {
            var session = await sessionManager.GetOrCreateSessionAsync(pid, ct);
            return await session.SendCommandAsync(command, ct);
        }
        catch (FileNotFoundException ex)
        {
            return $"エラー: 必要なファイルが見つかりません。{ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"エラー: {ex.Message}";
        }
        catch (TimeoutException ex)
        {
            return $"エラー: タイムアウト。{ex.Message}";
        }
        catch (Exception ex)
        {
            return $"エラー: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
