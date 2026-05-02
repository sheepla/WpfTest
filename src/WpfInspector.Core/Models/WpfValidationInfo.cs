namespace WpfInspector.Core.Models;

public sealed record WpfValidationInfo(
    bool HasError,
    int ErrorCount,
    List<string> Errors,
    string? Summary);
