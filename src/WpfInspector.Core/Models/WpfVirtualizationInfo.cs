namespace WpfInspector.Core.Models;

public sealed record WpfVirtualizationInfo(
    bool IsVirtualizing,
    bool IsVirtualizingWhenGrouping,
    string? VirtualizationMode,
    string? ScrollUnit,
    string? CacheLength,
    string? CacheLengthUnit,
    bool? CanContentScroll);
