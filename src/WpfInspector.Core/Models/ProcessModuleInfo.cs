namespace WpfInspector.Core.Models;

public sealed record ProcessModuleInfo(
    string ModuleName,
    string ModulePath,
    long BaseAddress,
    int ModuleMemorySize,
    PeSymbolInfo? SymbolInfo);

