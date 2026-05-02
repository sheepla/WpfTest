namespace WpfInspector.Core.Models;

public sealed record WpfCodeReferenceInfo(
    string Kind,
    string? Identifier,
    string? DisplayName,
    string? AssemblyName,
    string? AssemblyPath,
    string? TypeName,
    string? SourceUri,
    string? Notes);
