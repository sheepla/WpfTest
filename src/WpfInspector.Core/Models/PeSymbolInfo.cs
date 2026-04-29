namespace WpfInspector.Core.Models;

public sealed record PeSymbolInfo(
    string? OriginalPdbPath,
    string? SuggestedPdbPath,
    bool SuggestedPdbExists,
    Guid? Signature,
    int? Age,
    bool HasEmbeddedPortablePdb,
    string SymbolState,
    string? Architecture);

