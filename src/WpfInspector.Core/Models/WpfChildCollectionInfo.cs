namespace WpfInspector.Core.Models;

public sealed record WpfChildCollectionInfo(
    string Kind,
    int TotalCount,
    int ReturnedCount,
    int OmittedCount,
    bool WasTruncated,
    bool CanEnumerateMore,
    List<WpfElementReference> ChildReferences,
    string? Notes);
