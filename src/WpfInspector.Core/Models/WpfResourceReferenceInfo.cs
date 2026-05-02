namespace WpfInspector.Core.Models;

public sealed record WpfResourceReferenceInfo(
    string PropertyName,
    string ResolutionKind,
    string? ResourceKey,
    string? SourceScope,
    string? SourceDescription,
    bool IsDynamic,
    bool IsExpression,
    bool IsImplicit,
    string? ResolvedValueTypeName,
    string? ResolvedValueSummary)
{
    public string? Notes { get; init; }
}
