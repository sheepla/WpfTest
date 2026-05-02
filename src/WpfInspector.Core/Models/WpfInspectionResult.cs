namespace WpfInspector.Core.Models;

public sealed record WpfInspectionResult(
    int ProcessId,
    string WpfVersion,
    string DispatcherState,
    List<WpfRuntimeAssemblyInfo> RuntimeAssemblies,
    List<WpfWindowInfo> Windows)
{
    public string SchemaVersion { get; init; } = WpfInspectionSchema.CurrentVersion;

    public DateTimeOffset? CollectedAtUtc { get; init; }

    public WpfDispatcherInfo? Dispatcher { get; init; }

    public List<string> Notes { get; init; } = [];
}
