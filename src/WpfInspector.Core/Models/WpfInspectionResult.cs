namespace WpfInspector.Core.Models;

public sealed record WpfInspectionResult(
    int ProcessId,
    string WpfVersion,
    string DispatcherState,
    List<WpfRuntimeAssemblyInfo> RuntimeAssemblies,
    List<WpfWindowInfo> Windows);
