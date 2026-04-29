namespace WpfInspector.Core.Models;

public sealed record WpfWindowInfo(
    string Title,
    double Width,
    double Height,
    string WindowState,
    WpfElementInfo? RootElement);
