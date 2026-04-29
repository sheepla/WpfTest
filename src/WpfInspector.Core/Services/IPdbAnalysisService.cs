using WpfInspector.Core.Models;

namespace WpfInspector.Core.Services;

public interface IPdbAnalysisService
{
    PdbAnalysisResult Analyze(string pdbPath, string? assemblyPath = null);
}

