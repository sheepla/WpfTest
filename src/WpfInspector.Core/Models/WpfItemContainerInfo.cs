namespace WpfInspector.Core.Models;

public sealed record WpfItemContainerInfo(
    int Index,
    string? ItemTypeName,
    string? ItemSummary,
    string? ContainerStableId,
    string? ContainerTypeName,
    bool IsRealized);
