namespace WpfInspector.Core.Models;

public sealed record WpfItemsControlInfo(
    bool HasItems,
    int ItemCount,
    int RealizedItemCount,
    int UnrealizedItemCount,
    string? ItemsSourceTypeName,
    string? GeneratorStatus,
    List<WpfItemContainerInfo> ItemMappings,
    WpfVirtualizationInfo? Virtualization);
