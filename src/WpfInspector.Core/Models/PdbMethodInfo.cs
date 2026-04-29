namespace WpfInspector.Core.Models;

public sealed record PdbMethodInfo(
    int Token,
    string DisplayName,
    string? Document,
    int SequencePointCount,
    int VisibleSequencePointCount);

