using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WpfInspector.Core.Services;

namespace WpfInspector.McpServer.Tools;

[McpServerToolType]
internal sealed class PdbTools(IPdbAnalysisService pdbService)
{
    [McpServerTool(Name = "analyze_pdb"), Description("Portable PDBファイルを解析してデバッグ情報を返す")]
    public string AnalyzePdb(
        [Description("PDBファイルパス")] string pdbPath,
        [Description("メソッド名解決用アセンブリパス（省略可）")] string? assemblyPath = null)
    {
        try
        {
            var result = pdbService.Analyze(pdbPath, assemblyPath);
            return JsonSerializer.Serialize(result);
        }
        catch (FileNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = $"ファイルが見つかりません: {ex.Message}" });
        }
        catch (NotSupportedException ex)
        {
            return JsonSerializer.Serialize(new { error = $"非対応の PDB 形式です (Classic Windows PDB は非対応): {ex.Message}" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }
}
