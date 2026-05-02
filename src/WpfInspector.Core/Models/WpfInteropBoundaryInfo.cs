namespace WpfInspector.Core.Models;

public sealed record WpfInteropBoundaryInfo(
    string BoundaryKind,
    bool IsExternalBoundary,
    string? Handle,
    string? ExternalTechnology,
    string? Notes);
