namespace WpfInspector.Core.Models;

public sealed record PdbDocumentInfo(
    string Name,
    string HashAlgorithm,
    int HashLength,
    string Language);

