namespace WpfInspector.Core.Models;

public sealed record WpfElementInfo(
    string TypeName,
    string Name,
    string? DataContextTypeName,
    bool IsVisible,
    double Width,
    double Height,
    List<WpfElementInfo> Children);
