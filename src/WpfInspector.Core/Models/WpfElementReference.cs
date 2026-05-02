namespace WpfInspector.Core.Models;

public sealed record WpfElementReference(
    string StableId,
    string TypeName,
    string? Name,
    string? AutomationId,
    string Relationship,
    string? Path);
