using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using WpfInspector.McpServer.Session;
using WpfInspector.Protocol;

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

    [McpServerTool(Name = "get_element_text"), Description("要素識別子で指定した要素のテキストを取得する")]
    public async Task<string> GetElementTextAsync(
        [Description("対象プロセスID")] int pid,
        [Description("要素識別子(Name属性/AutomationId/stableId)")] string elementName,
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

    [McpServerTool(Name = "get_element_details"), Description("要素識別子で指定した要素の詳細情報を取得する")]
    public async Task<string> GetElementDetailsAsync(
        [Description("対象プロセスID")] int pid,
        [Description("要素識別子(Name属性/AutomationId/stableId)")] string elementName,
        CancellationToken ct)
        => await SendRequestAsync(
            pid,
            "get_element_details",
            new JsonObject
            {
                ["elementName"] = elementName
            },
            ct);

    [McpServerTool(Name = "get_binding_details"), Description("要素に設定されたバインディング詳細を取得する")]
    public async Task<string> GetBindingDetailsAsync(
        [Description("対象プロセスID")] int pid,
        [Description("要素識別子(Name属性/AutomationId/stableId)")] string elementName,
        [Description("対象プロパティ名。省略時はローカル値に設定されたBindingを列挙")] string? propertyName,
        CancellationToken ct)
    {
        var arguments = new JsonObject
        {
            ["elementName"] = elementName
        };
        if (!string.IsNullOrWhiteSpace(propertyName))
            arguments["propertyName"] = propertyName;

        return await SendRequestAsync(pid, "get_binding_details", arguments, ct);
    }

    [McpServerTool(Name = "get_command_state"), Description("要素の command 状態を取得する")]
    public async Task<string> GetCommandStateAsync(
        [Description("対象プロセスID")] int pid,
        [Description("要素識別子(Name属性/AutomationId/stableId)")] string elementName,
        CancellationToken ct)
        => await SendRequestAsync(
            pid,
            "get_command_state",
            new JsonObject
            {
                ["elementName"] = elementName
            },
            ct);

    [McpServerTool(Name = "list_popups"), Description("現在の Popup / ContextMenu / ToolTip を列挙する")]
    public async Task<string> ListPopupsAsync(
        [Description("対象プロセスID")] int pid,
        CancellationToken ct)
        => await SendRequestAsync(pid, "list_popups", new JsonObject(), ct);

    [McpServerTool(Name = "focus"), Description("要素識別子で指定した要素へキーボードフォーカスを移す")]
    public async Task<string> FocusAsync(
        [Description("対象プロセスID")] int pid,
        [Description("要素識別子(Name属性/AutomationId/stableId)")] string elementName,
        CancellationToken ct)
        => await SendRequestAsync(
            pid,
            "focus",
            new JsonObject
            {
                ["elementName"] = elementName
            },
            ct);

    [McpServerTool(Name = "scroll_into_view"), Description("要素識別子で指定した要素を表示領域へスクロールする")]
    public async Task<string> ScrollIntoViewAsync(
        [Description("対象プロセスID")] int pid,
        [Description("要素識別子(Name属性/AutomationId/stableId)")] string elementName,
        CancellationToken ct)
        => await SendRequestAsync(
            pid,
            "scroll_into_view",
            new JsonObject
            {
                ["elementName"] = elementName
            },
            ct);

    [McpServerTool(Name = "select_item"), Description("Selector 系要素の項目を選択する")]
    public async Task<string> SelectItemAsync(
        [Description("対象プロセスID")] int pid,
        [Description("要素識別子(Name属性/AutomationId/stableId)")] string elementName,
        [Description("選択する index。未指定時は item_text を使う")] int? index,
        [Description("選択する項目の表示値。未指定時は index を使う")] string? itemText,
        CancellationToken ct)
    {
        var arguments = new JsonObject
        {
            ["elementName"] = elementName
        };
        if (index is not null)
            arguments["index"] = index.Value;
        if (!string.IsNullOrWhiteSpace(itemText))
            arguments["itemText"] = itemText;

        return await SendRequestAsync(pid, "select_item", arguments, ct);
    }

    [McpServerTool(Name = "expand"), Description("Expander / TreeViewItem / MenuItem を展開する")]
    public async Task<string> ExpandAsync(
        [Description("対象プロセスID")] int pid,
        [Description("要素識別子(Name属性/AutomationId/stableId)")] string elementName,
        CancellationToken ct)
        => await SendRequestAsync(
            pid,
            "expand",
            new JsonObject
            {
                ["elementName"] = elementName
            },
            ct);

    [McpServerTool(Name = "collapse"), Description("Expander / TreeViewItem / MenuItem を折りたたむ")]
    public async Task<string> CollapseAsync(
        [Description("対象プロセスID")] int pid,
        [Description("要素識別子(Name属性/AutomationId/stableId)")] string elementName,
        CancellationToken ct)
        => await SendRequestAsync(
            pid,
            "collapse",
            new JsonObject
            {
                ["elementName"] = elementName
            },
            ct);

    [McpServerTool(Name = "invoke_command"), Description("要素に紐づく command を実行する")]
    public async Task<string> InvokeCommandAsync(
        [Description("対象プロセスID")] int pid,
        [Description("要素識別子(Name属性/AutomationId/stableId)")] string elementName,
        CancellationToken ct)
        => await SendRequestAsync(
            pid,
            "invoke_command",
            new JsonObject
            {
                ["elementName"] = elementName
            },
            ct);

    [McpServerTool(Name = "wait_for_idle"), Description("Dispatcher が ApplicationIdle に達するまで待機する")]
    public async Task<string> WaitForIdleAsync(
        [Description("対象プロセスID")] int pid,
        [Description("待機タイムアウト(ms)")] int timeoutMs,
        CancellationToken ct)
        => await SendRequestAsync(
            pid,
            "wait_for_idle",
            new JsonObject
            {
                ["timeoutMs"] = timeoutMs
            },
            ct);

    [McpServerTool(Name = "wait_for_condition"), Description("要素の状態が期待条件を満たすまで待機する")]
    public async Task<string> WaitForConditionAsync(
        [Description("対象プロセスID")] int pid,
        [Description("要素識別子(Name属性/AutomationId/stableId)")] string elementName,
        [Description("監視するプロパティ名。例: Text, IsEnabled, StableId, exists")] string propertyName,
        [Description("期待値。exists 以外では通常必須")] string? expectedValue,
        [Description("比較方法。equals/not_equals/contains/starts_with/ends_with/exists")] string? matchMode,
        [Description("待機タイムアウト(ms)。既定 2000")] int? timeoutMs,
        [Description("ポーリング間隔(ms)。既定 100")] int? pollIntervalMs,
        CancellationToken ct)
    {
        var arguments = new JsonObject
        {
            ["elementName"] = elementName,
            ["propertyName"] = propertyName
        };
        if (expectedValue is not null)
            arguments["expectedValue"] = expectedValue;
        if (!string.IsNullOrWhiteSpace(matchMode))
            arguments["matchMode"] = matchMode;
        if (timeoutMs is not null)
            arguments["timeoutMs"] = timeoutMs.Value;
        if (pollIntervalMs is not null)
            arguments["pollIntervalMs"] = pollIntervalMs.Value;

        return await SendRequestAsync(pid, "wait_for_condition", arguments, ct);
    }

    [McpServerTool(Name = "start_trace"), Description("簡易 trace セッションを開始する")]
    public async Task<string> StartTraceAsync(
        [Description("対象プロセスID")] int pid,
        CancellationToken ct)
        => await SendRequestAsync(pid, "start_trace", new JsonObject(), ct);

    [McpServerTool(Name = "stop_trace"), Description("簡易 trace セッションを停止する")]
    public async Task<string> StopTraceAsync(
        [Description("対象プロセスID")] int pid,
        CancellationToken ct)
        => await SendRequestAsync(pid, "stop_trace", new JsonObject(), ct);

    [McpServerTool(Name = "get_trace"), Description("簡易 trace セッションの状態を取得する")]
    public async Task<string> GetTraceAsync(
        [Description("対象プロセスID")] int pid,
        CancellationToken ct)
        => await SendRequestAsync(pid, "get_trace", new JsonObject(), ct);

    [McpServerTool(Name = "diff_snapshots"), Description("trace 開始/終了時点の snapshot 差分を取得する")]
    public async Task<string> DiffSnapshotsAsync(
        [Description("対象プロセスID")] int pid,
        CancellationToken ct)
        => await SendRequestAsync(pid, "diff_snapshots", new JsonObject(), ct);

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

    private async Task<string> SendRequestAsync(int pid, string operation, JsonObject arguments, CancellationToken ct)
    {
        try
        {
            var session = await sessionManager.GetOrCreateSessionAsync(pid, ct);
            var response = await session.SendRequestAsync(operation, arguments, ct);
            if (!response.Success)
                return $"エラー: {response.ErrorCode ?? "rpc_error"}: {response.ErrorMessage}";

            return PipeProtocol.ToJson(response.Data);
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
