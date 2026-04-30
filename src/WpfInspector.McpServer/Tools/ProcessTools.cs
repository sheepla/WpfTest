using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WpfInspector.Core;
using WpfInspector.Core.Services;

namespace WpfInspector.McpServer.Tools;

[McpServerToolType]
internal sealed class ProcessTools(IProcessInspectionService inspectionService)
{
    [McpServerTool(Name = "list_processes"), Description("実行中プロセスの一覧を取得する")]
    public string ListProcesses(
        [Description("バックグラウンドプロセス（ウィンドウなし）も含める")] bool includeBackground = false)
    {
        var processes = inspectionService.GetProcesses()
            .Where(p => includeBackground || ProcessListFiltering.IsUserFacing(p))
            .ToList();
        return JsonSerializer.Serialize(processes);
    }

    [McpServerTool(Name = "inspect_process"), Description("指定プロセスのモジュール・PDB・WPF情報を取得する")]
    public string InspectProcess([Description("プロセスID")] int pid)
    {
        try
        {
            var snapshot = inspectionService.InspectProcess(pid);
            return JsonSerializer.Serialize(snapshot);
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new { error = $"プロセス {pid} が見つかりません: {ex.Message}" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
