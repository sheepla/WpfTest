using System.Text.Json.Nodes;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using WpfInspector.Protocol;
using AiTool = Anthropic.SDK.Common.Tool;
using AiFunction = Anthropic.SDK.Common.Function;

namespace WpfInspector.LlmAgent;

/// <summary>
/// Claude API（tool_use）を使って WPF アプリを調査・操作するエージェントループ。
/// UI 操作の語彙は MCP サーバーと揃え、内部では TestHelper のパイプ RPC を使う。
/// </summary>
internal sealed class LlmAgentRunner
{
    private readonly WpfPipeClient _pipe;
    private readonly AnthropicClient _claude;

    private const int MaxTurns = 30;

    private const string SystemPrompt = """
        あなたは WPF デスクトップアプリを調査・操作するエージェントです。
        提供されたツール名と引数は MCP サーバーと同じ語彙です。
        まず get_inspection_result か find_elements で対象候補を把握してください。
        要素指定には stableId を優先し、必要な場合だけ Name や AutomationId を使ってください。
        操作前には get_element_details / get_binding_details / get_command_state を使って状態を確認してください。
        操作後は wait_for_condition か wait_for_idle で変化を待ち、必要なら start_trace / get_trace / diff_snapshots を使って結果を確認してください。
        get_visual_tree は互換用で、返り値は get_inspection_result と同じ完全観測スナップショットです。
        タスクが完了したら必ず done を呼び、summary に確認した内容を簡潔に書いてください。
        """;

    private static readonly AgentToolDefinition[] ToolDefinitions = CreateToolDefinitions();

    internal LlmAgentRunner(WpfPipeClient pipe, string apiKey)
    {
        _pipe = pipe;
        _claude = new AnthropicClient(apiKey);
    }

    internal async Task RunAsync(string task)
    {
        var tools = BuildTools();
        var messages = new List<Message>();
        var initialObservation = await ExecuteRpcAsync("get_inspection_result");

        messages.Add(new Message(
            RoleType.User,
            $"タスク: {task}\n\n接続は確立済みです。初期観測:\n{initialObservation}",
            null));

        for (var turn = 0; turn < MaxTurns; turn++)
        {
            var response = await _claude.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model = "claude-sonnet-4-6",
                MaxTokens = 4096,
                System = new List<SystemMessage> { new SystemMessage(SystemPrompt, null) },
                Tools = tools,
                Messages = messages
            });

            messages.Add(response.Message);

            var done = false;
            if (response.ToolCalls is not { Count: > 0 })
            {
                break;
            }

            foreach (var toolCall in response.ToolCalls)
            {
                if (toolCall.Name == "done")
                {
                    var summary = toolCall.Arguments?["summary"]?.GetValue<string>() ?? string.Empty;
                    Console.WriteLine($"[完了] {summary}");
                    done = true;
                    messages.Add(new Message(toolCall, "OK", false, null));
                    break;
                }

                var result = await ExecuteToolAsync(toolCall);
                var preview = result.Length > 160 ? result[..160] + "..." : result;
                Console.WriteLine($"[{toolCall.Name}] → {preview}");
                messages.Add(new Message(toolCall, result, false, null));
            }

            if (done)
            {
                break;
            }
        }
    }

    private async Task<string> ExecuteToolAsync(AiFunction toolCall)
    {
        try
        {
            var definition = ToolDefinitions.FirstOrDefault(tool => tool.Name == toolCall.Name);
            if (definition is null)
            {
                return "ERROR:Unknown tool";
            }

            var arguments = toolCall.Arguments is JsonObject jsonObject
                ? (JsonObject)jsonObject.DeepClone()
                : [];
            return await definition.ExecuteAsync(this, arguments);
        }
        catch (Exception ex)
        {
            return $"ERROR:{ex.Message}";
        }
    }

    private async Task<string> ExecuteRpcAsync(string operation, JsonObject? arguments = null)
    {
        var response = await _pipe.SendRequestAsync(operation, arguments);
        if (!response.Success)
        {
            return $"ERROR:{response.ErrorCode ?? "rpc_error"}:{response.ErrorMessage}";
        }

        return PipeProtocol.ToJson(response.Data);
    }

    private Task<string> ExecuteLegacyAsync(string command) =>
        _pipe.SendCommandAsync(command);

    private static List<AiTool> BuildTools()
    {
        var tools = ToolDefinitions
            .Select(static tool => (AiTool)new AiFunction(tool.Name, tool.Description, tool.Schema))
            .ToList();
        tools.Add(new AiFunction("done", "タスクが完了したことを宣言する", ParseSchema("""
            {
                "type": "object",
                "properties": {
                    "summary": { "type": "string", "description": "完了の要約" }
                },
                "required": ["summary"]
            }
            """)));
        return tools;
    }

    private static AgentToolDefinition[] CreateToolDefinitions() =>
    [
        new(
            Name: "get_inspection_result",
            Description: "schema 2.0 の完全な WPF 観測スナップショットを取得する",
            Schema: EmptyObjectSchema(),
            ExecuteAsync: static (runner, _) => runner.ExecuteRpcAsync("get_inspection_result")),
        new(
            Name: "get_visual_tree",
            Description: "互換用。get_inspection_result と同じ完全観測スナップショットを取得する",
            Schema: EmptyObjectSchema(),
            ExecuteAsync: static (runner, _) => runner.ExecuteRpcAsync("get_inspection_result")),
        new(
            Name: "find_elements",
            Description: "観測スナップショットから条件に一致する要素を検索する",
            Schema: ParseSchema("""
                {
                    "type": "object",
                    "properties": {
                        "query": { "type": "string", "description": "Name / AutomationId / stableId / Text を横断検索する文字列" },
                        "typeName": { "type": "string", "description": "型名または完全修飾型名" },
                        "propertyName": { "type": "string", "description": "ObservedProperties 内の DependencyProperty 名" },
                        "bindingPath": { "type": "string", "description": "Binding.Path / XPath" },
                        "ownerWindowTitle": { "type": "string", "description": "所有ウィンドウのタイトル" },
                        "isVisible": { "type": "boolean", "description": "表示状態で絞り込む" },
                        "isEnabled": { "type": "boolean", "description": "有効状態で絞り込む" },
                        "hasValidationError": { "type": "boolean", "description": "Validation エラーの有無で絞り込む" },
                        "hasCommand": { "type": "boolean", "description": "Command の有無で絞り込む" },
                        "includeReferences": { "type": "boolean", "description": "IsReference ノードも含める" },
                        "matchMode": { "type": "string", "description": "equals / contains / starts_with / ends_with" },
                        "maxResults": { "type": "integer", "description": "最大返却件数。既定 25" }
                    }
                }
                """),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteRpcAsync(
                "find_elements",
                CopyArguments(arguments,
                    "query",
                    "typeName",
                    "propertyName",
                    "bindingPath",
                    "ownerWindowTitle",
                    "isVisible",
                    "isEnabled",
                    "hasValidationError",
                    "hasCommand",
                    "includeReferences",
                    "matchMode",
                    "maxResults"))),
        new(
            Name: "get_window_title",
            Description: "メインウィンドウのタイトルを取得する",
            Schema: EmptyObjectSchema(),
            ExecuteAsync: static (runner, _) => runner.ExecuteLegacyAsync("GET_TITLE")),
        new(
            Name: "get_element_text",
            Description: "要素識別子で指定した要素のテキストを取得する",
            Schema: SingleStringArgumentSchema("elementName", "要素識別子(Name / AutomationId / stableId)"),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteLegacyAsync(
                $"GET_TEXT:{GetRequiredString(arguments, "elementName")}")),
        new(
            Name: "click_button",
            Description: "名前または Content 文字列でボタンをクリックする",
            Schema: SingleStringArgumentSchema("buttonName", "ボタン名または Content 文字列"),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteLegacyAsync(
                $"CLICK:{GetRequiredString(arguments, "buttonName")}")),
        new(
            Name: "type_text",
            Description: "TextBox にテキストを入力する",
            Schema: ParseSchema("""
                {
                    "type": "object",
                    "properties": {
                        "elementName": { "type": "string", "description": "TextBox の Name / AutomationId / stableId" },
                        "text": { "type": "string", "description": "入力するテキスト" }
                    },
                    "required": ["elementName", "text"]
                }
                """),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteLegacyAsync(
                $"TYPE:{GetRequiredString(arguments, "elementName")}:{GetRequiredString(arguments, "text")}")),
        new(
            Name: "get_data_grid_item_count",
            Description: "DataGrid の行数を取得する",
            Schema: SingleStringArgumentSchema("gridName", "DataGrid の要素名"),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteLegacyAsync(
                $"GET_ITEMS:{GetRequiredString(arguments, "gridName")}")),
        new(
            Name: "get_element_details",
            Description: "要素識別子で指定した要素の詳細情報を取得する",
            Schema: SingleStringArgumentSchema("elementName", "要素識別子(Name / AutomationId / stableId)"),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteRpcAsync(
                "get_element_details",
                CopyArguments(arguments, "elementName"))),
        new(
            Name: "get_binding_details",
            Description: "要素に設定されたバインディング詳細を取得する",
            Schema: ParseSchema("""
                {
                    "type": "object",
                    "properties": {
                        "elementName": { "type": "string", "description": "要素識別子(Name / AutomationId / stableId)" },
                        "propertyName": { "type": "string", "description": "対象プロパティ名。省略時はローカル値に設定された Binding を列挙" }
                    },
                    "required": ["elementName"]
                }
                """),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteRpcAsync(
                "get_binding_details",
                CopyArguments(arguments, "elementName", "propertyName"))),
        new(
            Name: "get_command_state",
            Description: "要素の command 状態を取得する",
            Schema: SingleStringArgumentSchema("elementName", "要素識別子(Name / AutomationId / stableId)"),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteRpcAsync(
                "get_command_state",
                CopyArguments(arguments, "elementName"))),
        new(
            Name: "list_popups",
            Description: "現在の Popup / ContextMenu / ToolTip を列挙する",
            Schema: EmptyObjectSchema(),
            ExecuteAsync: static (runner, _) => runner.ExecuteRpcAsync("list_popups")),
        new(
            Name: "focus",
            Description: "要素へキーボードフォーカスを移す",
            Schema: SingleStringArgumentSchema("elementName", "要素識別子(Name / AutomationId / stableId)"),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteRpcAsync(
                "focus",
                CopyArguments(arguments, "elementName"))),
        new(
            Name: "scroll_into_view",
            Description: "要素を表示領域へスクロールする",
            Schema: SingleStringArgumentSchema("elementName", "要素識別子(Name / AutomationId / stableId)"),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteRpcAsync(
                "scroll_into_view",
                CopyArguments(arguments, "elementName"))),
        new(
            Name: "select_item",
            Description: "Selector 系要素の項目を選択する",
            Schema: ParseSchema("""
                {
                    "type": "object",
                    "properties": {
                        "elementName": { "type": "string", "description": "要素識別子(Name / AutomationId / stableId)" },
                        "index": { "type": "integer", "description": "選択する index" },
                        "itemText": { "type": "string", "description": "選択する項目の表示値" }
                    },
                    "required": ["elementName"]
                }
                """),
            ExecuteAsync: static (runner, arguments) =>
            {
                EnsureSelectItemArguments(arguments);
                return runner.ExecuteRpcAsync("select_item", CopyArguments(arguments, "elementName", "index", "itemText"));
            }),
        new(
            Name: "expand",
            Description: "Expander / TreeViewItem / MenuItem を展開する",
            Schema: SingleStringArgumentSchema("elementName", "要素識別子(Name / AutomationId / stableId)"),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteRpcAsync(
                "expand",
                CopyArguments(arguments, "elementName"))),
        new(
            Name: "collapse",
            Description: "Expander / TreeViewItem / MenuItem を折りたたむ",
            Schema: SingleStringArgumentSchema("elementName", "要素識別子(Name / AutomationId / stableId)"),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteRpcAsync(
                "collapse",
                CopyArguments(arguments, "elementName"))),
        new(
            Name: "invoke_command",
            Description: "要素に紐づく command を実行する",
            Schema: SingleStringArgumentSchema("elementName", "要素識別子(Name / AutomationId / stableId)"),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteRpcAsync(
                "invoke_command",
                CopyArguments(arguments, "elementName"))),
        new(
            Name: "wait_for_idle",
            Description: "Dispatcher が ApplicationIdle に達するまで待機する",
            Schema: ParseSchema("""
                {
                    "type": "object",
                    "properties": {
                        "timeoutMs": { "type": "integer", "description": "待機タイムアウト(ms)。既定 1000" }
                    }
                }
                """),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteRpcAsync(
                "wait_for_idle",
                CopyArguments(arguments, "timeoutMs"))),
        new(
            Name: "wait_for_condition",
            Description: "要素の状態が期待条件を満たすまで待機する",
            Schema: ParseSchema("""
                {
                    "type": "object",
                    "properties": {
                        "elementName": { "type": "string", "description": "要素識別子(Name / AutomationId / stableId)" },
                        "propertyName": { "type": "string", "description": "監視するプロパティ名。例: Text, IsEnabled, StableId, exists" },
                        "expectedValue": { "type": "string", "description": "期待値。exists 以外では通常必須" },
                        "matchMode": { "type": "string", "description": "equals / not_equals / contains / starts_with / ends_with / exists" },
                        "timeoutMs": { "type": "integer", "description": "待機タイムアウト(ms)。既定 2000" },
                        "pollIntervalMs": { "type": "integer", "description": "ポーリング間隔(ms)。既定 100" }
                    },
                    "required": ["elementName", "propertyName"]
                }
                """),
            ExecuteAsync: static (runner, arguments) => runner.ExecuteRpcAsync(
                "wait_for_condition",
                CopyArguments(arguments, "elementName", "propertyName", "expectedValue", "matchMode", "timeoutMs", "pollIntervalMs"))),
        new(
            Name: "start_trace",
            Description: "簡易 trace セッションを開始する",
            Schema: EmptyObjectSchema(),
            ExecuteAsync: static (runner, _) => runner.ExecuteRpcAsync("start_trace")),
        new(
            Name: "stop_trace",
            Description: "簡易 trace セッションを停止する",
            Schema: EmptyObjectSchema(),
            ExecuteAsync: static (runner, _) => runner.ExecuteRpcAsync("stop_trace")),
        new(
            Name: "get_trace",
            Description: "簡易 trace セッションの状態を取得する",
            Schema: EmptyObjectSchema(),
            ExecuteAsync: static (runner, _) => runner.ExecuteRpcAsync("get_trace")),
        new(
            Name: "diff_snapshots",
            Description: "trace 開始/終了時点の snapshot 差分を取得する",
            Schema: EmptyObjectSchema(),
            ExecuteAsync: static (runner, _) => runner.ExecuteRpcAsync("diff_snapshots"))
    ];

    private static JsonNode EmptyObjectSchema() => ParseSchema("""
        { "type": "object", "properties": {} }
        """);

    private static JsonNode SingleStringArgumentSchema(string argumentName, string description) => ParseSchema($$"""
        {
            "type": "object",
            "properties": {
                "{{argumentName}}": { "type": "string", "description": "{{description}}" }
            },
            "required": ["{{argumentName}}"]
        }
        """);

    private static JsonNode ParseSchema(string json) =>
        JsonNode.Parse(json) ?? throw new InvalidOperationException("Failed to parse tool schema.");

    private static JsonObject CopyArguments(JsonObject source, params string[] argumentNames)
    {
        var result = new JsonObject();
        foreach (var argumentName in argumentNames)
        {
            if (source.TryGetPropertyValue(argumentName, out var value) && value is not null)
            {
                result[argumentName] = value.DeepClone();
            }
        }

        return result;
    }

    private static string GetRequiredString(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out var value) ||
            value is null ||
            value.GetValue<string>() is not { } stringValue ||
            string.IsNullOrWhiteSpace(stringValue))
        {
            throw new InvalidOperationException($"Missing required argument '{name}'.");
        }

        return stringValue;
    }

    private static void EnsureSelectItemArguments(JsonObject arguments)
    {
        _ = GetRequiredString(arguments, "elementName");
        var hasIndex = arguments.TryGetPropertyValue("index", out var indexNode) && indexNode is not null;
        var hasItemText = arguments.TryGetPropertyValue("itemText", out var itemTextNode)
            && itemTextNode is not null
            && !string.IsNullOrWhiteSpace(itemTextNode.GetValue<string>());

        if (!hasIndex && !hasItemText)
        {
            throw new InvalidOperationException("select_item requires either 'index' or 'itemText'.");
        }
    }

    private sealed record AgentToolDefinition(
        string Name,
        string Description,
        JsonNode Schema,
        Func<LlmAgentRunner, JsonObject, Task<string>> ExecuteAsync);
}
