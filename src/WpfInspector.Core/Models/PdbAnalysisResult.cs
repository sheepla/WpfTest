namespace WpfInspector.Core.Models;

public sealed record PdbAnalysisResult(
    string PdbPath,
    string? AssemblyPath,
    string Format,
    int DocumentCount,
    int MethodCount,
    int SequencePointCount,
    int VisibleSequencePointCount,
    IReadOnlyList<PdbDocumentInfo> Documents,
    IReadOnlyList<PdbMethodInfo> Methods,
    IReadOnlyList<string> Warnings);

