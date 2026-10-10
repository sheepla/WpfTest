using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using WpfInspector.Core.Models;

namespace WpfInspector.UITests;

[Collection("WpfApp")]
public sealed class ObservabilitySurfaceTests(InProcessWpfAppFixture fixture) : IClassFixture<InProcessWpfAppFixture>, IAsyncLifetime
{
    private readonly InProcessWpfAppFixture _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.ReconnectHelperAsync();
        await _fixture.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Collector_payload_includes_observability_metadata_for_fixture_surfaces()
    {
        var inspection = await _fixture.InspectCurrentProcessAsync();

        Assert.Equal(WpfInspectionSchema.CurrentVersion, inspection.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(inspection.DispatcherState));
        Assert.NotNull(inspection.Dispatcher);
        Assert.True(inspection.Dispatcher!.ThreadId > 0);
        Assert.Equal("STA", inspection.Dispatcher.ApartmentState);

        var window = Assert.Single(inspection.Windows, candidate => candidate.Title == "WpfInspector");
        Assert.Equal(WpfInspectionSchema.CurrentVersion, window.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(window.StableId));
        Assert.NotNull(window.RootElement);

        var elements = EnumerateElements(window.RootElement!).ToList();
        Assert.NotEmpty(elements);
        Assert.Equal(
            elements.Count,
            elements
                .Select(element => element.StableId)
                .Where(static stableId => !string.IsNullOrWhiteSpace(stableId))
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(elements, element => Assert.Equal(WpfInspectionSchema.CurrentVersion, element.SchemaVersion));

        var validationTextBox = FindElement(window.RootElement!, "ValidationFixtureTextBox");
        Assert.NotNull(validationTextBox.Validation);
        Assert.True(validationTextBox.Validation!.HasError);
        Assert.NotEmpty(validationTextBox.Validation.Errors);
        Assert.Contains(validationTextBox.ChildCollections, collection => collection.Kind == "Visual");
        Assert.Contains(validationTextBox.ChildCollections, collection => collection.Kind == "Logical");

        var validationBinding = Assert.Single(
            validationTextBox.ObservedProperties,
            property => property.PropertyName == "Text");
        Assert.NotNull(validationBinding.Binding);
        Assert.Equal("Binding", validationBinding.Binding!.BindingKind);
        Assert.Equal("ValidatedToken", validationBinding.Binding.Path);
        Assert.True(validationBinding.Binding.HasError);
        Assert.NotEmpty(validationBinding.Binding.Errors);

        var contextMenuButton = FindElement(window.RootElement!, "ContextMenuFixtureButton");
        var auxiliaryChildren = Assert.Single(
            contextMenuButton.ChildCollections,
            collection => collection.Kind == "Auxiliary");
        Assert.NotEmpty(auxiliaryChildren.ChildReferences);
        Assert.Contains(
            auxiliaryChildren.ChildReferences,
            child => string.Equals(child.TypeName, "ContextMenu", StringComparison.Ordinal));

        var priorityText = FindElement(window.RootElement!, "PriorityBindingFixtureText");
        var priorityBinding = Assert.Single(
            priorityText.ObservedProperties,
            property => property.PropertyName == "Text");
        Assert.NotNull(priorityBinding.Binding);
        Assert.Equal("PriorityBinding", priorityBinding.Binding!.BindingKind);
        Assert.True(priorityBinding.Binding.ChildBindings.Count >= 2);

        var virtualizedList = FindElement(window.RootElement!, "VirtualizedItemsListBox");
        Assert.NotNull(virtualizedList.ItemsControl);
        Assert.Equal(250, virtualizedList.ItemsControl!.ItemCount);
        Assert.NotNull(virtualizedList.ItemsControl.Virtualization);
        Assert.True(virtualizedList.ItemsControl.Virtualization!.IsVirtualizing);
        Assert.Equal("Recycling", virtualizedList.ItemsControl.Virtualization.VirtualizationMode);

        var asyncButton = FindElement(window.RootElement!, "RunAsyncFixtureButton");
        Assert.NotNull(asyncButton.Command);
        Assert.True(asyncButton.Command!.CanExecute);
        Assert.Contains("AsyncDelegateCommand", asyncButton.Command.CommandTypeName ?? string.Empty, StringComparison.Ordinal);
        Assert.NotEmpty(asyncButton.Command.CodeReferences);

        var interopHost = FindElement(window.RootElement!, "FixtureHwndHostSurface");
        Assert.NotNull(interopHost.InteropBoundary);
        Assert.Equal("HwndHost", interopHost.InteropBoundary!.BoundaryKind);
        Assert.True(interopHost.InteropBoundary.IsExternalBoundary);
        Assert.False(string.IsNullOrWhiteSpace(interopHost.InteropBoundary.Handle));
    }

    [Fact]
    public async Task Rpc_surface_reports_element_binding_command_and_popup_details()
    {
        var elementEnvelope = await _fixture.SendSuccessfulObjectAsync(
            "get_element_details",
            new
            {
                elementName = "VirtualizedItemsListBox"
            });
        var elementDetails = GetRequiredObject(elementEnvelope, "Details");
        Assert.Equal("ListBox", GetRequiredString(elementDetails, "TypeName"));
        Assert.False(string.IsNullOrWhiteSpace(GetRequiredString(elementDetails, "StableId")));
        Assert.Equal(250, GetRequiredInt32(elementDetails, "ItemCount"));
        Assert.True(GetRequiredInt32(elementDetails, "SelectedIndex") >= 0);

        var validationEnvelope = await _fixture.SendSuccessfulObjectAsync(
            "get_binding_details",
            new
            {
                elementName = "ValidationFixtureTextBox",
                propertyName = "Text"
            });
        var validationBindings = GetRequiredArray(validationEnvelope, "Bindings")
            .OfType<JsonObject>()
            .ToList();
        var validationBinding = Assert.Single(validationBindings);
        Assert.Equal("Binding", GetRequiredString(validationBinding, "BindingKind"));
        Assert.Equal("ValidatedToken", GetRequiredString(validationBinding, "Path"));
        Assert.True(GetRequiredBoolean(validationBinding, "HasError"));
        Assert.NotEmpty(GetRequiredArray(validationBinding, "Errors"));

        var priorityEnvelope = await _fixture.SendSuccessfulObjectAsync(
            "get_binding_details",
            new
            {
                elementName = "PriorityBindingFixtureText",
                propertyName = "Text"
            });
        var priorityBindings = GetRequiredArray(priorityEnvelope, "Bindings")
            .OfType<JsonObject>()
            .ToList();
        var priorityBinding = Assert.Single(priorityBindings);
        Assert.Equal("PriorityBinding", GetRequiredString(priorityBinding, "BindingKind"));
        Assert.True(GetRequiredArray(priorityBinding, "ChildBindings").Count >= 2);

        var commandState = await _fixture.SendSuccessfulObjectAsync(
            "get_command_state",
            new
            {
                elementName = "RunAsyncFixtureButton"
            });
        Assert.True(GetRequiredBoolean(commandState, "CanExecute"));
        Assert.Contains("AsyncDelegateCommand", GetRequiredString(commandState, "CommandTypeName"), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(GetRequiredString(commandState, "ElementStableId")));

        var popupEnvelope = await _fixture.SendSuccessfulObjectAsync("list_popups");
        var popups = GetRequiredArray(popupEnvelope, "Popups")
            .OfType<JsonObject>()
            .ToList();
        Assert.Contains(
            popups,
            popup =>
                GetRequiredString(popup, "Kind") == "Popup" &&
                GetRequiredString(popup, "ElementName") == "PopupAnchorButton" &&
                GetRequiredBoolean(popup, "IsOpen"));
        Assert.Contains(
            popups,
            popup =>
                GetRequiredString(popup, "Kind") == "ContextMenu" &&
                GetRequiredString(popup, "ElementName") == "ContextMenuFixtureButton");
    }

    [Fact]
    public async Task Rpc_surface_supports_full_snapshot_and_element_search()
    {
        var inspection = await _fixture.SendSuccessfulObjectAsync("get_inspection_result");
        Assert.Equal(WpfInspectionSchema.CurrentVersion, GetRequiredString(inspection, "SchemaVersion"));
        Assert.NotNull(inspection["Dispatcher"]);
        var windows = GetRequiredArray(inspection, "Windows")
            .OfType<JsonObject>()
            .ToList();
        Assert.Contains(windows, window => GetRequiredString(window, "Title") == "WpfInspector");

        var validationSearch = await _fixture.SendSuccessfulObjectAsync(
            "find_elements",
            new
            {
                query = "ValidationFixtureTextBox",
                hasValidationError = true,
                matchMode = "equals",
                maxResults = 5
            });
        Assert.True(GetRequiredInt32(validationSearch, "TotalMatchCount") >= 1);
        var validationMatches = GetRequiredArray(validationSearch, "Elements")
            .OfType<JsonObject>()
            .ToList();
        Assert.Contains(
            validationMatches,
            match =>
                GetRequiredString(match, "Name") == "ValidationFixtureTextBox" &&
                GetRequiredBoolean(match, "HasValidationError"));

        var prioritySearch = await _fixture.SendSuccessfulObjectAsync(
            "find_elements",
            new
            {
                bindingPath = "PriorityFallbackValue",
                typeName = "TextBlock",
                maxResults = 5
            });
        Assert.True(GetRequiredInt32(prioritySearch, "TotalMatchCount") >= 1);
        var priorityMatches = GetRequiredArray(prioritySearch, "Elements")
            .OfType<JsonObject>()
            .ToList();
        Assert.Contains(
            priorityMatches,
            match => GetRequiredString(match, "Name") == "PriorityBindingFixtureText");
    }

    [Fact]
    public async Task Rpc_surface_supports_selection_wait_trace_and_idle_operations()
    {
        var traceStart = await _fixture.SendSuccessfulObjectAsync("start_trace");
        Assert.True(GetRequiredBoolean(traceStart, "IsActive"));
        Assert.True(GetRequiredInt32(traceStart, "EventCount") >= 1);

        var selection = await _fixture.SendSuccessfulObjectAsync(
            "select_item",
            new
            {
                elementName = "VirtualizedItemsListBox",
                index = 249
            });
        Assert.Equal(249, GetRequiredInt32(selection, "SelectedIndex"));
        Assert.False(string.IsNullOrWhiteSpace(GetRequiredString(selection, "ElementStableId")));

        var waitCondition = await _fixture.SendSuccessfulObjectAsync(
            "wait_for_condition",
            new
            {
                elementName = "VirtualizedItemsListBox",
                propertyName = "SelectedIndex",
                expectedValue = "249",
                timeoutMs = 2_000,
                pollIntervalMs = 50
            });
        Assert.True(GetRequiredBoolean(waitCondition, "Matched"));
        Assert.Equal("249", GetRequiredString(waitCondition, "ActualValue"));

        var waitForIdle = await _fixture.SendSuccessfulObjectAsync(
            "wait_for_idle",
            new
            {
                timeoutMs = 1_000
            });
        Assert.True(GetRequiredBoolean(waitForIdle, "ReachedIdle"));
        Assert.True(GetRequiredInt64(waitForIdle, "WaitedMs") >= 0);

        var traceDetails = await _fixture.SendSuccessfulObjectAsync("get_trace");
        Assert.True(GetRequiredBoolean(traceDetails, "IsActive"));
        var events = GetRequiredArray(traceDetails, "Events")
            .OfType<JsonObject>()
            .ToList();
        Assert.Contains(events, traceEvent => GetRequiredString(traceEvent, "Operation") == "start_trace");
        Assert.Contains(events, traceEvent => GetRequiredString(traceEvent, "Operation") == "select_item");
        Assert.Contains(events, traceEvent => GetRequiredString(traceEvent, "Operation") == "wait_for_condition");
        Assert.Contains(events, traceEvent => GetRequiredString(traceEvent, "Operation") == "wait_for_idle");

        var traceDiff = GetRequiredObject(traceDetails, "Diff");
        Assert.False(string.IsNullOrWhiteSpace(GetRequiredString(traceDiff, "BeforeDispatcherState")));
        Assert.False(string.IsNullOrWhiteSpace(GetRequiredString(traceDiff, "AfterDispatcherState")));

        var traceStop = await _fixture.SendSuccessfulObjectAsync("stop_trace");
        Assert.False(GetRequiredBoolean(traceStop, "IsActive"));
        Assert.True(GetRequiredInt32(traceStop, "EventCount") >= events.Count);
        Assert.NotNull(traceStop["StoppedAtUtc"]);
        Assert.NotNull(traceStop["Diff"]);
    }

    [Fact]
    public async Task Injected_worker_unloads_after_disconnect_and_can_reconnect()
    {
        var baseline = ObserveWorkerUnloadState();
        var baselineWorkerAssemblyCount = baseline.WorkerAssemblyCount;
        var baselineCollectibleWorkerAssemblyCount = baseline.CollectibleWorkerAssemblyCount;
        var baselineNonCollectibleWorkerAssemblyCount =
            baselineWorkerAssemblyCount - baselineCollectibleWorkerAssemblyCount;
        var baselineCollectibleContextCount = baseline.CollectibleContextCount;

        await _fixture.SendSuccessfulObjectAsync("get_inspection_result");
        await _fixture.DisconnectHelperAsync();

        var idleReached = await _fixture.WaitForHelperIdleAsync(TimeSpan.FromSeconds(10));
        Assert.True(idleReached, "Collectible helper worker did not become idle before the timeout.");

        var diagnostics = _fixture.GetHelperDiagnostics();
        Assert.Equal(0, diagnostics.ActiveSessionCount);
        Assert.True(diagnostics.CompletedSessionCount >= 1);
        Assert.True(diagnostics.SuccessfulUnloadCount >= 1, diagnostics.LastError);
        Assert.True(baselineCollectibleWorkerAssemblyCount >= 1);
        Assert.True(baselineCollectibleContextCount >= 1);

        var unloadObserved = await WaitForWorkerUnloadStateAsync(
            baselineNonCollectibleWorkerAssemblyCount,
            TimeSpan.FromSeconds(10));
        Assert.True(unloadObserved, "Worker assemblies did not fully leave the collectible context before the timeout. " + BuildUnloadReport(baselineNonCollectibleWorkerAssemblyCount));

        await _fixture.ReconnectHelperAsync();

        var inspection = await _fixture.SendSuccessfulObjectAsync("get_inspection_result");
        Assert.NotNull(inspection["Windows"]);
    }

    [Fact]
    public async Task Idle_helper_session_unloads_after_disconnect()
    {
        var baseline = ObserveWorkerUnloadState();
        var baselineNonCollectibleWorkerAssemblyCount =
            baseline.WorkerAssemblyCount - baseline.CollectibleWorkerAssemblyCount;

        await _fixture.DisconnectHelperAsync();

        var idleReached = await _fixture.WaitForHelperIdleAsync(TimeSpan.FromSeconds(10));
        Assert.True(idleReached, "Collectible helper worker did not become idle before the timeout.");

        var unloadObserved = await WaitForWorkerUnloadStateAsync(
            expectedWorkerAssemblyCount: baselineNonCollectibleWorkerAssemblyCount,
            timeout: TimeSpan.FromSeconds(10));
        Assert.True(unloadObserved, "Idle helper session did not unload before the timeout. " + BuildUnloadReport(baselineNonCollectibleWorkerAssemblyCount));

        await _fixture.ReconnectHelperAsync();
        var inspection = await _fixture.SendSuccessfulObjectAsync("get_inspection_result");
        Assert.NotNull(inspection["Windows"]);
    }

    private static IEnumerable<WpfElementInfo> EnumerateElements(WpfElementInfo root)
    {
        yield return root;

        foreach (var child in root.Children)
        {
            foreach (var descendant in EnumerateElements(child))
            {
                yield return descendant;
            }
        }
    }

    // Assembly 参照を保持したローカル (List<Assembly> など) は、それ自体が
    // コレクティブ ALC のアンロードを妨げる自己ルートになるため、
    // カウントのみを返す NonInlining メソッドに切り出して観測する。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkerUnloadSnapshot ObserveWorkerUnloadState()
    {
        var workerAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => string.Equals(
                assembly.GetName().Name,
                "WpfInspector.TestRuntime",
                StringComparison.Ordinal))
            .ToList();

        return new WorkerUnloadSnapshot(
            workerAssemblies.Count,
            workerAssemblies.Count(assembly =>
                AssemblyLoadContext.GetLoadContext(assembly)?.IsCollectible == true),
            AssemblyLoadContext.All.Count(context =>
                context.IsCollectible &&
                (context.Name?.StartsWith("WpfInspector.TestRuntime::", StringComparison.Ordinal) ?? false)));
    }

    private string BuildUnloadReport(int expectedCount)
    {
        var snapshot = ObserveWorkerUnloadState();
        var assemblies = string.Join(" | ", AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => string.Equals(
                assembly.GetName().Name,
                "WpfInspector.TestRuntime",
                StringComparison.Ordinal))
            .Select(assembly =>
                $"{assembly.GetName().Version}@{AssemblyLoadContext.GetLoadContext(assembly)?.Name ?? "<null>"}(collectible={AssemblyLoadContext.GetLoadContext(assembly)?.IsCollectible})"));
        var contexts = string.Join(" | ", AssemblyLoadContext.All
            .Where(context => context.IsCollectible)
            .Select(context => context.Name ?? "<unnamed>"));
        var diagnostics = _fixture.GetHelperDiagnostics();
        return $"expected={expectedCount} snapshot={snapshot} assemblies=[{assemblies}] collectibleContexts=[{contexts}] diagnostics={diagnostics}";
    }

    private static async Task<bool> WaitForWorkerUnloadStateAsync(
        int expectedWorkerAssemblyCount,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = ObserveWorkerUnloadState();
            if (snapshot.WorkerAssemblyCount == expectedWorkerAssemblyCount
                && snapshot.CollectibleWorkerAssemblyCount == 0
                && snapshot.CollectibleContextCount == 0)
            {
                return true;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);
        }

        return false;
    }

    private readonly record struct WorkerUnloadSnapshot(
        int WorkerAssemblyCount,
        int CollectibleWorkerAssemblyCount,
        int CollectibleContextCount);

    private static WpfElementInfo FindElement(WpfElementInfo root, string identifier) =>
        EnumerateElements(root)
            .FirstOrDefault(element =>
                string.Equals(element.Name, identifier, StringComparison.Ordinal) ||
                string.Equals(element.AutomationId, identifier, StringComparison.Ordinal))
        ?? throw new Xunit.Sdk.XunitException($"Element '{identifier}' was not found in the collected payload.");

    private static JsonObject GetRequiredObject(JsonObject parent, string propertyName) =>
        parent[propertyName] as JsonObject
        ?? throw new Xunit.Sdk.XunitException($"Property '{propertyName}' was not a JsonObject.");

    private static JsonArray GetRequiredArray(JsonObject parent, string propertyName) =>
        parent[propertyName] as JsonArray
        ?? throw new Xunit.Sdk.XunitException($"Property '{propertyName}' was not a JsonArray.");

    private static string GetRequiredString(JsonObject parent, string propertyName) =>
        parent[propertyName]?.GetValue<string>()
        ?? throw new Xunit.Sdk.XunitException($"Property '{propertyName}' was null or not a string.");

    private static int GetRequiredInt32(JsonObject parent, string propertyName) =>
        parent[propertyName]?.GetValue<int>()
        ?? throw new Xunit.Sdk.XunitException($"Property '{propertyName}' was null or not an Int32.");

    private static long GetRequiredInt64(JsonObject parent, string propertyName) =>
        parent[propertyName]?.GetValue<long>()
        ?? throw new Xunit.Sdk.XunitException($"Property '{propertyName}' was null or not an Int64.");

    private static bool GetRequiredBoolean(JsonObject parent, string propertyName) =>
        parent[propertyName]?.GetValue<bool>()
        ?? throw new Xunit.Sdk.XunitException($"Property '{propertyName}' was null or not a Boolean.");
}
