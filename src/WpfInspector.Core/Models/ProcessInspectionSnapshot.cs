namespace WpfInspector.Core.Models;

public sealed record ProcessInspectionSnapshot(
    ProcessSummary Summary,
    string? MainModulePath,
    DateTime? StartTime,
    long WorkingSetBytes,
    IReadOnlyList<ProcessModuleInfo> Modules,
    IReadOnlyList<string> Notes)
{
    public bool IsWpfProcess => WpfModuleDetection.IsWpfProcess(Modules);

    public ProcessModuleInfo? MainModule => Modules.FirstOrDefault(module =>
        string.Equals(module.ModulePath, MainModulePath, StringComparison.OrdinalIgnoreCase));

    public string? MainModuleSuggestedPdbPath => MainModule?.SymbolInfo?.SuggestedPdbPath;

    public bool HasMainModuleSuggestedPdb => MainModule?.SymbolInfo?.SuggestedPdbExists == true;

    public ProcessModuleInfo? ResolvePreferredInitialAnalysisModule()
    {
        var mainModule = MainModule;
        var siblingManagedModule = FindSiblingManagedModule();
        if (mainModule is null)
        {
            return siblingManagedModule;
        }

        if (siblingManagedModule is null)
        {
            return mainModule;
        }

        return ShouldPreferSiblingManagedModule(mainModule, siblingManagedModule)
            ? siblingManagedModule
            : mainModule;
    }

    public ProcessModuleInfo? ResolvePreferredWpfAnalysisModule(WpfInspectionResult? inspectionResult)
    {
        if (inspectionResult is null || inspectionResult.RuntimeAssemblies.Count == 0)
        {
            return ResolvePreferredInitialAnalysisModule();
        }

        var candidateModules = inspectionResult.RuntimeAssemblies
            .Where(candidate => !candidate.IsDynamic)
            .OrderByDescending(candidate => candidate.IsInAppDirectory)
            .ThenBy(candidate => GetDiscoverySourcePriority(candidate.DiscoverySource))
            .ThenBy(candidate => candidate.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .Select(FindModuleForRuntimeAssembly)
            .Where(module => module is not null)
            .DistinctBy(module => module!.ModulePath, StringComparer.OrdinalIgnoreCase)
            .Cast<ProcessModuleInfo>()
            .ToList();

        return candidateModules.FirstOrDefault(module => module.SymbolInfo?.SuggestedPdbExists == true)
            ?? (ResolvePreferredInitialAnalysisModule()?.SymbolInfo?.SuggestedPdbExists == true
                ? ResolvePreferredInitialAnalysisModule()
                : candidateModules.FirstOrDefault() ?? ResolvePreferredInitialAnalysisModule());
    }

    private ProcessModuleInfo? FindModuleForRuntimeAssembly(WpfRuntimeAssemblyInfo runtimeAssembly)
    {
        if (!string.IsNullOrWhiteSpace(runtimeAssembly.AssemblyPath))
        {
            var exactMatch = Modules.FirstOrDefault(module =>
                string.Equals(module.ModulePath, runtimeAssembly.AssemblyPath, StringComparison.OrdinalIgnoreCase));
            if (exactMatch is not null)
            {
                return exactMatch;
            }
        }

        return Modules.FirstOrDefault(module =>
            string.Equals(
                Path.GetFileNameWithoutExtension(module.ModulePath),
                runtimeAssembly.AssemblyName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static int GetDiscoverySourcePriority(string discoverySource) => discoverySource switch
    {
        "Window" => 0,
        "Visual" => 1,
        "DataContext" => 2,
        _ => 3
    };

    private ProcessModuleInfo? FindSiblingManagedModule()
    {
        if (string.IsNullOrWhiteSpace(MainModulePath))
        {
            return null;
        }

        var mainDirectory = Path.GetDirectoryName(MainModulePath);
        var mainBaseName = Path.GetFileNameWithoutExtension(MainModulePath);
        if (string.IsNullOrWhiteSpace(mainDirectory) || string.IsNullOrWhiteSpace(mainBaseName))
        {
            return null;
        }

        return Modules.FirstOrDefault(module =>
            !string.Equals(module.ModulePath, MainModulePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(module.ModulePath), mainDirectory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(module.ModulePath), ".dll", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileNameWithoutExtension(module.ModulePath), mainBaseName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldPreferSiblingManagedModule(ProcessModuleInfo mainModule, ProcessModuleInfo siblingManagedModule)
    {
        if (!string.Equals(Path.GetExtension(mainModule.ModulePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsAppHostLike(mainModule))
        {
            return true;
        }

        return siblingManagedModule.SymbolInfo?.SuggestedPdbExists == true
            && mainModule.SymbolInfo?.SuggestedPdbExists != true;
    }

    private static bool IsAppHostLike(ProcessModuleInfo module)
    {
        var originalPdbName = Path.GetFileName(module.SymbolInfo?.OriginalPdbPath);
        var suggestedPdbName = Path.GetFileName(module.SymbolInfo?.SuggestedPdbPath);
        return string.Equals(originalPdbName, "apphost.pdb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(suggestedPdbName, "apphost.pdb", StringComparison.OrdinalIgnoreCase);
    }
}
