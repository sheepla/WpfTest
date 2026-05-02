namespace WpfInspector.Core.Models;

public sealed record WpfResourceDictionaryInfo(
    string Scope,
    string OwnerTypeName,
    string? Source,
    int KeyCount,
    int MergedDictionaryCount);
