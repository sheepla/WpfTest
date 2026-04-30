namespace WpfInspector.Core.Models;

public sealed record WpfElementInfo(
    string TypeName,
    string Name,
    string? AutomationId,
    string? Text,
    string? DataContextTypeName,
    bool IsVisible,
    bool IsEnabled,
    double Width,
    double Height,
    List<WpfElementInfo> Children);
