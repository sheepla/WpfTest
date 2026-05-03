using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using WpfInspector.Core.Models;

namespace WpfInspector.InspectionPayload;

public static class Inspector
{
    private const int MaxDepth = 20;
    private const int MaxChildren = 40;

    private sealed record ResourceDictionaryScope(
        string Scope,
        string OwnerTypeName,
        ResourceDictionary Dictionary,
        string? Description);

    private sealed record ResourceLookupResult(
        object? Key,
        string Scope,
        string? SourceDescription,
        string? Notes);

    public static string CollectJson(int processId)
    {
        var result = CollectResult(processId);
        return JsonSerializer.Serialize(result, WpfInspectionJsonContext.Default.WpfInspectionResult);
    }

    internal static WpfInspectionResult CollectResult(int processId)
    {
        var windows = new List<WpfWindowInfo>();
        var runtimeAssemblies = new List<WpfRuntimeAssemblyInfo>();
        string wpfVersion = string.Empty;
        string dispatcherState = string.Empty;
        WpfDispatcherInfo? dispatcherInfo = null;

        var app = Application.Current;
        if (app is null)
            throw new InvalidOperationException("Application.Current が null です。対象プロセスで WPF Application が起動していません。");

        app.Dispatcher.Invoke(() =>
        {
            var collector = new RuntimeAssemblyCollector(AppDomain.CurrentDomain.BaseDirectory);
            var stableIdMap = new Dictionary<DependencyObject, string>(ReferenceEqualityComparer.Instance);
            var uiThread = app.Dispatcher.Thread;
            dispatcherState = uiThread.ThreadState.ToString();
            dispatcherInfo = new WpfDispatcherInfo(
                ThreadId: uiThread.ManagedThreadId,
                ThreadName: uiThread.Name ?? string.Empty,
                ThreadState: uiThread.ThreadState.ToString(),
                ApartmentState: uiThread.GetApartmentState().ToString(),
                HasShutdownStarted: app.Dispatcher.HasShutdownStarted,
                HasShutdownFinished: app.Dispatcher.HasShutdownFinished,
                CurrentPriority: DispatcherPriority.Normal.ToString());

            var openWindows = app.Windows.OfType<Window>().ToList();
            var windowIds = openWindows
                .Select((window, index) => new { window, stableId = $"window:{index}" })
                .ToDictionary(entry => entry.window, entry => entry.stableId);

            foreach (var window in openWindows)
            {
                collector.Record(window.GetType(), "Window");
                var stableId = windowIds[window];
                var ownerStableId = window.Owner is not null && windowIds.TryGetValue(window.Owner, out var resolvedOwnerId)
                    ? resolvedOwnerId
                    : null;

                var root = CollectElement(
                    obj: window,
                    depth: 0,
                    collector: collector,
                    ownerWindow: window,
                    ownerWindowStableId: stableId,
                    parentStableId: null,
                    parentChain: [],
                    visualPath: "v:0",
                    logicalPath: "l:0",
                    stableIdMap: stableIdMap);

                windows.Add(new WpfWindowInfo(
                    Title: window.Title,
                    Width: window.ActualWidth,
                    Height: window.ActualHeight,
                    WindowState: window.WindowState.ToString(),
                    RootElement: root)
                {
                    StableId = stableId,
                    TypeName = window.GetType().Name,
                    FullTypeName = window.GetType().FullName,
                    OwnerWindowStableId = ownerStableId,
                    OwnedWindowStableIds = openWindows
                        .Where(candidate => ReferenceEquals(candidate.Owner, window))
                        .Select(candidate => windowIds[candidate])
                        .ToList(),
                    WindowHandle = GetWindowHandle(window),
                    IsVisible = window.IsVisible,
                    IsActive = window.IsActive,
                    IsLoaded = window.IsLoaded,
                    ShowInTaskbar = window.ShowInTaskbar,
                    Topmost = window.Topmost,
                    ResizeMode = window.ResizeMode.ToString(),
                    WindowStyle = window.WindowStyle.ToString(),
                    XamlSourceUri = TryGetBaseUri(window),
                    DispatcherThreadId = uiThread.ManagedThreadId
                });
            }

            var pfAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "PresentationFramework");
            wpfVersion = pfAsm?.GetName().Version?.ToString() ?? "不明";
            runtimeAssemblies = collector.ToList();
        }, DispatcherPriority.Normal);

        return new WpfInspectionResult(
            ProcessId: processId,
            WpfVersion: wpfVersion,
            DispatcherState: dispatcherState,
            RuntimeAssemblies: runtimeAssemblies,
            Windows: windows)
        {
            CollectedAtUtc = DateTimeOffset.UtcNow,
            Dispatcher = dispatcherInfo,
            Notes =
            [
                "Schema 2.0 collector",
                "Visual tree is primary; logical/auxiliary relationships are attached as metadata."
            ]
        };
    }

    private static WpfElementInfo CollectElement(
        DependencyObject obj,
        int depth,
        RuntimeAssemblyCollector collector,
        Window ownerWindow,
        string ownerWindowStableId,
        string? parentStableId,
        List<WpfElementReference> parentChain,
        string visualPath,
        string logicalPath,
        Dictionary<DependencyObject, string> stableIdMap)
    {
        var type = obj.GetType();
        collector.Record(type, "Visual");

        var frameworkElement = obj as FrameworkElement;
        var uiElement = obj as UIElement;
        var stableId = $"{ownerWindowStableId}|{visualPath}";
        stableIdMap[obj] = stableId;
        var typeName = type.Name;
        var automationId = AutomationProperties.GetAutomationId(obj);
        var name = frameworkElement?.Name ?? string.Empty;
        var normalizedAutomationId = string.IsNullOrWhiteSpace(automationId) ? null : automationId;
        var text = ExtractText(obj);
        var dataContext = frameworkElement?.DataContext;
        Type? dataContextType = null;
        if (dataContext is not null)
        {
            dataContextType = dataContext.GetType();
            collector.Record(dataContextType, "DataContext");
        }

        var selfReference = new WpfElementReference(
            StableId: stableId,
            TypeName: typeName,
            Name: string.IsNullOrWhiteSpace(name) ? null : name,
            AutomationId: normalizedAutomationId,
            Relationship: "Self",
            Path: visualPath);
        var nextParentChain = new List<WpfElementReference>(parentChain) { selfReference };

        var directVisualChildren = GetVisualChildren(obj)
            .Where(child => !ShouldSkip(child))
            .ToList();
        var directLogicalChildren = GetLogicalChildren(obj);
        var supplementalChildren = GetSupplementalChildren(obj)
            .Where(child => directVisualChildren.All(existing => !ReferenceEquals(existing, child)))
            .Where(child => directLogicalChildren.All(existing => !ReferenceEquals(existing, child)))
            .ToList();
        var adornerChildren = GetAdornerChildren(obj)
            .Where(child => directVisualChildren.All(existing => !ReferenceEquals(existing, child)))
            .Where(child => directLogicalChildren.All(existing => !ReferenceEquals(existing, child)))
            .Where(child => supplementalChildren.All(existing => !ReferenceEquals(existing, child)))
            .ToList();

        var children = new List<WpfElementInfo>();
        if (depth < MaxDepth)
        {
            for (var index = 0; index < Math.Min(directVisualChildren.Count, MaxChildren); index++)
            {
                var child = directVisualChildren[index];
                children.Add(CollectElement(
                    obj: child,
                    depth: depth + 1,
                    collector: collector,
                    ownerWindow: ownerWindow,
                    ownerWindowStableId: ownerWindowStableId,
                    parentStableId: stableId,
                    parentChain: nextParentChain,
                    visualPath: $"{visualPath}/{index}",
                    logicalPath: $"{logicalPath}/{ResolveLogicalIndex(directLogicalChildren, child)}",
                    stableIdMap: stableIdMap));
            }

            var remainingSlots = Math.Max(0, MaxChildren - children.Count);
            for (var index = 0; index < Math.Min(supplementalChildren.Count, remainingSlots); index++)
            {
                var child = supplementalChildren[index];
                children.Add(CollectElement(
                    obj: child,
                    depth: depth + 1,
                    collector: collector,
                    ownerWindow: ownerWindow,
                    ownerWindowStableId: ownerWindowStableId,
                    parentStableId: stableId,
                    parentChain: nextParentChain,
                    visualPath: $"{visualPath}/aux{index}",
                    logicalPath: $"{logicalPath}/aux{ResolveLogicalIndex(directLogicalChildren, child)}",
                    stableIdMap: stableIdMap));
            }

            remainingSlots = Math.Max(0, MaxChildren - children.Count);
            for (var index = 0; index < Math.Min(adornerChildren.Count, remainingSlots); index++)
            {
                var child = adornerChildren[index];
                children.Add(CollectElement(
                    obj: child,
                    depth: depth + 1,
                    collector: collector,
                    ownerWindow: ownerWindow,
                    ownerWindowStableId: ownerWindowStableId,
                    parentStableId: stableId,
                    parentChain: nextParentChain,
                    visualPath: $"{visualPath}/adorner{index}",
                    logicalPath: $"{logicalPath}/adorner{ResolveLogicalIndex(directLogicalChildren, child)}",
                    stableIdMap: stableIdMap));
            }
        }

        var observedProperties = BuildObservedProperties(obj, ownerWindowStableId, stableIdMap);
        var resourceReferences = TryBuild(() => BuildResourceReferences(observedProperties), []);
        var validation = BuildValidationInfo(obj);
        var resourceDictionaries = TryBuild(() => BuildResourceDictionaries(obj), []);
        var itemsControlInfo = BuildItemsControlInfo(obj, ownerWindowStableId, stableIdMap);
        var commandInfo = BuildCommandInfo(obj, ownerWindowStableId, stableIdMap);
        var codeReferences = TryBuild(() => BuildCodeReferences(obj, ownerWindowStableId, stableIdMap), []);

        return new WpfElementInfo(
            TypeName: typeName,
            Name: name,
            AutomationId: normalizedAutomationId,
            Text: text,
            DataContextTypeName: dataContextType?.Name,
            IsVisible: uiElement?.IsVisible == true,
            IsEnabled: uiElement?.IsEnabled == true,
            Width: frameworkElement?.ActualWidth ?? 0d,
            Height: frameworkElement?.ActualHeight ?? 0d,
            Children: children)
        {
            StableId = stableId,
            FullTypeName = type.FullName,
            VisualPath = visualPath,
            LogicalPath = logicalPath,
            ParentStableId = parentStableId,
            VisualParentStableId = parentStableId,
            LogicalParentStableId = GetLogicalParent(obj) is DependencyObject logicalParent
                ? TryResolveStableId(logicalParent, ownerWindowStableId, stableIdMap)
                : parentStableId,
            ParentChain = parentChain,
            TemplatedParent = CreateReference(frameworkElement?.TemplatedParent, "TemplatedParent", ownerWindowStableId, stableIdMap),
            OwnerWindowStableId = ownerWindowStableId,
            WindowHandle = GetWindowHandle(ownerWindow),
            IsLoaded = frameworkElement?.IsLoaded ?? false,
            IsHitTestVisible = uiElement?.IsHitTestVisible == true,
            Focusable = uiElement?.Focusable == true,
            IsFocused = uiElement?.IsFocused == true,
            IsKeyboardFocused = uiElement?.IsKeyboardFocused == true,
            IsKeyboardFocusWithin = uiElement?.IsKeyboardFocusWithin == true,
            IsTabStop = obj switch
            {
                Control control => control.IsTabStop,
                _ => null
            },
            IsMouseOver = uiElement?.IsMouseOver == true,
            DataContextValueSummary = SummarizeValue(dataContext),
            XamlSourceUri = TryGetBaseUri(obj),
            VisualChildCount = directVisualChildren.Count,
            LogicalChildCount = directLogicalChildren.Count,
            ChildCollections = BuildChildCollections(
                ownerWindowStableId,
                visualPath,
                directVisualChildren,
                directLogicalChildren,
                supplementalChildren,
                adornerChildren,
                stableIdMap),
            ObservedProperties = observedProperties,
            Validation = validation,
            ResourceDictionaries = resourceDictionaries,
            ResourceReferences = resourceReferences,
            ItemsControl = itemsControlInfo,
            Command = commandInfo,
            CodeReferences = codeReferences,
            InteropBoundary = BuildInteropBoundaryInfo(obj),
            IsReference = false,
            ReferenceReason = null
        };
    }

    private static List<WpfChildCollectionInfo> BuildChildCollections(
        string ownerWindowStableId,
        string visualPath,
        IReadOnlyList<DependencyObject> visualChildren,
        IReadOnlyList<DependencyObject> logicalChildren,
        IReadOnlyList<DependencyObject> supplementalChildren,
        IReadOnlyList<DependencyObject> adornerChildren,
        IReadOnlyDictionary<DependencyObject, string> stableIdMap)
    {
        var collections = new List<WpfChildCollectionInfo>
        {
            BuildChildCollection("Visual", ownerWindowStableId, visualPath, visualChildren, stableIdMap),
            BuildChildCollection("Logical", ownerWindowStableId, visualPath, logicalChildren, stableIdMap)
        };

        if (supplementalChildren.Count > 0)
        {
            collections.Add(BuildChildCollection("Auxiliary", ownerWindowStableId, visualPath, supplementalChildren, stableIdMap));
        }

        if (adornerChildren.Count > 0)
        {
            collections.Add(BuildChildCollection("Adorner", ownerWindowStableId, visualPath, adornerChildren, stableIdMap));
        }

        return collections;
    }

    private static WpfChildCollectionInfo BuildChildCollection(
        string kind,
        string ownerWindowStableId,
        string visualPath,
        IReadOnlyList<DependencyObject> children,
        IReadOnlyDictionary<DependencyObject, string> stableIdMap)
    {
        var returnedCount = Math.Min(children.Count, MaxChildren);
        return new WpfChildCollectionInfo(
            Kind: kind,
            TotalCount: children.Count,
            ReturnedCount: returnedCount,
            OmittedCount: Math.Max(0, children.Count - returnedCount),
            WasTruncated: children.Count > returnedCount,
            CanEnumerateMore: children.Count > returnedCount,
            ChildReferences: children
                .Take(returnedCount)
                .Select((child, index) => CreateReference(
                    child,
                    kind,
                    ownerWindowStableId,
                    stableIdMap,
                    $"{visualPath}/{kind.ToLowerInvariant()}:{index}"))
                .Where(reference => reference is not null)
                .Cast<WpfElementReference>()
                .ToList(),
            Notes: children.Count > returnedCount
                ? $"Only the first {returnedCount} children are embedded in this snapshot."
                : null);
    }

    private static List<WpfDependencyPropertyValueInfo> BuildObservedProperties(
        DependencyObject obj,
        string ownerWindowStableId,
        IReadOnlyDictionary<DependencyObject, string> stableIdMap)
    {
        var properties = new List<WpfDependencyPropertyValueInfo>();
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);

        TryAddObservedProperty(properties, propertyNames, obj, UIElement.VisibilityProperty, "Visibility", ownerWindowStableId, stableIdMap);
        TryAddObservedProperty(properties, propertyNames, obj, UIElement.IsEnabledProperty, "IsEnabled", ownerWindowStableId, stableIdMap);
        TryAddObservedProperty(properties, propertyNames, obj, UIElement.IsFocusedProperty, "IsFocused", ownerWindowStableId, stableIdMap);
        TryAddObservedProperty(properties, propertyNames, obj, UIElement.IsKeyboardFocusedProperty, "IsKeyboardFocused", ownerWindowStableId, stableIdMap);
        TryAddObservedProperty(properties, propertyNames, obj, UIElement.IsKeyboardFocusWithinProperty, "IsKeyboardFocusWithin", ownerWindowStableId, stableIdMap);
        TryAddObservedProperty(properties, propertyNames, obj, UIElement.IsHitTestVisibleProperty, "IsHitTestVisible", ownerWindowStableId, stableIdMap);
        TryAddObservedProperty(properties, propertyNames, obj, UIElement.FocusableProperty, "Focusable", ownerWindowStableId, stableIdMap);

        if (obj is ToggleButton)
        {
            TryAddObservedProperty(properties, propertyNames, obj, ToggleButton.IsCheckedProperty, "IsChecked", ownerWindowStableId, stableIdMap);
        }
        else if (obj is MenuItem)
        {
            TryAddObservedProperty(properties, propertyNames, obj, MenuItem.IsCheckedProperty, "IsChecked", ownerWindowStableId, stableIdMap);
        }

        if (obj is ListBoxItem)
        {
            TryAddObservedProperty(properties, propertyNames, obj, ListBoxItem.IsSelectedProperty, "IsSelected", ownerWindowStableId, stableIdMap);
        }
        else if (obj is TreeViewItem)
        {
            TryAddObservedProperty(properties, propertyNames, obj, TreeViewItem.IsSelectedProperty, "IsSelected", ownerWindowStableId, stableIdMap);
        }
        else if (obj is TabItem)
        {
            TryAddObservedProperty(properties, propertyNames, obj, TabItem.IsSelectedProperty, "IsSelected", ownerWindowStableId, stableIdMap);
        }
        else
        {
            TryAddObservedProperty(properties, propertyNames, obj, Selector.IsSelectedProperty, "IsSelected", ownerWindowStableId, stableIdMap);
        }

        if (obj is FrameworkElement)
        {
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.DataContextProperty, "DataContext", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.StyleProperty, "Style", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.WidthProperty, "Width", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.HeightProperty, "Height", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.MinWidthProperty, "MinWidth", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.MinHeightProperty, "MinHeight", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.MaxWidthProperty, "MaxWidth", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.MaxHeightProperty, "MaxHeight", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.ActualWidthProperty, "ActualWidth", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.ActualHeightProperty, "ActualHeight", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.MarginProperty, "Margin", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.TagProperty, "Tag", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.ToolTipProperty, "ToolTip", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkElement.ContextMenuProperty, "ContextMenu", ownerWindowStableId, stableIdMap);
        }
        else if (obj is FrameworkContentElement)
        {
            TryAddObservedProperty(properties, propertyNames, obj, FrameworkContentElement.DataContextProperty, "DataContext", ownerWindowStableId, stableIdMap);
        }

        if (obj is Control)
        {
            TryAddObservedProperty(properties, propertyNames, obj, Control.TemplateProperty, "Template", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Control.ForegroundProperty, "Foreground", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Control.BackgroundProperty, "Background", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Control.BorderBrushProperty, "BorderBrush", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Control.BorderThicknessProperty, "BorderThickness", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Control.PaddingProperty, "Padding", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Control.FontFamilyProperty, "FontFamily", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Control.FontSizeProperty, "FontSize", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Control.FontWeightProperty, "FontWeight", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Control.IsTabStopProperty, "IsTabStop", ownerWindowStableId, stableIdMap);
        }
        else if (obj is TextBlock or Run)
        {
            TryAddObservedProperty(properties, propertyNames, obj, TextElement.ForegroundProperty, "Foreground", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, TextElement.BackgroundProperty, "Background", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, TextElement.FontFamilyProperty, "FontFamily", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, TextElement.FontSizeProperty, "FontSize", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, TextElement.FontWeightProperty, "FontWeight", ownerWindowStableId, stableIdMap);
        }
        else if (obj is Border)
        {
            TryAddObservedProperty(properties, propertyNames, obj, Border.BackgroundProperty, "Background", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Border.BorderBrushProperty, "BorderBrush", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Border.BorderThicknessProperty, "BorderThickness", ownerWindowStableId, stableIdMap);
        }
        else if (obj is Panel)
        {
            TryAddObservedProperty(properties, propertyNames, obj, Panel.BackgroundProperty, "Background", ownerWindowStableId, stableIdMap);
        }

        if (obj is TextBox)
        {
            TryAddObservedProperty(properties, propertyNames, obj, TextBox.TextProperty, "Text", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, TextBoxBase.IsReadOnlyProperty, "IsReadOnly", ownerWindowStableId, stableIdMap);
        }
        else if (obj is TextBlock)
        {
            TryAddObservedProperty(properties, propertyNames, obj, TextBlock.TextProperty, "Text", ownerWindowStableId, stableIdMap);
        }
        else if (obj is Run)
        {
            TryAddObservedProperty(properties, propertyNames, obj, Run.TextProperty, "Text", ownerWindowStableId, stableIdMap);
        }

        if (obj is Selector)
        {
            TryAddObservedProperty(properties, propertyNames, obj, Selector.SelectedItemProperty, "SelectedItem", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Selector.SelectedValueProperty, "SelectedValue", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Selector.SelectedIndexProperty, "SelectedIndex", ownerWindowStableId, stableIdMap);
        }

        if (obj is ItemsControl)
        {
            TryAddObservedProperty(properties, propertyNames, obj, ItemsControl.ItemsSourceProperty, "ItemsSource", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, ItemsControl.ItemTemplateProperty, "ItemTemplate", ownerWindowStableId, stableIdMap);
        }

        if (obj is ContentControl)
        {
            TryAddObservedProperty(properties, propertyNames, obj, ContentControl.ContentProperty, "Content", ownerWindowStableId, stableIdMap);
        }

        if (obj is HeaderedContentControl)
        {
            TryAddObservedProperty(properties, propertyNames, obj, HeaderedContentControl.HeaderProperty, "Header", ownerWindowStableId, stableIdMap);
        }
        else if (obj is HeaderedItemsControl)
        {
            TryAddObservedProperty(properties, propertyNames, obj, HeaderedItemsControl.HeaderProperty, "Header", ownerWindowStableId, stableIdMap);
        }

        if (obj is Expander)
        {
            TryAddObservedProperty(properties, propertyNames, obj, Expander.IsExpandedProperty, "IsExpanded", ownerWindowStableId, stableIdMap);
        }
        else if (obj is TreeViewItem)
        {
            TryAddObservedProperty(properties, propertyNames, obj, TreeViewItem.IsExpandedProperty, "IsExpanded", ownerWindowStableId, stableIdMap);
        }

        if (obj is Popup)
        {
            TryAddObservedProperty(properties, propertyNames, obj, Popup.IsOpenProperty, "IsOpen", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Popup.PlacementTargetProperty, "PlacementTarget", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Popup.PlacementProperty, "Placement", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Popup.StaysOpenProperty, "StaysOpen", ownerWindowStableId, stableIdMap);
        }

        if (obj is ButtonBase)
        {
            TryAddObservedProperty(properties, propertyNames, obj, ButtonBase.CommandProperty, "Command", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, ButtonBase.CommandParameterProperty, "CommandParameter", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, ButtonBase.CommandTargetProperty, "CommandTarget", ownerWindowStableId, stableIdMap);
        }
        else if (obj is MenuItem)
        {
            TryAddObservedProperty(properties, propertyNames, obj, MenuItem.CommandProperty, "Command", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, MenuItem.CommandParameterProperty, "CommandParameter", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, MenuItem.CommandTargetProperty, "CommandTarget", ownerWindowStableId, stableIdMap);
        }
        else if (obj is Hyperlink)
        {
            TryAddObservedProperty(properties, propertyNames, obj, Hyperlink.CommandProperty, "Command", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Hyperlink.CommandParameterProperty, "CommandParameter", ownerWindowStableId, stableIdMap);
            TryAddObservedProperty(properties, propertyNames, obj, Hyperlink.CommandTargetProperty, "CommandTarget", ownerWindowStableId, stableIdMap);
        }

        return properties;
    }

    private static List<WpfResourceReferenceInfo> BuildResourceReferences(
        IReadOnlyList<WpfDependencyPropertyValueInfo> observedProperties) =>
        observedProperties
            .Where(property => property.Resource is not null)
            .Select(property => property.Resource!)
            .Distinct()
            .OrderBy(resource => resource.PropertyName, StringComparer.Ordinal)
            .ToList();

    private static void TryAddObservedProperty(
        ICollection<WpfDependencyPropertyValueInfo> properties,
        ISet<string> propertyNames,
        DependencyObject obj,
        DependencyProperty property,
        string propertyName,
        string ownerWindowStableId,
        IReadOnlyDictionary<DependencyObject, string> stableIdMap)
    {
        if (!propertyNames.Add(propertyName))
        {
            return;
        }

        object? value;
        try
        {
            value = obj.GetValue(property);
        }
        catch
        {
            return;
        }

        var source = DependencyPropertyHelper.GetValueSource(obj, property);
        var binding = BuildBindingInfo(obj, property);
        WpfResourceReferenceInfo? resource = null;
        try
        {
            resource = BuildResourceReference(obj, property, propertyName, value, source);
        }
        catch
        {
        }
        properties.Add(new WpfDependencyPropertyValueInfo(
            PropertyName: propertyName,
            OwnerTypeName: property.OwnerType.Name,
            ValueTypeName: value?.GetType().FullName,
            ValueSummary: SummarizeValue(value),
            BaseValueSource: source.BaseValueSource.ToString(),
            IsExpression: source.IsExpression,
            IsAnimated: source.IsAnimated,
            IsCoerced: source.IsCoerced,
            IsCurrent: source.IsCurrent,
            IsDefaultValue: source.BaseValueSource == BaseValueSource.Default,
            IsLocallySet: source.BaseValueSource == BaseValueSource.Local,
            ReferencedElementStableId: ResolveReferencedElementStableId(obj, property, value, ownerWindowStableId, stableIdMap),
            Binding: binding,
            Resource: resource));
    }

    private static WpfBindingInfo? BuildBindingInfo(DependencyObject obj, DependencyProperty property)
    {
        var expression = BindingOperations.GetBindingExpressionBase(obj, property);
        if (expression is null)
        {
            return null;
        }

        var errors = Validation.GetErrors(obj)
            .Select(error => error.ErrorContent?.ToString() ?? error.Exception?.Message ?? "Validation error")
            .ToList();

        if (BindingOperations.GetBinding(obj, property) is Binding binding)
        {
            var bindingExpression = expression as BindingExpression;
            return new WpfBindingInfo(
                BindingKind: nameof(Binding),
                Status: expression.Status.ToString(),
                TargetPropertyName: property.Name,
                Path: binding.Path?.Path,
                XPath: binding.XPath,
                ElementName: binding.ElementName,
                RelativeSourceMode: binding.RelativeSource?.Mode.ToString(),
                SourceTypeName: binding.Source?.GetType().FullName,
                SourceSummary: SummarizeValue(bindingExpression?.DataItem),
                DataItemTypeName: bindingExpression?.DataItem?.GetType().FullName,
                FallbackValue: SummarizeValue(binding.FallbackValue),
                TargetNullValue: SummarizeValue(binding.TargetNullValue),
                ConverterTypeName: binding.Converter?.GetType().FullName,
                StringFormat: binding.StringFormat,
                Mode: binding.Mode.ToString(),
                UpdateSourceTrigger: binding.UpdateSourceTrigger.ToString(),
                HasError: errors.Count > 0,
                Errors: errors,
                ChildBindings: []);
        }

        if (BindingOperations.GetMultiBinding(obj, property) is MultiBinding multiBinding)
        {
            return new WpfBindingInfo(
                BindingKind: nameof(MultiBinding),
                Status: expression.Status.ToString(),
                TargetPropertyName: property.Name,
                Path: null,
                XPath: null,
                ElementName: null,
                RelativeSourceMode: null,
                SourceTypeName: null,
                SourceSummary: null,
                DataItemTypeName: null,
                FallbackValue: SummarizeValue(multiBinding.FallbackValue),
                TargetNullValue: SummarizeValue(multiBinding.TargetNullValue),
                ConverterTypeName: multiBinding.Converter?.GetType().FullName,
                StringFormat: multiBinding.StringFormat,
                Mode: multiBinding.Mode.ToString(),
                UpdateSourceTrigger: multiBinding.UpdateSourceTrigger.ToString(),
                HasError: errors.Count > 0,
                Errors: errors,
                ChildBindings: multiBinding.Bindings.Select(BuildBindingDefinition).ToList());
        }

        if (BindingOperations.GetPriorityBinding(obj, property) is PriorityBinding priorityBinding)
        {
            return new WpfBindingInfo(
                BindingKind: nameof(PriorityBinding),
                Status: expression.Status.ToString(),
                TargetPropertyName: property.Name,
                Path: null,
                XPath: null,
                ElementName: null,
                RelativeSourceMode: null,
                SourceTypeName: null,
                SourceSummary: null,
                DataItemTypeName: null,
                FallbackValue: SummarizeValue(priorityBinding.FallbackValue),
                TargetNullValue: SummarizeValue(priorityBinding.TargetNullValue),
                ConverterTypeName: null,
                StringFormat: priorityBinding.StringFormat,
                Mode: null,
                UpdateSourceTrigger: null,
                HasError: errors.Count > 0,
                Errors: errors,
                ChildBindings: priorityBinding.Bindings.Select(BuildBindingDefinition).ToList());
        }

        return new WpfBindingInfo(
            BindingKind: expression.GetType().Name,
            Status: expression.Status.ToString(),
            TargetPropertyName: property.Name,
            Path: null,
            XPath: null,
            ElementName: null,
            RelativeSourceMode: null,
            SourceTypeName: null,
            SourceSummary: null,
            DataItemTypeName: null,
            FallbackValue: null,
            TargetNullValue: null,
            ConverterTypeName: null,
            StringFormat: null,
            Mode: null,
            UpdateSourceTrigger: null,
            HasError: errors.Count > 0,
            Errors: errors,
            ChildBindings: []);
    }

    private static WpfBindingInfo BuildBindingDefinition(BindingBase bindingBase)
    {
        if (bindingBase is Binding binding)
        {
            return new WpfBindingInfo(
                BindingKind: nameof(Binding),
                Status: "Definition",
                TargetPropertyName: null,
                Path: binding.Path?.Path,
                XPath: binding.XPath,
                ElementName: binding.ElementName,
                RelativeSourceMode: binding.RelativeSource?.Mode.ToString(),
                SourceTypeName: binding.Source?.GetType().FullName,
                SourceSummary: SummarizeValue(binding.Source),
                DataItemTypeName: null,
                FallbackValue: SummarizeValue(binding.FallbackValue),
                TargetNullValue: SummarizeValue(binding.TargetNullValue),
                ConverterTypeName: binding.Converter?.GetType().FullName,
                StringFormat: binding.StringFormat,
                Mode: binding.Mode.ToString(),
                UpdateSourceTrigger: binding.UpdateSourceTrigger.ToString(),
                HasError: false,
                Errors: [],
                ChildBindings: []);
        }

        if (bindingBase is MultiBinding multiBinding)
        {
            return new WpfBindingInfo(
                BindingKind: nameof(MultiBinding),
                Status: "Definition",
                TargetPropertyName: null,
                Path: null,
                XPath: null,
                ElementName: null,
                RelativeSourceMode: null,
                SourceTypeName: null,
                SourceSummary: null,
                DataItemTypeName: null,
                FallbackValue: SummarizeValue(multiBinding.FallbackValue),
                TargetNullValue: SummarizeValue(multiBinding.TargetNullValue),
                ConverterTypeName: multiBinding.Converter?.GetType().FullName,
                StringFormat: multiBinding.StringFormat,
                Mode: multiBinding.Mode.ToString(),
                UpdateSourceTrigger: multiBinding.UpdateSourceTrigger.ToString(),
                HasError: false,
                Errors: [],
                ChildBindings: multiBinding.Bindings.Select(BuildBindingDefinition).ToList());
        }

        if (bindingBase is PriorityBinding priorityBinding)
        {
            return new WpfBindingInfo(
                BindingKind: nameof(PriorityBinding),
                Status: "Definition",
                TargetPropertyName: null,
                Path: null,
                XPath: null,
                ElementName: null,
                RelativeSourceMode: null,
                SourceTypeName: null,
                SourceSummary: null,
                DataItemTypeName: null,
                FallbackValue: SummarizeValue(priorityBinding.FallbackValue),
                TargetNullValue: SummarizeValue(priorityBinding.TargetNullValue),
                ConverterTypeName: null,
                StringFormat: priorityBinding.StringFormat,
                Mode: null,
                UpdateSourceTrigger: null,
                HasError: false,
                Errors: [],
                ChildBindings: priorityBinding.Bindings.Select(BuildBindingDefinition).ToList());
        }

        return new WpfBindingInfo(
            BindingKind: bindingBase.GetType().Name,
            Status: "Definition",
            TargetPropertyName: null,
            Path: null,
            XPath: null,
            ElementName: null,
            RelativeSourceMode: null,
            SourceTypeName: null,
            SourceSummary: null,
            DataItemTypeName: null,
            FallbackValue: null,
            TargetNullValue: null,
            ConverterTypeName: null,
            StringFormat: null,
            Mode: null,
            UpdateSourceTrigger: null,
            HasError: false,
            Errors: [],
            ChildBindings: []);
    }

    private static WpfValidationInfo? BuildValidationInfo(DependencyObject obj)
    {
        var hasError = Validation.GetHasError(obj);
        var errors = Validation.GetErrors(obj)
            .Select(error => error.ErrorContent?.ToString() ?? error.Exception?.Message ?? "Validation error")
            .ToList();

        if (!hasError && errors.Count == 0)
        {
            return null;
        }

        return new WpfValidationInfo(
            HasError: hasError,
            ErrorCount: errors.Count,
            Errors: errors,
            Summary: string.Join(" | ", errors));
    }

    private static List<WpfResourceDictionaryInfo> BuildResourceDictionaries(DependencyObject obj)
    {
        var resources = new List<WpfResourceDictionaryInfo>();
        var seen = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);

        void AddResourceDictionary(string scope, string ownerTypeName, ResourceDictionary dictionary)
        {
            if (!seen.Add(dictionary))
            {
                return;
            }

            resources.Add(new WpfResourceDictionaryInfo(
                Scope: scope,
                OwnerTypeName: ownerTypeName,
                Source: dictionary.Source?.ToString(),
                KeyCount: dictionary.Keys.Count,
                MergedDictionaryCount: dictionary.MergedDictionaries.Count));
        }

        if (obj is FrameworkElement frameworkElement)
        {
            AddResourceDictionary("Element", frameworkElement.GetType().Name, frameworkElement.Resources);

            if (frameworkElement.Style is { Resources: { } styleResources })
            {
                AddResourceDictionary("Style", frameworkElement.Style.GetType().Name, styleResources);
            }
        }
        else if (obj is FrameworkContentElement frameworkContentElement)
        {
            AddResourceDictionary("ContentElement", frameworkContentElement.GetType().Name, frameworkContentElement.Resources);

            if (frameworkContentElement.Style is { Resources: { } styleResources })
            {
                AddResourceDictionary("Style", frameworkContentElement.Style.GetType().Name, styleResources);
            }
        }

        if (obj is Control { Template: { } template })
        {
            AddResourceDictionary("Template", template.GetType().Name, template.Resources);
        }

        var ownerWindow = TryGetContainingWindow(obj);
        if (ownerWindow is not null && !ReferenceEquals(ownerWindow, obj))
        {
            AddResourceDictionary("Window", ownerWindow.GetType().Name, ownerWindow.Resources);
        }

        if (Application.Current is { Resources: { } appResources })
        {
            AddResourceDictionary("Application", nameof(Application), appResources);
        }

        return resources;
    }

    private static WpfItemsControlInfo? BuildItemsControlInfo(
        DependencyObject obj,
        string ownerWindowStableId,
        IReadOnlyDictionary<DependencyObject, string> stableIdMap)
    {
        if (obj is not ItemsControl itemsControl)
        {
            return null;
        }

        var itemMappings = new List<WpfItemContainerInfo>();
        var realizedCount = 0;
        var itemCount = itemsControl.Items.Count;
        for (var index = 0; index < itemCount; index++)
        {
            var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(index) as DependencyObject;
            if (container is not null)
            {
                realizedCount++;
            }

            if (index >= MaxChildren)
            {
                continue;
            }

            var item = itemsControl.Items[index];

            itemMappings.Add(new WpfItemContainerInfo(
                Index: index,
                ItemTypeName: item?.GetType().FullName,
                ItemSummary: SummarizeValue(item),
                ContainerStableId: container is null
                    ? null
                    : TryResolveStableId(container, ownerWindowStableId, stableIdMap),
                ContainerTypeName: container?.GetType().FullName,
                IsRealized: container is not null));
        }

        return new WpfItemsControlInfo(
            HasItems: itemCount > 0,
            ItemCount: itemCount,
            RealizedItemCount: realizedCount,
            UnrealizedItemCount: Math.Max(0, itemCount - realizedCount),
            ItemsSourceTypeName: itemsControl.ItemsSource?.GetType().FullName,
            GeneratorStatus: itemsControl.ItemContainerGenerator.Status.ToString(),
            ItemMappings: itemMappings,
            Virtualization: BuildVirtualizationInfo(itemsControl));
    }

    private static WpfVirtualizationInfo BuildVirtualizationInfo(ItemsControl itemsControl) =>
        new(
            IsVirtualizing: VirtualizingPanel.GetIsVirtualizing(itemsControl),
            IsVirtualizingWhenGrouping: VirtualizingPanel.GetIsVirtualizingWhenGrouping(itemsControl),
            VirtualizationMode: VirtualizingPanel.GetVirtualizationMode(itemsControl).ToString(),
            ScrollUnit: VirtualizingPanel.GetScrollUnit(itemsControl).ToString(),
            CacheLength: VirtualizingPanel.GetCacheLength(itemsControl).ToString(),
            CacheLengthUnit: VirtualizingPanel.GetCacheLengthUnit(itemsControl).ToString(),
            CanContentScroll: ScrollViewer.GetCanContentScroll(itemsControl));

    private static WpfCommandInfo? BuildCommandInfo(
        DependencyObject obj,
        string ownerWindowStableId,
        IReadOnlyDictionary<DependencyObject, string> stableIdMap)
    {
        if (obj is not ICommandSource commandSource || commandSource.Command is null)
        {
            return null;
        }

        var command = commandSource.Command;
        var target = commandSource.CommandTarget as DependencyObject;
        var targetStableId = target is null
            ? null
            : TryResolveStableId(target, ownerWindowStableId, stableIdMap);
        return new WpfCommandInfo(
            CommandTypeName: command.GetType().FullName,
            CommandName: command.ToString(),
            OwnerTypeName: obj.GetType().FullName,
            CommandParameterTypeName: commandSource.CommandParameter?.GetType().FullName,
            CommandParameterSummary: SummarizeValue(commandSource.CommandParameter),
            CommandTargetStableId: targetStableId,
            CommandTargetTypeName: target?.GetType().FullName,
            CanExecute: SafeCanExecute(command, commandSource.CommandParameter),
            CommandBindingOwners: BuildCommandBindingOwners(obj, target, command, ownerWindowStableId, stableIdMap),
            CodeReferences: BuildCommandCodeReferences(command, target, targetStableId));
    }

    private static List<WpfCodeReferenceInfo> BuildCodeReferences(
        DependencyObject obj,
        string ownerWindowStableId,
        IReadOnlyDictionary<DependencyObject, string> stableIdMap)
    {
        var type = obj.GetType();
        var references = new List<WpfCodeReferenceInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddReference(string kind, Type referencedType, string? identifier, string? displayName, string? sourceUri, string? notes)
        {
            var key = $"{kind}|{referencedType.AssemblyQualifiedName}|{identifier}|{displayName}|{sourceUri}|{notes}";
            if (!seen.Add(key))
            {
                return;
            }

            references.Add(new WpfCodeReferenceInfo(
                Kind: kind,
                Identifier: identifier,
                DisplayName: displayName,
                AssemblyName: referencedType.Assembly.GetName().Name,
                AssemblyPath: TryGetAssemblyLocation(referencedType),
                TypeName: referencedType.FullName,
                SourceUri: sourceUri,
                Notes: notes));
        }

        AddReference(
            kind: "ControlType",
            referencedType: type,
            identifier: type.Name,
            displayName: type.FullName,
            sourceUri: TryGetBaseUri(obj),
            notes: null);

        if (obj is FrameworkElement { DataContext: not null } frameworkElement)
        {
            var dataContextType = frameworkElement.DataContext.GetType();
            AddReference(
                kind: "DataContext",
                referencedType: dataContextType,
                identifier: dataContextType.Name,
                displayName: dataContextType.FullName,
                sourceUri: null,
                notes: frameworkElement.DataContext is DependencyObject dependencyObject
                    ? BuildStableIdNote(TryResolveStableId(dependencyObject, ownerWindowStableId, stableIdMap))
                    : null);

            var styleTargetType = TryGetStyleTargetType(frameworkElement.Style);
            if (styleTargetType is not null)
            {
                AddReference(
                    kind: "StyleTargetType",
                    referencedType: styleTargetType,
                    identifier: styleTargetType.Name,
                    displayName: styleTargetType.FullName,
                    sourceUri: null,
                    notes: null);
            }
        }

        if (obj is FrameworkContentElement { DataContext: not null } contentElement)
        {
            var dataContextType = contentElement.DataContext.GetType();
            AddReference(
                kind: "DataContext",
                referencedType: dataContextType,
                identifier: dataContextType.Name,
                displayName: dataContextType.FullName,
                sourceUri: null,
                notes: contentElement.DataContext is DependencyObject dependencyObject
                    ? BuildStableIdNote(TryResolveStableId(dependencyObject, ownerWindowStableId, stableIdMap))
                    : null);
        }

        if (obj is Control { Template: { } template })
        {
            var templateTargetType = TryGetTemplateTargetType(template);
            if (templateTargetType is not null)
            {
                AddReference(
                    kind: "TemplateTargetType",
                    referencedType: templateTargetType,
                    identifier: templateTargetType.Name,
                    displayName: templateTargetType.FullName,
                    sourceUri: null,
                    notes: template.GetType().FullName);
            }
        }

        if (obj is ItemsControl { ItemsSource: not null } itemsControl)
        {
            var itemsSourceType = itemsControl.ItemsSource.GetType();
            AddReference(
                kind: "ItemsSource",
                referencedType: itemsSourceType,
                identifier: itemsSourceType.Name,
                displayName: itemsSourceType.FullName,
                sourceUri: null,
                notes: null);
        }

        if (obj is ICommandSource { Command: not null } commandSource)
        {
            var command = commandSource.Command;
            var commandType = command.GetType();
            AddReference(
                kind: "Command",
                referencedType: commandType,
                identifier: commandType.Name,
                displayName: command.ToString(),
                sourceUri: null,
                notes: commandSource.CommandTarget is DependencyObject target
                    ? BuildStableIdNote(TryResolveStableId(target, ownerWindowStableId, stableIdMap))
                    : null);
        }

        return references;
    }

    private static WpfInteropBoundaryInfo? BuildInteropBoundaryInfo(DependencyObject obj)
    {
        if (obj is HwndHost host)
        {
            return new WpfInteropBoundaryInfo(
                BoundaryKind: nameof(HwndHost),
                IsExternalBoundary: true,
                Handle: host.Handle.ToString("X"),
                ExternalTechnology: "Win32 child HWND",
                Notes: "Requires a non-WPF adapter beyond this boundary.");
        }

        if (obj.GetType().FullName?.Contains("WebView2", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new WpfInteropBoundaryInfo(
                BoundaryKind: "WebView2",
                IsExternalBoundary: true,
                Handle: null,
                ExternalTechnology: "Edge/WebView2",
                Notes: "Rendered outside managed WPF visual semantics.");
        }

        return null;
    }

    private static IEnumerable<DependencyObject> GetVisualChildren(DependencyObject obj)
    {
        int count;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(obj);
        }
        catch
        {
            yield break;
        }

        for (var index = 0; index < count; index++)
        {
            yield return VisualTreeHelper.GetChild(obj, index);
        }
    }

    private static List<DependencyObject> GetLogicalChildren(DependencyObject obj)
    {
        var result = new List<DependencyObject>();
        try
        {
            foreach (var child in LogicalTreeHelper.GetChildren(obj))
            {
                if (child is DependencyObject dependencyObject)
                {
                    result.Add(dependencyObject);
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static List<DependencyObject> GetSupplementalChildren(DependencyObject obj)
    {
        var result = new List<DependencyObject>();

        if (obj is FrameworkElement frameworkElement)
        {
            if (frameworkElement.ContextMenu is { } contextMenu)
            {
                result.Add(contextMenu);
            }

            if (frameworkElement.ToolTip is DependencyObject toolTipObject)
            {
                result.Add(toolTipObject);
            }
        }

        if (obj is Popup popup && popup.Child is DependencyObject popupChild)
        {
            result.Add(popupChild);
        }

        return result;
    }

    private static List<DependencyObject> GetAdornerChildren(DependencyObject obj)
    {
        if (obj is not UIElement element)
        {
            return [];
        }

        try
        {
            var adornerLayer = AdornerLayer.GetAdornerLayer(element);
            return adornerLayer?.GetAdorners(element)?.Cast<DependencyObject>().ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static DependencyObject? GetLogicalParent(DependencyObject obj)
    {
        try
        {
            return LogicalTreeHelper.GetParent(obj);
        }
        catch
        {
            return null;
        }
    }

    private static int ResolveLogicalIndex(IReadOnlyList<DependencyObject> logicalChildren, DependencyObject child)
    {
        var index = logicalChildren
            .Select((candidate, resolvedIndex) => new { candidate, resolvedIndex })
            .FirstOrDefault(entry => ReferenceEquals(entry.candidate, child))
            ?.resolvedIndex;
        return index ?? -1;
    }

    private static string? TryResolveStableId(
        DependencyObject target,
        string ownerWindowStableId,
        IReadOnlyDictionary<DependencyObject, string> stableIdMap)
    {
        if (stableIdMap.TryGetValue(target, out var stableId))
        {
            return stableId;
        }

        if (Application.Current is null)
        {
            return null;
        }

        if (TryResolveStableIdByTraversal(target, ownerWindowStableId, out stableId))
        {
            if (stableId is not null && stableIdMap is IDictionary<DependencyObject, string> mutableMap)
            {
                mutableMap[target] = stableId;
            }

            return stableId;
        }

        return null;
    }

    private static bool TryResolveStableIdByTraversal(
        DependencyObject target,
        string ownerWindowStableId,
        out string? stableId)
    {
        stableId = null;

        if (Application.Current is null)
        {
            return false;
        }

        var windows = Application.Current.Windows.OfType<Window>().ToList();
        var preferredWindowIndex = TryParseWindowIndex(ownerWindowStableId);
        if (preferredWindowIndex is int windowIndex
            && windowIndex >= 0
            && windowIndex < windows.Count
            && TryResolveStableIdForElement(
                current: windows[windowIndex],
                target: target,
                ownerWindowStableId: $"window:{windowIndex}",
                visualPath: "v:0",
                visited: new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance),
                stableId: out stableId))
        {
            return true;
        }

        for (var index = 0; index < windows.Count; index++)
        {
            if (preferredWindowIndex == index)
            {
                continue;
            }

            if (TryResolveStableIdForElement(
                    current: windows[index],
                    target: target,
                    ownerWindowStableId: $"window:{index}",
                    visualPath: "v:0",
                    visited: new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance),
                    stableId: out stableId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveStableIdForElement(
        DependencyObject current,
        DependencyObject target,
        string ownerWindowStableId,
        string visualPath,
        ISet<DependencyObject> visited,
        out string? stableId)
    {
        if (!visited.Add(current))
        {
            stableId = null;
            return false;
        }

        if (ReferenceEquals(current, target))
        {
            stableId = $"{ownerWindowStableId}|{visualPath}";
            return true;
        }

        var directVisualChildren = GetVisualChildren(current)
            .Where(child => !ShouldSkip(child))
            .ToList();
        for (var index = 0; index < directVisualChildren.Count; index++)
        {
            if (TryResolveStableIdForElement(
                    directVisualChildren[index],
                    target,
                    ownerWindowStableId,
                    $"{visualPath}/{index}",
                    visited,
                    out stableId))
            {
                return true;
            }
        }

        var directLogicalChildren = GetLogicalChildren(current);
        var supplementalChildren = GetSupplementalChildren(current)
            .Where(child => directVisualChildren.All(existing => !ReferenceEquals(existing, child)))
            .Where(child => directLogicalChildren.All(existing => !ReferenceEquals(existing, child)))
            .ToList();
        for (var index = 0; index < supplementalChildren.Count; index++)
        {
            if (TryResolveStableIdForElement(
                    supplementalChildren[index],
                    target,
                    ownerWindowStableId,
                    $"{visualPath}/aux{index}",
                    visited,
                    out stableId))
            {
                return true;
            }
        }

        var adornerChildren = GetAdornerChildren(current)
            .Where(child => directVisualChildren.All(existing => !ReferenceEquals(existing, child)))
            .Where(child => directLogicalChildren.All(existing => !ReferenceEquals(existing, child)))
            .Where(child => supplementalChildren.All(existing => !ReferenceEquals(existing, child)))
            .ToList();
        for (var index = 0; index < adornerChildren.Count; index++)
        {
            if (TryResolveStableIdForElement(
                    adornerChildren[index],
                    target,
                    ownerWindowStableId,
                    $"{visualPath}/adorner{index}",
                    visited,
                    out stableId))
            {
                return true;
            }
        }

        stableId = null;
        return false;
    }

    private static WpfResourceReferenceInfo? BuildResourceReference(
        DependencyObject obj,
        DependencyProperty property,
        string propertyName,
        object? value,
        ValueSource source)
    {
        var localValue = TryReadLocalValue(obj, property);
        var dynamicResourceKey = TryGetDynamicResourceKey(localValue);
        var lookup = TryFindResourceLookup(obj, value, dynamicResourceKey);
        var isImplicit = source.BaseValueSource == BaseValueSource.ImplicitStyleReference;
        var isDynamic = dynamicResourceKey is not null;
        var resourceKey = dynamicResourceKey
            ?? lookup?.Key
            ?? TryInferImplicitResourceKey(property, value, obj);

        if (resourceKey is null && lookup is null && !isImplicit)
        {
            return null;
        }

        var notes = new List<string>();
        if (lookup?.Notes is { Length: > 0 } lookupNotes)
        {
            notes.Add(lookupNotes);
        }

        if (dynamicResourceKey is not null && lookup is null)
        {
            notes.Add("Resource key extracted from a runtime expression.");
        }
        else if (dynamicResourceKey is null && lookup?.Key is not null)
        {
            notes.Add("Resource key inferred from the resolved runtime value.");
        }

        if (source.IsExpression && dynamicResourceKey is null)
        {
            notes.Add("Expression-backed value; resource key reflection was unavailable.");
        }

        return new WpfResourceReferenceInfo(
            PropertyName: propertyName,
            ResolutionKind: ResolveResourceKind(source, isDynamic, isImplicit, lookup),
            ResourceKey: FormatResourceKey(resourceKey),
            SourceScope: lookup?.Scope ?? (isImplicit ? "Implicit" : null),
            SourceDescription: lookup?.SourceDescription,
            IsDynamic: isDynamic,
            IsExpression: source.IsExpression,
            IsImplicit: isImplicit,
            ResolvedValueTypeName: value?.GetType().FullName,
            ResolvedValueSummary: SummarizeValue(value))
        {
            Notes = notes.Count == 0 ? null : string.Join(" | ", notes)
        };
    }

    private static string? ResolveReferencedElementStableId(
        DependencyObject obj,
        DependencyProperty property,
        object? value,
        string ownerWindowStableId,
        IReadOnlyDictionary<DependencyObject, string> stableIdMap)
    {
        if (value is DependencyObject dependencyObject)
        {
            return TryResolveStableId(dependencyObject, ownerWindowStableId, stableIdMap);
        }

        if (property == Selector.SelectedItemProperty
            && obj is Selector selector
            && value is not null
            && selector.ItemContainerGenerator.ContainerFromItem(value) is DependencyObject selectedContainer)
        {
            return TryResolveStableId(selectedContainer, ownerWindowStableId, stableIdMap);
        }

        return null;
    }

    private static string? ExtractText(DependencyObject obj) =>
        obj switch
        {
            TextBlock textBlock => textBlock.Text,
            TextBox textBox => textBox.Text,
            ContentControl { Content: string content } => content,
            HeaderedContentControl { Header: string header } => header,
            HeaderedItemsControl { Header: string header } => header,
            Run run => run.Text,
            _ => null
        };

    private static bool ShouldSkip(DependencyObject obj)
    {
        if (obj is not UIElement ui) return false;
        if (ui.IsVisible) return false;
        if (obj is not FrameworkElement fe) return false;
        return fe.ActualWidth == 0 && fe.ActualHeight == 0;
    }

    private static WpfElementReference? CreateReference(
        DependencyObject? obj,
        string relationship,
        string ownerWindowStableId,
        IReadOnlyDictionary<DependencyObject, string> stableIdMap,
        string? path = null)
    {
        if (obj is null)
        {
            return null;
        }

        return new WpfElementReference(
            StableId: TryResolveStableId(obj, ownerWindowStableId, stableIdMap)
                ?? $"{ownerWindowStableId}|ref:{ResolveElementIdentity(obj)}",
            TypeName: obj.GetType().Name,
            Name: GetElementName(obj),
            AutomationId: GetAutomationId(obj),
            Relationship: relationship,
            Path: path);
    }

    private static string ResolveElementIdentity(DependencyObject obj)
    {
        if (obj is FrameworkElement { Name: { Length: > 0 } name })
        {
            return name;
        }

        if (obj is FrameworkContentElement { Name: { Length: > 0 } contentElementName })
        {
            return contentElementName;
        }

        var automationId = GetAutomationId(obj);
        if (!string.IsNullOrWhiteSpace(automationId))
        {
            return automationId;
        }

        return obj.GetHashCode().ToString("X");
    }

    private static string? TryGetBaseUri(DependencyObject obj)
    {
        try
        {
            var uri = BaseUriHelper.GetBaseUri(obj);
            return uri?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetAutomationId(DependencyObject obj)
    {
        var automationId = AutomationProperties.GetAutomationId(obj);
        return string.IsNullOrWhiteSpace(automationId) ? null : automationId;
    }

    private static string? GetElementName(DependencyObject obj) =>
        obj switch
        {
            FrameworkElement { Name: { Length: > 0 } name } => name,
            FrameworkContentElement { Name: { Length: > 0 } name } => name,
            _ => null
        };

    private static string? GetWindowHandle(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            return handle == IntPtr.Zero ? null : $"0x{handle.ToInt64():X}";
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetAssemblyLocation(Type type)
    {
        try
        {
            return type.Assembly.IsDynamic || string.IsNullOrWhiteSpace(type.Assembly.Location)
                ? null
                : type.Assembly.Location;
        }
        catch
        {
            return null;
        }
    }

    private static string? SummarizeValue(object? value) =>
        value switch
        {
            null => null,
            string text => text,
            DependencyObject dependencyObject => $"{dependencyObject.GetType().Name}:{ResolveElementIdentity(dependencyObject)}",
            _ => value.ToString()
        };

    private static bool? SafeCanExecute(ICommand command, object? parameter)
    {
        try
        {
            return command.CanExecute(parameter);
        }
        catch
        {
            return null;
        }
    }

    private static List<WpfElementReference> BuildCommandBindingOwners(
        DependencyObject commandSource,
        DependencyObject? commandTarget,
        ICommand command,
        string ownerWindowStableId,
        IReadOnlyDictionary<DependencyObject, string> stableIdMap)
    {
        var owners = new List<WpfElementReference>();
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);

        foreach (var candidate in EnumerateSelfAndAncestors(commandSource))
        {
            if (!seen.Add(candidate) || !HasMatchingCommandBinding(candidate, command))
            {
                continue;
            }

            var reference = CreateReference(candidate, "CommandBindingOwner", ownerWindowStableId, stableIdMap);
            if (reference is not null)
            {
                owners.Add(reference);
            }
        }

        if (commandTarget is not null)
        {
            foreach (var candidate in EnumerateSelfAndAncestors(commandTarget))
            {
                if (!seen.Add(candidate) || !HasMatchingCommandBinding(candidate, command))
                {
                    continue;
                }

                var reference = CreateReference(candidate, "CommandBindingOwner", ownerWindowStableId, stableIdMap);
                if (reference is not null)
                {
                    owners.Add(reference);
                }
            }
        }

        return owners;
    }

    private static List<WpfCodeReferenceInfo> BuildCommandCodeReferences(
        ICommand command,
        DependencyObject? target,
        string? targetStableId)
    {
        var references = new List<WpfCodeReferenceInfo>();
        var commandType = command.GetType();
        references.Add(new WpfCodeReferenceInfo(
            Kind: "Command",
            Identifier: commandType.Name,
            DisplayName: command.ToString(),
            AssemblyName: commandType.Assembly.GetName().Name,
            AssemblyPath: TryGetAssemblyLocation(commandType),
            TypeName: commandType.FullName,
            SourceUri: null,
            Notes: targetStableId is null ? null : $"CommandTargetStableId={targetStableId}"));

        if (command is RoutedCommand { OwnerType: { } ownerType })
        {
            references.Add(new WpfCodeReferenceInfo(
                Kind: "CommandOwnerType",
                Identifier: ownerType.Name,
                DisplayName: ownerType.FullName,
                AssemblyName: ownerType.Assembly.GetName().Name,
                AssemblyPath: TryGetAssemblyLocation(ownerType),
                TypeName: ownerType.FullName,
                SourceUri: null,
                Notes: null));
        }

        if (target is not null)
        {
            var targetType = target.GetType();
            references.Add(new WpfCodeReferenceInfo(
                Kind: "CommandTargetType",
                Identifier: targetType.Name,
                DisplayName: targetType.FullName,
                AssemblyName: targetType.Assembly.GetName().Name,
                AssemblyPath: TryGetAssemblyLocation(targetType),
                TypeName: targetType.FullName,
                SourceUri: TryGetBaseUri(target),
                Notes: BuildStableIdNote(targetStableId)));
        }

        return references;
    }

    private static IEnumerable<DependencyObject> EnumerateSelfAndAncestors(DependencyObject start)
    {
        var current = start;
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);

        while (visited.Add(current))
        {
            yield return current;

            current = GetLogicalParent(current)
                ?? GetVisualParent(current)
                ?? (current as FrameworkElement)?.TemplatedParent
                ?? (current as FrameworkContentElement)?.Parent;

            if (current is null)
            {
                yield break;
            }
        }
    }

    private static DependencyObject? GetVisualParent(DependencyObject obj)
    {
        try
        {
            return VisualTreeHelper.GetParent(obj);
        }
        catch
        {
            return null;
        }
    }

    private static bool HasMatchingCommandBinding(DependencyObject candidate, ICommand command)
    {
        var bindings = candidate switch
        {
            UIElement uiElement => uiElement.CommandBindings,
            ContentElement contentElement => contentElement.CommandBindings,
            UIElement3D uiElement3D => uiElement3D.CommandBindings,
            _ => null
        };

        if (bindings is null || bindings.Count == 0)
        {
            return false;
        }

        return bindings
            .OfType<CommandBinding>()
            .Any(binding => DoesCommandBindingMatch(binding.Command, command));
    }

    private static bool DoesCommandBindingMatch(ICommand? bindingCommand, ICommand command)
    {
        if (bindingCommand is null)
        {
            return false;
        }

        if (ReferenceEquals(bindingCommand, command))
        {
            return true;
        }

        if (bindingCommand is RoutedCommand bindingRoutedCommand
            && command is RoutedCommand routedCommand)
        {
            return string.Equals(bindingRoutedCommand.Name, routedCommand.Name, StringComparison.Ordinal)
                && bindingRoutedCommand.OwnerType == routedCommand.OwnerType;
        }

        return bindingCommand.GetType() == command.GetType()
            && string.Equals(bindingCommand.ToString(), command.ToString(), StringComparison.Ordinal);
    }

    private static object? TryReadLocalValue(DependencyObject obj, DependencyProperty property)
    {
        try
        {
            var localValue = obj.ReadLocalValue(property);
            return ReferenceEquals(localValue, DependencyProperty.UnsetValue)
                ? null
                : localValue;
        }
        catch
        {
            return null;
        }
    }

    private static object? TryGetDynamicResourceKey(object? localValue)
    {
        if (localValue is null)
        {
            return null;
        }

        var type = localValue.GetType();
        if (type.FullName?.Contains("ResourceReferenceExpression", StringComparison.Ordinal) != true
            && !type.Name.Contains("ResourceReferenceExpression", StringComparison.Ordinal))
        {
            return null;
        }

        return TryReadMember(localValue, "ResourceKey")
            ?? TryReadMember(localValue, "_resourceKey")
            ?? TryReadMember(localValue, "resourceKey");
    }

    private static object? TryReadMember(object instance, string memberName)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        try
        {
            if (instance.GetType().GetProperty(memberName, flags) is { } property)
            {
                return property.GetValue(instance);
            }
        }
        catch
        {
        }

        try
        {
            if (instance.GetType().GetField(memberName, flags) is { } field)
            {
                return field.GetValue(instance);
            }
        }
        catch
        {
        }

        return null;
    }

    private static ResourceLookupResult? TryFindResourceLookup(
        DependencyObject obj,
        object? value,
        object? resourceKey)
    {
        foreach (var scope in EnumerateResourceDictionaryScopes(obj))
        {
            if (resourceKey is not null
                && TryFindResourceByKey(scope.Dictionary, resourceKey, out var matchedKey, out var notes))
            {
                return new ResourceLookupResult(
                    Key: matchedKey,
                    Scope: scope.Scope,
                    SourceDescription: scope.Description,
                    Notes: notes);
            }

            if (resourceKey is null
                && CanInferResourceLookupFromValue(value)
                && TryFindResourceByValue(scope.Dictionary, value, out matchedKey, out notes))
            {
                return new ResourceLookupResult(
                    Key: matchedKey,
                    Scope: scope.Scope,
                    SourceDescription: scope.Description,
                    Notes: notes);
            }
        }

        return null;
    }

    private static IEnumerable<ResourceDictionaryScope> EnumerateResourceDictionaryScopes(DependencyObject obj)
    {
        var seen = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);

        bool TryTrack(ResourceDictionary dictionary) => seen.Add(dictionary);

        if (obj is FrameworkElement frameworkElement)
        {
            if (TryTrack(frameworkElement.Resources))
            {
                yield return new ResourceDictionaryScope(
                    Scope: "Element",
                    OwnerTypeName: frameworkElement.GetType().Name,
                    Dictionary: frameworkElement.Resources,
                    Description: BuildResourceScopeDescription("Element", frameworkElement.GetType().Name, frameworkElement.Resources));
            }

            if (frameworkElement.Style is { Resources: { } styleResources } && TryTrack(styleResources))
            {
                yield return new ResourceDictionaryScope(
                    Scope: "Style",
                    OwnerTypeName: frameworkElement.Style.GetType().Name,
                    Dictionary: styleResources,
                    Description: BuildResourceScopeDescription("Style", frameworkElement.Style.GetType().Name, styleResources));
            }
        }
        else if (obj is FrameworkContentElement frameworkContentElement)
        {
            if (TryTrack(frameworkContentElement.Resources))
            {
                yield return new ResourceDictionaryScope(
                    Scope: "ContentElement",
                    OwnerTypeName: frameworkContentElement.GetType().Name,
                    Dictionary: frameworkContentElement.Resources,
                    Description: BuildResourceScopeDescription("ContentElement", frameworkContentElement.GetType().Name, frameworkContentElement.Resources));
            }

            if (frameworkContentElement.Style is { Resources: { } styleResources } && TryTrack(styleResources))
            {
                yield return new ResourceDictionaryScope(
                    Scope: "Style",
                    OwnerTypeName: frameworkContentElement.Style.GetType().Name,
                    Dictionary: styleResources,
                    Description: BuildResourceScopeDescription("Style", frameworkContentElement.Style.GetType().Name, styleResources));
            }
        }

        if (obj is Control { Template: { } template } && TryTrack(template.Resources))
        {
            yield return new ResourceDictionaryScope(
                Scope: "Template",
                OwnerTypeName: template.GetType().Name,
                Dictionary: template.Resources,
                Description: BuildResourceScopeDescription("Template", template.GetType().Name, template.Resources));
        }

        foreach (var ancestor in EnumerateSelfAndAncestors(obj).Skip(1))
        {
            switch (ancestor)
            {
                case FrameworkElement ancestorElement when TryTrack(ancestorElement.Resources):
                    yield return new ResourceDictionaryScope(
                        Scope: "AncestorElement",
                        OwnerTypeName: ancestorElement.GetType().Name,
                        Dictionary: ancestorElement.Resources,
                        Description: BuildResourceScopeDescription("AncestorElement", ancestorElement.GetType().Name, ancestorElement.Resources));
                    break;

                case FrameworkContentElement ancestorContentElement when TryTrack(ancestorContentElement.Resources):
                    yield return new ResourceDictionaryScope(
                        Scope: "AncestorContentElement",
                        OwnerTypeName: ancestorContentElement.GetType().Name,
                        Dictionary: ancestorContentElement.Resources,
                        Description: BuildResourceScopeDescription("AncestorContentElement", ancestorContentElement.GetType().Name, ancestorContentElement.Resources));
                    break;
            }
        }

        var ownerWindow = TryGetContainingWindow(obj);
        if (ownerWindow is not null && TryTrack(ownerWindow.Resources))
        {
            yield return new ResourceDictionaryScope(
                Scope: "Window",
                OwnerTypeName: ownerWindow.GetType().Name,
                Dictionary: ownerWindow.Resources,
                Description: BuildResourceScopeDescription("Window", ownerWindow.GetType().Name, ownerWindow.Resources));
        }

        if (Application.Current is { Resources: { } appResources } && TryTrack(appResources))
        {
            yield return new ResourceDictionaryScope(
                Scope: "Application",
                OwnerTypeName: nameof(Application),
                Dictionary: appResources,
                Description: BuildResourceScopeDescription("Application", nameof(Application), appResources));
        }
    }

    private static string BuildResourceScopeDescription(string scope, string ownerTypeName, ResourceDictionary dictionary) =>
        string.IsNullOrWhiteSpace(dictionary.Source?.ToString())
            ? $"{scope}:{ownerTypeName}"
            : $"{scope}:{ownerTypeName}:{dictionary.Source}";

    private static bool TryFindResourceByKey(
        ResourceDictionary dictionary,
        object resourceKey,
        out object? matchedKey,
        out string? notes)
    {
        try
        {
            if (dictionary.Contains(resourceKey))
            {
                matchedKey = resourceKey;
                notes = null;
                return true;
            }
        }
        catch
        {
        }

        foreach (System.Collections.DictionaryEntry entry in dictionary)
        {
            if (AreResourceKeysEquivalent(entry.Key, resourceKey))
            {
                matchedKey = entry.Key;
                notes = null;
                return true;
            }
        }

        for (var index = 0; index < dictionary.MergedDictionaries.Count; index++)
        {
            if (TryFindResourceByKey(dictionary.MergedDictionaries[index], resourceKey, out matchedKey, out var mergedNotes))
            {
                notes = mergedNotes is null
                    ? $"Resolved via merged dictionary #{index}."
                    : $"Resolved via merged dictionary #{index}. {mergedNotes}";
                return true;
            }
        }

        matchedKey = null;
        notes = null;
        return false;
    }

    private static bool TryFindResourceByValue(
        ResourceDictionary dictionary,
        object? value,
        out object? matchedKey,
        out string? notes)
    {
        foreach (System.Collections.DictionaryEntry entry in dictionary)
        {
            if (ReferenceEquals(entry.Value, value))
            {
                matchedKey = entry.Key;
                notes = null;
                return true;
            }
        }

        for (var index = 0; index < dictionary.MergedDictionaries.Count; index++)
        {
            if (TryFindResourceByValue(dictionary.MergedDictionaries[index], value, out matchedKey, out var mergedNotes))
            {
                notes = mergedNotes is null
                    ? $"Matched by value identity in merged dictionary #{index}."
                    : $"Matched by value identity in merged dictionary #{index}. {mergedNotes}";
                return true;
            }
        }

        matchedKey = null;
        notes = null;
        return false;
    }

    private static bool AreResourceKeysEquivalent(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return Equals(left, right)
            || string.Equals(FormatResourceKey(left), FormatResourceKey(right), StringComparison.Ordinal);
    }

    private static bool CanInferResourceLookupFromValue(object? value) =>
        value is not null
        && value is not string
        && !value.GetType().IsValueType;

    private static object? TryInferImplicitResourceKey(
        DependencyProperty property,
        object? value,
        DependencyObject obj)
    {
        if (property == FrameworkElement.StyleProperty || property == FrameworkContentElement.StyleProperty)
        {
            return value is Style style
                ? TryGetStyleTargetType(style)
                : obj.GetType();
        }

        return null;
    }

    private static Type? TryGetStyleTargetType(Style? style)
    {
        if (style is null)
        {
            return null;
        }

        try
        {
            return style.TargetType;
        }
        catch
        {
            return null;
        }
    }

    private static Type? TryGetTemplateTargetType(ControlTemplate? template)
    {
        if (template is null)
        {
            return null;
        }

        try
        {
            return template.TargetType;
        }
        catch
        {
            return null;
        }
    }

    private static T TryBuild<T>(Func<T> builder, T fallback)
    {
        try
        {
            return builder();
        }
        catch
        {
            return fallback;
        }
    }

    private static string ResolveResourceKind(
        ValueSource source,
        bool isDynamic,
        bool isImplicit,
        ResourceLookupResult? lookup)
    {
        if (isDynamic)
        {
            return "DynamicResource";
        }

        if (isImplicit)
        {
            return "ImplicitResource";
        }

        return source.BaseValueSource switch
        {
            BaseValueSource.Style or BaseValueSource.StyleTrigger or BaseValueSource.DefaultStyle or BaseValueSource.DefaultStyleTrigger => "StyleResource",
            BaseValueSource.ParentTemplate or BaseValueSource.ParentTemplateTrigger => "TemplateResource",
            _ when lookup is not null => "StaticResource",
            _ => "ResourceLookup"
        };
    }

    private static string? FormatResourceKey(object? resourceKey) =>
        resourceKey switch
        {
            null => null,
            Type type => type.FullName ?? type.Name,
            ComponentResourceKey componentResourceKey => $"{componentResourceKey.TypeInTargetAssembly?.FullName}:{componentResourceKey.ResourceId}",
            _ => resourceKey.ToString()
        };

    private static Window? TryGetContainingWindow(DependencyObject obj)
    {
        if (obj is Window window)
        {
            return window;
        }

        try
        {
            return Window.GetWindow(obj);
        }
        catch
        {
            return null;
        }
    }

    private static int? TryParseWindowIndex(string ownerWindowStableId)
    {
        const string prefix = "window:";
        if (!ownerWindowStableId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(ownerWindowStableId[prefix.Length..], out var index)
            ? index
            : null;
    }

    private static string? BuildStableIdNote(string? stableId) =>
        stableId is null ? null : $"StableId={stableId}";

    private sealed class RuntimeAssemblyCollector
    {
        private readonly string _appBaseDirectory;
        private readonly Dictionary<string, WpfRuntimeAssemblyInfo> _assemblies = new(StringComparer.OrdinalIgnoreCase);

        public RuntimeAssemblyCollector(string appBaseDirectory)
        {
            _appBaseDirectory = appBaseDirectory;
        }

        public void Record(Type type, string discoverySource)
        {
            var assembly = type.Assembly;
            var assemblyName = assembly.GetName();
            var location = GetAssemblyLocation(assembly);
            var key = !string.IsNullOrWhiteSpace(location)
                ? location
                : assemblyName.FullName ?? assemblyName.Name ?? type.FullName ?? type.Name;

            if (_assemblies.TryGetValue(key, out var existing))
            {
                if (GetDiscoverySourcePriority(discoverySource) < GetDiscoverySourcePriority(existing.DiscoverySource))
                {
                    _assemblies[key] = existing with
                    {
                        RepresentativeTypeName = type.FullName ?? type.Name,
                        DiscoverySource = discoverySource
                    };
                }

                return;
            }

            _assemblies[key] = new WpfRuntimeAssemblyInfo(
                AssemblyName: assemblyName.Name ?? "<unknown>",
                AssemblyPath: location,
                AssemblyVersion: assemblyName.Version?.ToString(),
                RepresentativeTypeName: type.FullName ?? type.Name,
                DiscoverySource: discoverySource,
                IsDynamic: assembly.IsDynamic,
                IsInAppDirectory: IsInAppDirectory(location));
        }

        public List<WpfRuntimeAssemblyInfo> ToList() =>
            _assemblies.Values
                .OrderByDescending(assembly => assembly.IsInAppDirectory)
                .ThenBy(assembly => GetDiscoverySourcePriority(assembly.DiscoverySource))
                .ThenBy(assembly => assembly.AssemblyName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private bool IsInAppDirectory(string? assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                return false;
            }

            var appBasePath = Path.GetFullPath(_appBaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullAssemblyPath = Path.GetFullPath(assemblyPath);
            return fullAssemblyPath.StartsWith(appBasePath, StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetAssemblyLocation(System.Reflection.Assembly assembly)
        {
            if (assembly.IsDynamic)
            {
                return null;
            }

            try
            {
                return string.IsNullOrWhiteSpace(assembly.Location) ? null : assembly.Location;
            }
            catch
            {
                return null;
            }
        }

        private static int GetDiscoverySourcePriority(string discoverySource) => discoverySource switch
        {
            "Window" => 0,
            "Visual" => 1,
            "DataContext" => 2,
            _ => 3
        };
    }
}
