using System.ComponentModel;
using ModelContextProtocol.Server;
using WpfInspector.McpServer.Session;

namespace WpfInspector.McpServer.Tools;

[McpServerToolType]
internal sealed class InjectionTools(WpfSessionManager sessionManager)
{
    [McpServerTool(Name = "inject_and_connect"), Description("WPFプロセスにTestHelperを注入してパイプ接続を確立する")]
    public async Task<string> InjectAndConnectAsync([Description("対象プロセスID")] int pid, CancellationToken ct)
    {
        try
        {
            await sessionManager.GetOrCreateSessionAsync(pid, ct);
            return $"PID {pid} への接続を確立しました。";
        }
        catch (FileNotFoundException ex)
        {
            return $"エラー: 必要なファイルが見つかりません。{ex.Message}";
        }
        catch (TimeoutException ex)
        {
            return $"エラー: タイムアウト。{ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"エラー: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"エラー: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "disconnect"), Description("指定プロセスとのパイプ接続を切断する")]
    public async Task<string> DisconnectAsync([Description("対象プロセスID")] int pid)
    {
        var removed = await sessionManager.DisconnectAsync(pid);
        return removed
            ? $"PID {pid} との接続を切断しました。"
            : $"PID {pid} のアクティブなセッションが見つかりませんでした。";
    }
}
