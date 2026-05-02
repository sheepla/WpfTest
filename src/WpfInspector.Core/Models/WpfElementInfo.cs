namespace WpfInspector.Core.Models;

public sealed record WpfElementInfo(
    string TypeName,
    string Name,
    string? AutomationId,
    string? Text,
    string? DataContextTypeName,
    bool IsVisible,
    bool IsEnabled,
    double Width,
    double Height,
    List<WpfElementInfo> Children)
{
    public string SchemaVersion { get; init; } = WpfInspectionSchema.CurrentVersion;

    public string? StableId { get; init; }

    public string? FullTypeName { get; init; }

    public string? VisualPath { get; init; }

    public string? LogicalPath { get; init; }

    public string? ParentStableId { get; init; }

    public string? VisualParentStableId { get; init; }

    public string? LogicalParentStableId { get; init; }

    public List<WpfElementReference> ParentChain { get; init; } = [];

    public WpfElementReference? TemplatedParent { get; init; }

    public string? OwnerWindowStableId { get; init; }

    public string? WindowHandle { get; init; }

    public bool IsLoaded { get; init; }

    public bool IsHitTestVisible { get; init; }

    public bool Focusable { get; init; }

    public bool IsFocused { get; init; }

    public bool IsKeyboardFocused { get; init; }

    public bool IsKeyboardFocusWithin { get; init; }

    public bool? IsTabStop { get; init; }

    public bool IsMouseOver { get; init; }

    public string? DataContextValueSummary { get; init; }

    public string? XamlSourceUri { get; init; }

    public int VisualChildCount { get; init; }

    public int LogicalChildCount { get; init; }

    public List<WpfChildCollectionInfo> ChildCollections { get; init; } = [];

    public List<WpfDependencyPropertyValueInfo> ObservedProperties { get; init; } = [];

    public WpfValidationInfo? Validation { get; init; }

    public List<WpfResourceDictionaryInfo> ResourceDictionaries { get; init; } = [];

    public List<WpfResourceReferenceInfo> ResourceReferences { get; init; } = [];

    public WpfItemsControlInfo? ItemsControl { get; init; }

    public WpfCommandInfo? Command { get; init; }

    public List<WpfCodeReferenceInfo> CodeReferences { get; init; } = [];

    public WpfInteropBoundaryInfo? InteropBoundary { get; init; }

    public bool IsReference { get; init; }

    public string? ReferenceReason { get; init; }
}
