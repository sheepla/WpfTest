using WpfInspector.Core.Models;

namespace WpfInspector.Core;

internal static class WpfModuleDetection
{
    internal static bool IsWpfProcess(IReadOnlyList<ProcessModuleInfo> modules) =>
        FindRuntimeModule(modules) is not null;

    internal static ProcessModuleInfo? FindRuntimeModule(IReadOnlyList<ProcessModuleInfo> modules) =>
        modules.FirstOrDefault(module => IsPrimaryWpfRuntimeModule(module.ModuleName))
        ?? modules.FirstOrDefault(module => IsSecondaryWpfRuntimeModule(module.ModuleName));

    private static bool IsPrimaryWpfRuntimeModule(string moduleName)
    {
        var fileName = Path.GetFileName(moduleName);
        return fileName.StartsWith("PresentationFramework", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("PresentationCore", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSecondaryWpfRuntimeModule(string moduleName)
    {
        var fileName = Path.GetFileName(moduleName);
        return fileName.StartsWith("wpfgfx", StringComparison.OrdinalIgnoreCase);
    }
}
