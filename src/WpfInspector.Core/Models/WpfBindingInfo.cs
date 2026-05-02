namespace WpfInspector.Core.Models;

public sealed record WpfBindingInfo(
    string BindingKind,
    string Status,
    string? TargetPropertyName,
    string? Path,
    string? XPath,
    string? ElementName,
    string? RelativeSourceMode,
    string? SourceTypeName,
    string? SourceSummary,
    string? DataItemTypeName,
    string? FallbackValue,
    string? TargetNullValue,
    string? ConverterTypeName,
    string? StringFormat,
    string? Mode,
    string? UpdateSourceTrigger,
    bool HasError,
    List<string> Errors,
    List<WpfBindingInfo> ChildBindings);
