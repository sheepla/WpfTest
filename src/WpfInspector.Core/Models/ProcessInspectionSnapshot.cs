namespace WpfInspector.Core.Models;

public sealed record ProcessInspectionSnapshot(
    ProcessSummary Summary,
    string? MainModulePath,
    DateTime? StartTime,
    long WorkingSetBytes,
    IReadOnlyList<ProcessModuleInfo> Modules,
    IReadOnlyList<string> Notes);

