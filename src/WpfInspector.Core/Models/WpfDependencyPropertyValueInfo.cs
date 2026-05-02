namespace WpfInspector.Core.Models;

public sealed record WpfDependencyPropertyValueInfo(
    string PropertyName,
    string OwnerTypeName,
    string? ValueTypeName,
    string? ValueSummary,
    string BaseValueSource,
    bool IsExpression,
    bool IsAnimated,
    bool IsCoerced,
    bool IsCurrent,
    bool IsDefaultValue,
    bool IsLocallySet,
    string? ReferencedElementStableId,
    WpfBindingInfo? Binding,
    WpfResourceReferenceInfo? Resource);
