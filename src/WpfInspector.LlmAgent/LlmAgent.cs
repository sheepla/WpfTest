using System.Text.Json.Nodes;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using AiTool = Anthropic.SDK.Common.Tool;
using AiFunction = Anthropic.SDK.Common.Function;

namespace WpfInspector.LlmAgent;

/// <summary>
/// Claude API（tool_use）を使って WPF アプリを自律操作するエージェントループ。
/// GET_TREE でビジュアルツリーを取得し、Claude のコマンドを TestHelper 経由で実行する。
/// </summary>
internal sealed class LlmAgentRunner
{
    private readonly WpfPipeClient _pipe;
    private readonly AnthropicClient _claude;
    private const int MaxTurns = 20;

    private const string SystemPrompt = """
        あなたは WPF デスクトップアプリを操作するエージェントです。
        提供されたツールを使ってユーザーのタスクを完了してください。
        操作前後に get_ui_state で UI 状態を確認し、正確に操作してください。
        タスクが完了したら必ず done ツールを呼び出してください。
        要素識別子には Name、AutomationId、stableId を使えます。
        """;

    internal LlmAgentRunner(WpfPipeClient pipe, string apiKey)
    {
        _pipe = pipe;
        _claude = new AnthropicClient(apiKey);
    }

    internal async Task RunAsync(string task)
    {
        var tools = BuildTools();
        var messages = new List<Message>();

        // 初期 UI 状態を取得してタスクと一緒に渡す
        var initialTree = await _pipe.SendCommandAsync("GET_TREE");
        messages.Add(new Message(RoleType.User, $"タスク: {task}\n\n現在のUI状態:\n{initialTree}", null));

        for (int turn = 0; turn < MaxTurns; turn++)
        {
            var response = await _claude.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model = "claude-sonnet-4-6",
                MaxTokens = 4096,
                System = new List<SystemMessage> { new SystemMessage(SystemPrompt, null) },
                Tools = tools,
                Messages = messages
            });

            // アシスタントメッセージを会話履歴に追加
            messages.Add(response.Message);

            bool done = false;

            if (response.ToolCalls is not { Count: > 0 })
                break;

            foreach (var toolCall in response.ToolCalls)
            {
                if (toolCall.Name == "done")
                {
                    var summary = toolCall.Arguments?["summary"]?.GetValue<string>() ?? "";
                    Console.WriteLine($"[完了] {summary}");
                    done = true;
                    messages.Add(new Message(toolCall, "OK", false, null));
                    break;
                }

                var result = await ExecuteToolAsync(toolCall);
                var preview = result.Length > 120 ? result[..120] + "..." : result;
                Console.WriteLine($"[{toolCall.Name}] → {preview}");
                messages.Add(new Message(toolCall, result, false, null));
            }

            if (done) break;
        }
    }

    private async Task<string> ExecuteToolAsync(AiFunction toolCall)
    {
        try
        {
            var name = toolCall.Arguments?["name"]?.GetValue<string>() ?? "";
            var text = toolCall.Arguments?["text"]?.GetValue<string>() ?? "";

            return toolCall.Name switch
            {
                "click" => await _pipe.SendCommandAsync($"CLICK:{name}"),
                "type_text" => await _pipe.SendCommandAsync($"TYPE:{name}:{text}"),
                "get_text" => await _pipe.SendCommandAsync($"GET_TEXT:{name}"),
                "get_ui_state" => await _pipe.SendCommandAsync("GET_TREE"),
                _ => "ERROR:Unknown tool"
            };
        }
        catch (Exception ex)
        {
            return $"ERROR:{ex.Message}";
        }
    }

    private static List<AiTool> BuildTools()
    {
        var clickSchema = JsonNode.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string", "description": "ボタンの Name / AutomationId / stableId" }
                },
                "required": ["name"]
            }
            """)!;

        var typeTextSchema = JsonNode.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string", "description": "TextBox の Name / AutomationId / stableId" },
                    "text": { "type": "string", "description": "入力するテキスト" }
                },
                "required": ["name", "text"]
            }
            """)!;

        var getTextSchema = JsonNode.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": { "type": "string", "description": "要素の Name / AutomationId / stableId" }
                },
                "required": ["name"]
            }
            """)!;

        var getUiStateSchema = JsonNode.Parse("""
            { "type": "object", "properties": {} }
            """)!;

        var doneSchema = JsonNode.Parse("""
            {
                "type": "object",
                "properties": {
                    "summary": { "type": "string", "description": "完了の要約" }
                },
                "required": ["summary"]
            }
            """)!;

        return
        [
            new AiFunction("click", "WPF ウィンドウ上のボタンをクリックする", clickSchema),
            new AiFunction("type_text", "テキストボックスに文字を入力する", typeTextSchema),
            new AiFunction("get_text", "テキストボックスまたはテキストブロックの内容を取得する", getTextSchema),
            new AiFunction("get_ui_state", "現在の UI 状態（ビジュアルツリー JSON）を取得する", getUiStateSchema),
            new AiFunction("done", "タスクが完了したことを宣言する", doneSchema),
        ];
    }
}
