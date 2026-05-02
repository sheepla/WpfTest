namespace WpfInspector.Core.Models;

public sealed record WpfWindowInfo(
    string Title,
    double Width,
    double Height,
    string WindowState,
    WpfElementInfo? RootElement)
{
    public string SchemaVersion { get; init; } = WpfInspectionSchema.CurrentVersion;

    public string? StableId { get; init; }

    public string? TypeName { get; init; }

    public string? FullTypeName { get; init; }

    public string? OwnerWindowStableId { get; init; }

    public List<string> OwnedWindowStableIds { get; init; } = [];

    public string? WindowHandle { get; init; }

    public bool IsVisible { get; init; }

    public bool IsActive { get; init; }

    public bool IsLoaded { get; init; }

    public bool ShowInTaskbar { get; init; }

    public bool Topmost { get; init; }

    public string? ResizeMode { get; init; }

    public string? WindowStyle { get; init; }

    public string? XamlSourceUri { get; init; }

    public int? DispatcherThreadId { get; init; }
}
