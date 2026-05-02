using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WpfInspector.Core.Models;
using WpfInspector.InspectionPayload;
using WpfInspector.Protocol;

namespace WpfInspector.TestHelper;

internal static class UiCommandHandler
{
    private const int MaxDepth = 20;
    private const int MaxChildren = 40;
    private const int MaxTraceEvents = 2_000;

    private static readonly object LogSync = new();
    private static readonly object TraceSync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static TraceSession? _traceSession;

    internal static async Task RunAsync(string fullPipeName)
    {
        var pipeName = fullPipeName.Replace(@"\\.\pipe\", string.Empty)
            .TrimStart('\\')
            .TrimStart('.');
        Log($"RunAsync start full='{fullPipeName}' pipe='{pipeName}'");

        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                using var cts = new CancellationTokenSource(30_000);
                Log($"Connecting to server pipe='{pipeName}'");
                await pipe.ConnectAsync(cts.Token);
                Log($"Connected pipe='{pipeName}'");

                using var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024,
                    leaveOpen: true);
                await using var writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                await writer.WriteLineAsync("READY");
                Log($"Sent READY pipe='{pipeName}'");

                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    Log($"Received command='{line}' pipe='{pipeName}'");
                    if (line == "EXIT")
                    {
                        Log($"Received EXIT pipe='{pipeName}'");
                        break;
                    }

                    var response = await DispatchAsync(line);
                    Log($"Sending response length={response.Length} pipe='{pipeName}'");
                    await writer.WriteLineAsync(response);
                }
            }
            catch (Exception exception)
            {
                Log($"Loop exception pipe='{pipeName}' error={exception}");
            }

            await Task.Delay(250);
        }
    }

    private static async Task<string> DispatchAsync(string command)
    {
        PipeRequest? request = null;

        try
        {
            if (PipeProtocol.TryDecodeRequest(command, out request) && request is not null)
            {
                var response = await DispatchRpcAsync(request);
                return PipeProtocol.EncodeResponse(response);
            }

            return DispatchLegacy(command);
        }
        catch (Exception ex)
        {
            if (request is not null)
            {
                return PipeProtocol.EncodeResponse(PipeProtocol.Error(
                    errorCode: "dispatch_failed",
                    errorMessage: $"{ex.GetType().Name}: {ex.Message}",
                    requestId: request.RequestId));
            }

            return $"ERROR:{ex.GetType().Name}:{ex.Message}";
        }
    }

    private static async Task<PipeResponse> DispatchRpcAsync(PipeRequest request)
    {
        switch (request.Operation)
        {
            case "get_inspection_result":
                return BuildInspectionResultResponse(request);
            case "find_elements":
                return BuildFindElementsResponse(request);
            case "get_element_details":
                return BuildElementDetailsResponse(request);
            case "get_binding_details":
                return BuildBindingDetailsResponse(request);
            case "get_command_state":
                return BuildCommandStateResponse(request);
            case "list_popups":
                return BuildListPopupsResponse(request);
            case "focus":
                return BuildActionResponse(
                    request,
                    "focus",
                    element => element switch
                    {
                        IInputElement inputElement => FocusElement(inputElement, element),
                        _ => false
                    });
            case "scroll_into_view":
                return BuildActionResponse(
                    request,
                    "scroll_into_view",
                    element => element switch
                    {
                        FrameworkElement frameworkElement => ScrollIntoView(frameworkElement),
                        _ => false
                    });
            case "select_item":
                return BuildSelectItemResponse(request);
            case "expand":
                return BuildExpandCollapseResponse(request, expand: true);
            case "collapse":
                return BuildExpandCollapseResponse(request, expand: false);
            case "invoke_command":
                return BuildInvokeCommandResponse(request);
            case "start_trace":
                return BuildStartTraceResponse(request);
            case "stop_trace":
                return BuildStopTraceResponse(request);
            case "get_trace":
                return BuildGetTraceResponse(request);
            case "diff_snapshots":
                return BuildDiffSnapshotsResponse(request);
            case "wait_for_idle":
                return await BuildWaitForIdleResponseAsync(request);
            case "wait_for_condition":
                return await BuildWaitForConditionResponseAsync(request);
            default:
                return PipeProtocol.Error(
                    errorCode: "unknown_operation",
                    errorMessage: $"Unknown operation '{request.Operation}'.",
                    requestId: request.RequestId);
        }
    }

    private static string DispatchLegacy(string command)
    {
        if (command == "GET_TREE")
            return CollectTreeJson();
        if (command == "GET_TITLE")
            return GetWindowTitle();
        if (command == "GET_STATUS")
            return GetStatusText();
        if (command.StartsWith("GET_ITEMS:", StringComparison.Ordinal))
            return GetDataGridItemCount(command[10..]);
        if (command.StartsWith("GET_TEXT:", StringComparison.Ordinal))
            return GetText(command[9..]);
        if (command.StartsWith("CLICK:", StringComparison.Ordinal))
            return ClickButton(command[6..]);
        if (command.StartsWith("TYPE:", StringComparison.Ordinal))
            return TypeText(command[5..]);
        return "ERROR:Unknown command";
    }

    private static PipeResponse BuildElementDetailsResponse(PipeRequest request)
    {
        var elementName = GetRequiredStringArgument(request, "elementName");
        if (!TryFindElement(elementName, out var element))
        {
            return PipeProtocol.Error(
                errorCode: "element_not_found",
                errorMessage: $"Element '{elementName}' was not found.",
                requestId: request.RequestId);
        }

        ElementDetails? details = null;
        InvokeOnUiThread(() => details = BuildElementDetails(element));

        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(new ElementDetailsEnvelope(
                elementName,
                details!),
                JsonOptions),
            requestId: request.RequestId);
    }

    private static PipeResponse BuildInspectionResultResponse(PipeRequest request)
    {
        var inspection = SelfInspection.InspectCurrentProcess();
        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(inspection, JsonOptions),
            requestId: request.RequestId);
    }

    private static PipeResponse BuildFindElementsResponse(PipeRequest request)
    {
        var query = GetOptionalStringArgument(request, "query");
        var typeName = GetOptionalStringArgument(request, "typeName");
        var propertyName = GetOptionalStringArgument(request, "propertyName");
        var bindingPath = GetOptionalStringArgument(request, "bindingPath");
        var ownerWindowTitle = GetOptionalStringArgument(request, "ownerWindowTitle");
        var isVisible = GetOptionalBooleanArgument(request, "isVisible");
        var isEnabled = GetOptionalBooleanArgument(request, "isEnabled");
        var hasValidationError = GetOptionalBooleanArgument(request, "hasValidationError");
        var hasCommand = GetOptionalBooleanArgument(request, "hasCommand");
        var includeReferences = GetOptionalBooleanArgument(request, "includeReferences") ?? false;
        var maxResults = Math.Clamp(GetOptionalIntArgument(request, "maxResults") ?? 25, 1, 200);
        var matchMode = NormalizeElementSearchMatchMode(GetOptionalStringArgument(request, "matchMode"));

        var hasCriteria =
            !string.IsNullOrWhiteSpace(query) ||
            !string.IsNullOrWhiteSpace(typeName) ||
            !string.IsNullOrWhiteSpace(propertyName) ||
            !string.IsNullOrWhiteSpace(bindingPath) ||
            !string.IsNullOrWhiteSpace(ownerWindowTitle) ||
            isVisible is not null ||
            isEnabled is not null ||
            hasValidationError is not null ||
            hasCommand is not null;
        if (!hasCriteria)
        {
            return PipeProtocol.Error(
                errorCode: "invalid_arguments",
                errorMessage: "Provide at least one search criterion.",
                requestId: request.RequestId);
        }

        var criteria = new FindElementsCriteria(
            Query: query,
            MatchMode: matchMode,
            TypeName: typeName,
            PropertyName: propertyName,
            BindingPath: bindingPath,
            OwnerWindowTitle: ownerWindowTitle,
            IsVisible: isVisible,
            IsEnabled: isEnabled,
            HasValidationError: hasValidationError,
            HasCommand: hasCommand,
            IncludeReferences: includeReferences,
            MaxResults: maxResults);
        var inspection = SelfInspection.InspectCurrentProcess();
        var matches = new List<FoundElementSummary>();
        var scannedElementCount = 0;
        var totalMatchCount = 0;

        foreach (var window in inspection.Windows)
        {
            if (window.RootElement is null)
            {
                continue;
            }

            foreach (var element in EnumerateElements(window.RootElement))
            {
                scannedElementCount++;

                if (!criteria.IncludeReferences && element.IsReference)
                {
                    continue;
                }

                if (!MatchesElementSearchCriteria(element, window, criteria, out var matchedFields))
                {
                    continue;
                }

                totalMatchCount++;
                if (matches.Count >= criteria.MaxResults)
                {
                    continue;
                }

                matches.Add(BuildFoundElementSummary(window, element, matchedFields));
            }
        }

        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(
                new FindElementsResult(
                    Criteria: criteria,
                    ScannedElementCount: scannedElementCount,
                    TotalMatchCount: totalMatchCount,
                    Truncated: totalMatchCount > matches.Count,
                    Elements: matches),
                JsonOptions),
            requestId: request.RequestId);
    }

    private static PipeResponse BuildBindingDetailsResponse(PipeRequest request)
    {
        var elementName = GetRequiredStringArgument(request, "elementName");
        var propertyName = GetOptionalStringArgument(request, "propertyName");

        if (!TryFindElement(elementName, out var element))
        {
            return PipeProtocol.Error(
                errorCode: "element_not_found",
                errorMessage: $"Element '{elementName}' was not found.",
                requestId: request.RequestId);
        }

        List<BindingDetails>? bindings = null;
        InvokeOnUiThread(() => bindings = CollectBindingDetails(element, propertyName));
        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(new BindingDetailsEnvelope(
                elementName,
                propertyName,
                bindings!),
                JsonOptions),
            requestId: request.RequestId);
    }

    private static PipeResponse BuildCommandStateResponse(PipeRequest request)
    {
        var elementName = GetRequiredStringArgument(request, "elementName");
        if (!TryFindElement(elementName, out var element))
        {
            return PipeProtocol.Error(
                errorCode: "element_not_found",
                errorMessage: $"Element '{elementName}' was not found.",
                requestId: request.RequestId);
        }

        CommandStateResult? commandState = null;
        InvokeOnUiThread(() => commandState = BuildCommandState(element));
        if (commandState is null)
        {
            return PipeProtocol.Error(
                errorCode: "no_command",
                errorMessage: $"Element '{elementName}' does not expose ICommandSource.",
                requestId: request.RequestId);
        }

        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(commandState, JsonOptions),
            requestId: request.RequestId);
    }

    private static PipeResponse BuildListPopupsResponse(PipeRequest request)
    {
        PopupListResult? result = null;
        InvokeOnUiThread(() => result = new PopupListResult(CollectPopupStates()));
        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(result, JsonOptions),
            requestId: request.RequestId);
    }

    private static PipeResponse BuildActionResponse(PipeRequest request, string action, Func<DependencyObject, bool> execute)
    {
        var elementName = GetRequiredStringArgument(request, "elementName");
        if (!TryFindElement(elementName, out var element))
        {
            return PipeProtocol.Error(
                errorCode: "element_not_found",
                errorMessage: $"Element '{elementName}' was not found.",
                requestId: request.RequestId);
        }

        var success = false;
        ActionResult? result = null;
        InvokeOnUiThread(() =>
        {
            success = execute(element);
            if (success)
            {
                var elementInfo = DescribeElementCore(element);
                result = new ActionResult(
                    Action: action,
                    ElementName: elementInfo.ElementName,
                    ElementStableId: elementInfo.ElementStableId,
                    Succeeded: true);
            }
        });

        if (!success)
        {
            return PipeProtocol.Error(
                errorCode: "action_failed",
                errorMessage: $"Action '{action}' failed for '{elementName}'.",
                requestId: request.RequestId);
        }

        RecordTraceEvent(action, element, null);
        RefreshTraceSubscriptionsIfActive();

        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(result, JsonOptions),
            requestId: request.RequestId);
    }

    private static async Task<PipeResponse> BuildWaitForIdleResponseAsync(PipeRequest request)
    {
        var timeoutMs = GetOptionalIntArgument(request, "timeoutMs") ?? 1000;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return PipeProtocol.Error(
                errorCode: "no_dispatcher",
                errorMessage: "Application dispatcher is unavailable.",
                requestId: request.RequestId);
        }

        var stopwatch = Stopwatch.StartNew();
        var idleOperation = dispatcher.InvokeAsync(
            () => stopwatch.ElapsedMilliseconds,
            DispatcherPriority.ApplicationIdle);
        var timeoutTask = Task.Delay(timeoutMs);
        var completed = await Task.WhenAny(idleOperation.Task, timeoutTask);

        if (completed != idleOperation.Task)
        {
            RecordTraceEvent("wait_for_idle_timeout", elementName: null, details: $"timeoutMs={timeoutMs}; waitedMs={stopwatch.ElapsedMilliseconds}");
            RefreshTraceSubscriptionsIfActive();
            return PipeProtocol.Error(
                errorCode: "timeout",
                errorMessage: $"Dispatcher did not reach idle within {timeoutMs} ms.",
                requestId: request.RequestId,
                data: JsonSerializer.SerializeToNode(new WaitForIdleResult(
                    TimeoutMs: timeoutMs,
                    ReachedIdle: false,
                    WaitedMs: stopwatch.ElapsedMilliseconds),
                    JsonOptions));
        }

        var waitedMs = await idleOperation.Task;
        RecordTraceEvent("wait_for_idle", elementName: null, details: $"timeoutMs={timeoutMs}; waitedMs={waitedMs}");
        RefreshTraceSubscriptionsIfActive();
        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(new WaitForIdleResult(
                TimeoutMs: timeoutMs,
                ReachedIdle: true,
                WaitedMs: waitedMs),
            JsonOptions),
            requestId: request.RequestId);
    }

    private static async Task<PipeResponse> BuildWaitForConditionResponseAsync(PipeRequest request)
    {
        var elementName = GetRequiredStringArgument(request, "elementName");
        var propertyName = GetRequiredStringArgument(request, "propertyName");
        var expectedValue = GetOptionalStringArgument(request, "expectedValue");
        var matchMode = NormalizeMatchMode(GetOptionalStringArgument(request, "matchMode"));
        var timeoutMs = Math.Max(0, GetOptionalIntArgument(request, "timeoutMs") ?? 2_000);
        var pollIntervalMs = Math.Clamp(GetOptionalIntArgument(request, "pollIntervalMs") ?? 100, 10, 1_000);

        var stopwatch = Stopwatch.StartNew();
        ConditionEvaluationResult? lastEvaluation = null;

        while (true)
        {
            lastEvaluation = EvaluateCondition(elementName, propertyName, expectedValue, matchMode);
            if (lastEvaluation.PropertyMissing)
            {
                return PipeProtocol.Error(
                    errorCode: "unknown_property",
                    errorMessage: $"Property '{propertyName}' is not available on '{elementName}'.",
                    requestId: request.RequestId,
                    data: JsonSerializer.SerializeToNode(new WaitForConditionResult(
                        ElementName: lastEvaluation.ElementName ?? elementName,
                        ElementStableId: lastEvaluation.ElementStableId,
                        PropertyName: propertyName,
                        MatchMode: matchMode,
                        ExpectedValue: expectedValue,
                        ActualValue: lastEvaluation.ActualValue,
                        Matched: false,
                        WaitedMs: stopwatch.ElapsedMilliseconds),
                        JsonOptions));
            }

            if (lastEvaluation.Matched)
            {
                RecordTraceEvent(
                    "wait_for_condition",
                    lastEvaluation.Element,
                    $"{propertyName} {matchMode} '{expectedValue ?? string.Empty}' => '{lastEvaluation.ActualValue ?? "null"}'");
                RefreshTraceSubscriptionsIfActive();
                return PipeProtocol.Success(
                    data: JsonSerializer.SerializeToNode(new WaitForConditionResult(
                        ElementName: lastEvaluation.ElementName ?? elementName,
                        ElementStableId: lastEvaluation.ElementStableId,
                        PropertyName: propertyName,
                        MatchMode: matchMode,
                        ExpectedValue: expectedValue,
                        ActualValue: lastEvaluation.ActualValue,
                        Matched: true,
                        WaitedMs: stopwatch.ElapsedMilliseconds),
                        JsonOptions),
                    requestId: request.RequestId);
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs)
            {
                var errorCode = lastEvaluation.ElementMissing ? "element_not_found" : "timeout";
                RecordTraceEvent(
                    "wait_for_condition_timeout",
                    lastEvaluation.Element,
                    $"{propertyName} {matchMode} '{expectedValue ?? string.Empty}' => '{lastEvaluation.ActualValue ?? "null"}'; timeoutMs={timeoutMs}");
                return PipeProtocol.Error(
                    errorCode: errorCode,
                    errorMessage: lastEvaluation.ElementMissing
                        ? $"Element '{elementName}' was not found before the timeout elapsed."
                        : $"Condition on '{elementName}' did not match within {timeoutMs} ms.",
                    requestId: request.RequestId,
                    data: JsonSerializer.SerializeToNode(new WaitForConditionResult(
                        ElementName: lastEvaluation.ElementName ?? elementName,
                        ElementStableId: lastEvaluation.ElementStableId,
                        PropertyName: propertyName,
                        MatchMode: matchMode,
                        ExpectedValue: expectedValue,
                        ActualValue: lastEvaluation.ActualValue,
                        Matched: false,
                        WaitedMs: stopwatch.ElapsedMilliseconds),
                        JsonOptions));
            }

            RefreshTraceSubscriptionsIfActive();
            await Task.Delay(pollIntervalMs);
        }
    }

    private static PipeResponse BuildSelectItemResponse(PipeRequest request)
    {
        var elementName = GetRequiredStringArgument(request, "elementName");
        if (!TryFindElement(elementName, out var element) || element is not Selector selector)
        {
            return PipeProtocol.Error(
                errorCode: "selector_not_found",
                errorMessage: $"Selector '{elementName}' was not found.",
                requestId: request.RequestId);
        }

        var index = GetOptionalIntArgument(request, "index");
        var itemText = GetOptionalStringArgument(request, "itemText");
        var selected = false;
        SelectionResult? result = null;

        InvokeOnUiThread(() =>
        {
            if (index is >= 0 && index.Value < selector.Items.Count)
            {
                selector.SelectedIndex = index.Value;
                selected = selector.SelectedIndex == index.Value;
                if (selected)
                {
                    var elementInfo = DescribeElementCore(element);
                    result = new SelectionResult(
                        ElementName: elementInfo.ElementName,
                        ElementStableId: elementInfo.ElementStableId,
                        SelectedIndex: selector.SelectedIndex,
                        SelectedItemSummary: SummarizeValue(selector.SelectedItem));
                }
                return;
            }

            if (!string.IsNullOrWhiteSpace(itemText))
            {
                for (var i = 0; i < selector.Items.Count; i++)
                {
                    if (string.Equals(SummarizeValue(selector.Items[i]), itemText, StringComparison.Ordinal))
                    {
                        selector.SelectedIndex = i;
                        selected = selector.SelectedIndex == i;
                        if (selected)
                        {
                            var elementInfo = DescribeElementCore(element);
                            result = new SelectionResult(
                                ElementName: elementInfo.ElementName,
                                ElementStableId: elementInfo.ElementStableId,
                                SelectedIndex: selector.SelectedIndex,
                                SelectedItemSummary: SummarizeValue(selector.SelectedItem));
                        }
                        return;
                    }
                }
            }
        });

        if (!selected)
        {
            return PipeProtocol.Error(
                errorCode: "selection_failed",
                errorMessage: $"Could not select an item in '{elementName}'.",
                requestId: request.RequestId);
        }

        RecordTraceEvent("select_item", element, $"selectedIndex={result!.SelectedIndex}");
        RefreshTraceSubscriptionsIfActive();
        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(result, JsonOptions),
            requestId: request.RequestId);
    }

    private static PipeResponse BuildExpandCollapseResponse(PipeRequest request, bool expand)
    {
        var elementName = GetRequiredStringArgument(request, "elementName");
        if (!TryFindElement(elementName, out var element))
        {
            return PipeProtocol.Error(
                errorCode: "element_not_found",
                errorMessage: $"Element '{elementName}' was not found.",
                requestId: request.RequestId);
        }

        var changed = false;
        InvokeOnUiThread(() =>
        {
            switch (element)
            {
                case Expander expander:
                    expander.IsExpanded = expand;
                    changed = expander.IsExpanded == expand;
                    break;
                case TreeViewItem treeViewItem:
                    treeViewItem.IsExpanded = expand;
                    changed = treeViewItem.IsExpanded == expand;
                    break;
                case MenuItem menuItem:
                    menuItem.IsSubmenuOpen = expand;
                    changed = menuItem.IsSubmenuOpen == expand;
                    break;
            }
        });

        if (!changed)
        {
            return PipeProtocol.Error(
                errorCode: "toggle_failed",
                errorMessage: $"Element '{elementName}' does not support {(expand ? "expand" : "collapse")}.",
                requestId: request.RequestId);
        }

        RecordTraceEvent(expand ? "expand" : "collapse", element, null);
        RefreshTraceSubscriptionsIfActive();
        ToggleResult? result = null;
        InvokeOnUiThread(() =>
        {
            var elementInfo = DescribeElementCore(element);
            result = new ToggleResult(
                ElementName: elementInfo.ElementName,
                ElementStableId: elementInfo.ElementStableId,
                IsExpanded: expand);
        });
        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(result, JsonOptions),
            requestId: request.RequestId);
    }

    private static PipeResponse BuildInvokeCommandResponse(PipeRequest request)
    {
        var elementName = GetRequiredStringArgument(request, "elementName");
        if (!TryFindElement(elementName, out var element))
        {
            return PipeProtocol.Error(
                errorCode: "element_not_found",
                errorMessage: $"Element '{elementName}' was not found.",
                requestId: request.RequestId);
        }

        var invoked = false;
        var details = string.Empty;
        InvokeOnUiThread(() =>
        {
            if (element is ICommandSource commandSource && commandSource.Command is not null)
            {
                if (commandSource.Command.CanExecute(commandSource.CommandParameter))
                {
                    commandSource.Command.Execute(commandSource.CommandParameter);
                    invoked = true;
                    details = commandSource.Command.GetType().Name;
                }

                return;
            }

            if (element is ButtonBase buttonBase)
            {
                buttonBase.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                invoked = true;
                details = "ClickEvent";
            }
        });

        if (!invoked)
        {
            return PipeProtocol.Error(
                errorCode: "invoke_failed",
                errorMessage: $"Could not invoke command on '{elementName}'.",
                requestId: request.RequestId);
        }

        RecordTraceEvent("invoke_command", element, details);
        RefreshTraceSubscriptionsIfActive();
        ActionResult? result = null;
        InvokeOnUiThread(() =>
        {
            var elementInfo = DescribeElementCore(element);
            result = new ActionResult(
                Action: "invoke_command",
                ElementName: elementInfo.ElementName,
                ElementStableId: elementInfo.ElementStableId,
                Succeeded: true);
        });
        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(result, JsonOptions),
            requestId: request.RequestId);
    }

    private static PipeResponse BuildStartTraceResponse(PipeRequest request)
    {
        var startSnapshot = CaptureInspectionSnapshot();
        var observedStates = CaptureObservedStates();

        lock (TraceSync)
        {
            _traceSession = new TraceSession(
                StartedAtUtc: DateTimeOffset.UtcNow,
                StartSnapshot: startSnapshot,
                Events: [],
                ObservedStates: observedStates);
            AddTraceEventNoLock("start_trace", elementName: null, elementStableId: null, details: null);
        }

        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(new TraceStateResult(
                IsActive: true,
                StartedAtUtc: _traceSession!.StartedAtUtc,
                StoppedAtUtc: null,
                EventCount: _traceSession.Events.Count,
                Diff: null),
                JsonOptions),
            requestId: request.RequestId);
    }

    private static PipeResponse BuildStopTraceResponse(PipeRequest request)
    {
        RefreshTraceSubscriptionsIfActive();
        var stopSnapshot = CaptureInspectionSnapshot();
        TraceStateResult? result = null;
        lock (TraceSync)
        {
            if (_traceSession is null)
            {
                return PipeProtocol.Error(
                    errorCode: "trace_not_started",
                    errorMessage: "No trace session is active.",
                    requestId: request.RequestId);
            }

            AddTraceEventNoLock("stop_trace", elementName: null, elementStableId: null, details: null);
            _traceSession = _traceSession with
            {
                StopSnapshot = stopSnapshot,
                StoppedAtUtc = DateTimeOffset.UtcNow
            };

            result = new TraceStateResult(
                IsActive: false,
                StartedAtUtc: _traceSession.StartedAtUtc,
                StoppedAtUtc: _traceSession.StoppedAtUtc,
                EventCount: _traceSession.Events.Count,
                Diff: BuildSnapshotDiff(_traceSession.StartSnapshot, _traceSession.StopSnapshot));
        }

        return PipeProtocol.Success(
            data: JsonSerializer.SerializeToNode(result, JsonOptions),
            requestId: request.RequestId);
    }

    private static PipeResponse BuildGetTraceResponse(PipeRequest request)
    {
        RefreshTraceSubscriptionsIfActive();
        lock (TraceSync)
        {
            if (_traceSession is null)
            {
                return PipeProtocol.Error(
                    errorCode: "trace_not_started",
                    errorMessage: "No trace session is active.",
                    requestId: request.RequestId);
            }

            var result = new TraceDetailsResult(
                IsActive: _traceSession.StoppedAtUtc is null,
                StartedAtUtc: _traceSession.StartedAtUtc,
                StoppedAtUtc: _traceSession.StoppedAtUtc,
                Events: _traceSession.Events,
                Diff: BuildSnapshotDiff(_traceSession.StartSnapshot, _traceSession.StopSnapshot ?? CaptureInspectionSnapshot()));

            return PipeProtocol.Success(
                data: JsonSerializer.SerializeToNode(result, JsonOptions),
                requestId: request.RequestId);
        }
    }

    private static PipeResponse BuildDiffSnapshotsResponse(PipeRequest request)
    {
        RefreshTraceSubscriptionsIfActive();
        lock (TraceSync)
        {
            if (_traceSession is null)
            {
                return PipeProtocol.Error(
                    errorCode: "trace_not_started",
                    errorMessage: "No trace session is active.",
                    requestId: request.RequestId);
            }

            var currentSnapshot = _traceSession.StopSnapshot ?? CaptureInspectionSnapshot();
            return PipeProtocol.Success(
                data: JsonSerializer.SerializeToNode(
                    BuildSnapshotDiff(_traceSession.StartSnapshot, currentSnapshot),
                    JsonOptions),
                requestId: request.RequestId);
        }
    }

    private static InspectionSnapshot CaptureInspectionSnapshot()
    {
        InspectionSnapshot? snapshot = null;
        InvokeOnUiThread(() => snapshot = BuildInspectionSnapshot());
        return snapshot ?? BuildEmptySnapshot();
    }

    private static string CollectTreeJson()
        => Inspector.CollectJson(Environment.ProcessId);

    private static InspectionSnapshot BuildInspectionSnapshot()
    {
        var app = Application.Current;
        if (app is null)
        {
            return BuildEmptySnapshot();
        }

        var windows = app.Windows
            .OfType<Window>()
            .Select(BuildWindowSnapshot)
            .ToList();

        return new InspectionSnapshot(
            ProcessId: Environment.ProcessId,
            WpfVersion: GetWpfVersion(),
            DispatcherState: app.Dispatcher.Thread.ThreadState.ToString(),
            RuntimeAssemblies: [],
            Windows: windows)
        {
            CollectedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static InspectionSnapshot BuildEmptySnapshot() =>
        new(
            ProcessId: Environment.ProcessId,
            WpfVersion: GetWpfVersion(),
            DispatcherState: "Unknown",
            RuntimeAssemblies: [],
            Windows: [])
        {
            CollectedAtUtc = DateTimeOffset.UtcNow
        };

    private static WindowSnapshot BuildWindowSnapshot(Window window) =>
        new(
            Title: window.Title,
            Width: window.ActualWidth,
            Height: window.ActualHeight,
            WindowState: window.WindowState.ToString(),
            RootElement: BuildTreeElement(window, depth: 0));

    private static TreeElementSnapshot BuildTreeElement(DependencyObject obj, int depth)
    {
        var children = new List<TreeElementSnapshot>();
        if (depth < MaxDepth)
        {
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

            var visualLimit = Math.Min(directVisualChildren.Count, MaxChildren);
            for (var index = 0; index < visualLimit; index++)
            {
                children.Add(BuildTreeElement(directVisualChildren[index], depth + 1));
            }

            var remainingSlots = Math.Max(0, MaxChildren - children.Count);
            for (var index = 0; index < Math.Min(supplementalChildren.Count, remainingSlots); index++)
            {
                children.Add(BuildTreeElement(supplementalChildren[index], depth + 1));
            }

            remainingSlots = Math.Max(0, MaxChildren - children.Count);
            for (var index = 0; index < Math.Min(adornerChildren.Count, remainingSlots); index++)
            {
                children.Add(BuildTreeElement(adornerChildren[index], depth + 1));
            }
        }

        return new TreeElementSnapshot(
            TypeName: obj.GetType().Name,
            Name: (obj as FrameworkElement)?.Name ?? string.Empty,
            AutomationId: GetAutomationId(obj),
            Text: ExtractText(obj),
            DataContextTypeName: (obj as FrameworkElement)?.DataContext?.GetType().Name,
            IsVisible: obj is UIElement visibleElement && visibleElement.IsVisible,
            IsEnabled: obj is UIElement enabledElement && enabledElement.IsEnabled,
            Width: (obj as FrameworkElement)?.ActualWidth ?? 0d,
            Height: (obj as FrameworkElement)?.ActualHeight ?? 0d,
            Children: children);
    }

    private static bool ShouldSkip(DependencyObject obj)
    {
        if (obj is not UIElement element || element.IsVisible)
        {
            return false;
        }

        if (obj is not FrameworkElement frameworkElement)
        {
            return false;
        }

        return frameworkElement.ActualWidth == 0 && frameworkElement.ActualHeight == 0;
    }

    private static string GetWindowTitle()
    {
        string? title = null;
        InvokeOnUiThread(() => title = Application.Current?.MainWindow?.Title);
        return title ?? "ERROR:No window";
    }

    private static string GetStatusText()
    {
        string? text = null;
        InvokeOnUiThread(() =>
        {
            var window = Application.Current?.MainWindow;
            if (window is null)
            {
                return;
            }

            var textBlock = FindDescendant<TextBlock>(
                window,
                candidate => candidate.Text?.StartsWith("準備", StringComparison.Ordinal) == true
                    || candidate.Text?.Length > 0);
            text = textBlock?.Text;
        });

        return text ?? "ERROR:Not found";
    }

    private static string GetDataGridItemCount(string gridName)
    {
        var count = -1;
        InvokeOnUiThread(() =>
        {
            if (!TryFindElementCore(gridName, out var element) || element is not DataGrid grid)
            {
                return;
            }

            count = grid.Items.Count;
        });

        return count >= 0 ? count.ToString() : "ERROR:Grid not found";
    }

    private static string GetText(string elementName)
    {
        string? text = null;
        InvokeOnUiThread(() =>
        {
            if (!TryFindElementCore(elementName, out var element))
            {
                return;
            }

            if (element is not null)
            {
                text = ExtractText(element);
            }
        });

        return text ?? "ERROR:Not found";
    }

    private static string ClickButton(string buttonName)
    {
        ButtonBase? clickedButton = null;
        InvokeOnUiThread(() =>
        {
            if (TryFindElementCore(buttonName, out var resolvedElement) && resolvedElement is ButtonBase directButton)
            {
                directButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                clickedButton = directButton;
                return;
            }

            var window = Application.Current?.MainWindow;
            if (window is null)
            {
                return;
            }

            var button = FindDescendantByName<Button>(window, buttonName)
                ?? FindDescendant<Button>(window, candidate => candidate.Content?.ToString() == buttonName);
            if (button is null)
            {
                return;
            }

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            clickedButton = button;
        });

        if (clickedButton is not null)
        {
            RecordTraceEvent("click", clickedButton, null);
            RefreshTraceSubscriptionsIfActive();
        }

        return clickedButton is not null ? "OK" : "ERROR:Button not found";
    }

    private static string TypeText(string arg)
    {
        var separatorIndex = arg.IndexOf(':');
        if (separatorIndex < 0)
        {
            return "ERROR:Invalid format";
        }

        var elementName = arg[..separatorIndex];
        var text = arg[(separatorIndex + 1)..];

        TextBox? targetTextBox = null;
        InvokeOnUiThread(() =>
        {
            if (TryFindElementCore(elementName, out var resolvedElement) && resolvedElement is TextBox directTextBox)
            {
                targetTextBox = directTextBox;
            }
            else
            {
                var window = Application.Current?.MainWindow;
                if (window is null)
                {
                    return;
                }

                targetTextBox = FindDescendantByName<TextBox>(window, elementName)
                    ?? FindDescendant<TextBox>(window, _ => true);
            }

            if (targetTextBox is null)
            {
                return;
            }

            targetTextBox.Text = text;
            targetTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        });

        if (targetTextBox is not null)
        {
            RecordTraceEvent("type_text", targetTextBox, text);
            RefreshTraceSubscriptionsIfActive();
        }

        return targetTextBox is not null ? "OK" : "ERROR:TextBox not found";
    }

    private static bool TryFindElement(string identifier, out DependencyObject element)
    {
        DependencyObject? found = null;
        InvokeOnUiThread(() => TryFindElementCore(identifier, out found));
        element = found!;
        return found is not null;
    }

    private static bool TryFindElementCore(string identifier, out DependencyObject? element)
    {
        element = null;
        var app = Application.Current;
        if (app is null)
        {
            return false;
        }

        var windows = app.Windows.OfType<Window>().ToList();
        if (TryResolveStableIdentifier(identifier, windows, out element))
        {
            return true;
        }

        foreach (var window in windows)
        {
            if (MatchesIdentifier(window, identifier))
            {
                element = window;
                return true;
            }

            var candidate = FindDescendant<DependencyObject>(window, child => MatchesIdentifier(child, identifier));
            if (candidate is not null)
            {
                element = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool MatchesIdentifier(DependencyObject element, string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        if (element is FrameworkElement frameworkElement &&
            string.Equals(frameworkElement.Name, identifier, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(GetAutomationId(element), identifier, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(ExtractText(element), identifier, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool TryResolveStableIdentifier(
        string identifier,
        IReadOnlyList<Window> windows,
        out DependencyObject? element)
    {
        element = null;
        if (string.IsNullOrWhiteSpace(identifier) ||
            !identifier.StartsWith("window:", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryResolveStableWindow(identifier, windows, out var window, out var suffix) || window is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(suffix))
        {
            element = window;
            return true;
        }

        if (string.Equals(suffix, "v:0", StringComparison.Ordinal))
        {
            element = window;
            return true;
        }

        if (TryResolveVisualPath(window, suffix, out element))
        {
            return true;
        }

        return TryResolveReferenceIdentifier(window, suffix, out element);
    }

    private static bool TryResolveStableWindow(
        string identifier,
        IReadOnlyList<Window> windows,
        out Window? window,
        out string? suffix)
    {
        window = null;
        suffix = null;

        var separatorIndex = identifier.IndexOf('|');
        var windowToken = separatorIndex >= 0 ? identifier[..separatorIndex] : identifier;
        if (!windowToken.StartsWith("window:", StringComparison.Ordinal) ||
            !int.TryParse(windowToken["window:".Length..], out var windowIndex) ||
            windowIndex < 0 ||
            windowIndex >= windows.Count)
        {
            return false;
        }

        window = windows[windowIndex];
        suffix = separatorIndex >= 0 ? identifier[(separatorIndex + 1)..] : null;
        return true;
    }

    private static bool TryResolveVisualPath(DependencyObject root, string suffix, out DependencyObject? element)
    {
        element = null;
        if (!suffix.StartsWith("v:0", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = suffix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || !string.Equals(segments[0], "v:0", StringComparison.Ordinal))
        {
            return false;
        }

        var current = root;
        for (var segmentIndex = 1; segmentIndex < segments.Length; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            var directVisualChildren = GetVisualChildren(current)
                .Where(child => !ShouldSkip(child))
                .ToList();
            var directLogicalChildren = GetLogicalChildren(current);
            var supplementalChildren = GetSupplementalChildren(current)
                .Where(child => directVisualChildren.All(existing => !ReferenceEquals(existing, child)))
                .Where(child => directLogicalChildren.All(existing => !ReferenceEquals(existing, child)))
                .ToList();
            var adornerChildren = GetAdornerChildren(current)
                .Where(child => directVisualChildren.All(existing => !ReferenceEquals(existing, child)))
                .Where(child => directLogicalChildren.All(existing => !ReferenceEquals(existing, child)))
                .Where(child => supplementalChildren.All(existing => !ReferenceEquals(existing, child)))
                .ToList();

            if (segment.StartsWith("aux", StringComparison.Ordinal) &&
                int.TryParse(segment["aux".Length..], out var supplementalIndex) &&
                supplementalIndex >= 0 &&
                supplementalIndex < supplementalChildren.Count)
            {
                current = supplementalChildren[supplementalIndex];
                continue;
            }

            if (segment.StartsWith("adorner", StringComparison.Ordinal) &&
                int.TryParse(segment["adorner".Length..], out var adornerIndex) &&
                adornerIndex >= 0 &&
                adornerIndex < adornerChildren.Count)
            {
                current = adornerChildren[adornerIndex];
                continue;
            }

            if (!int.TryParse(segment, out var visualIndex) ||
                visualIndex < 0 ||
                visualIndex >= directVisualChildren.Count)
            {
                return false;
            }

            current = directVisualChildren[visualIndex];
        }

        element = current;
        return true;
    }

    private static bool TryResolveReferenceIdentifier(Window ownerWindow, string suffix, out DependencyObject? element)
    {
        element = null;
        var lastSeparatorIndex = suffix.LastIndexOf(':');
        if (lastSeparatorIndex < 0 || lastSeparatorIndex == suffix.Length - 1)
        {
            return false;
        }

        var identity = suffix[(lastSeparatorIndex + 1)..];
        var typeHint = suffix.StartsWith("logical:", StringComparison.Ordinal)
            ? suffix["logical:".Length..lastSeparatorIndex]
            : null;

        if (MatchesReferenceIdentity(ownerWindow, identity, typeHint))
        {
            element = ownerWindow;
            return true;
        }

        var candidate = FindDescendant<DependencyObject>(
            ownerWindow,
            child => MatchesReferenceIdentity(child, identity, typeHint));
        if (candidate is null)
        {
            return false;
        }

        element = candidate;
        return true;
    }

    private static bool MatchesReferenceIdentity(DependencyObject element, string identity, string? typeHint)
    {
        if (!string.IsNullOrWhiteSpace(typeHint) &&
            !string.Equals(element.GetType().Name, typeHint, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(ResolveElementIdentity(element), identity, StringComparison.Ordinal);
    }

    private static string ResolveElementIdentity(DependencyObject element)
    {
        if (element is FrameworkElement { Name: { Length: > 0 } name })
        {
            return name;
        }

        var automationId = GetAutomationId(element);
        if (!string.IsNullOrWhiteSpace(automationId))
        {
            return automationId;
        }

        return element.GetHashCode().ToString("X");
    }

    private static string GetElementDisplayName(DependencyObject element) =>
        (element as FrameworkElement)?.Name
        ?? GetAutomationId(element)
        ?? ExtractText(element)
        ?? element.GetType().Name;

    private static string? GetStableId(DependencyObject element) =>
        TryResolveStableIdForElement(element, out var stableId)
            ? stableId
            : null;

    private static bool TryResolveStableIdForElement(DependencyObject target, out string? stableId)
    {
        stableId = null;
        var app = Application.Current;
        if (app is null)
        {
            return false;
        }

        var windows = app.Windows.OfType<Window>().ToList();
        for (var index = 0; index < windows.Count; index++)
        {
            if (TryResolveStableIdForElement(
                    current: windows[index],
                    target: target,
                    ownerWindowStableId: $"window:{index}",
                    visualPath: "v:0",
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
        out string? stableId)
    {
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
                    out stableId))
            {
                return true;
            }
        }

        stableId = null;
        return false;
    }

    private static string NormalizeMatchMode(string? matchMode)
    {
        if (string.IsNullOrWhiteSpace(matchMode))
        {
            return "equals";
        }

        return matchMode.Trim().ToLowerInvariant() switch
        {
            "equals" or "eq" => "equals",
            "not_equals" or "not-equals" or "ne" => "not_equals",
            "contains" => "contains",
            "starts_with" or "starts-with" => "starts_with",
            "ends_with" or "ends-with" => "ends_with",
            "exists" => "exists",
            _ => "equals"
        };
    }

    private static string NormalizeElementSearchMatchMode(string? matchMode)
    {
        if (string.IsNullOrWhiteSpace(matchMode))
        {
            return "contains";
        }

        return NormalizeMatchMode(matchMode) switch
        {
            "equals" or "contains" or "starts_with" or "ends_with" => NormalizeMatchMode(matchMode),
            _ => "contains"
        };
    }

    private static ConditionEvaluationResult EvaluateCondition(
        string elementName,
        string propertyName,
        string? expectedValue,
        string matchMode)
    {
        ConditionEvaluationResult? result = null;
        var expectsExistence = !string.Equals(expectedValue, "false", StringComparison.OrdinalIgnoreCase);

        InvokeOnUiThread(() =>
        {
            if (!TryFindElementCore(elementName, out var element) || element is null)
            {
                result = new ConditionEvaluationResult(
                    Element: null,
                    ElementName: null,
                    ElementStableId: null,
                    ElementMissing: true,
                    PropertyMissing: false,
                    Matched: matchMode == "exists" && !expectsExistence,
                    ActualValue: "false");
                return;
            }

            var elementInfo = DescribeElementCore(element);
            if (matchMode == "exists" ||
                string.Equals(propertyName, "exists", StringComparison.OrdinalIgnoreCase))
            {
                result = new ConditionEvaluationResult(
                    Element: element,
                    ElementName: elementInfo.ElementName,
                    ElementStableId: elementInfo.ElementStableId,
                    ElementMissing: false,
                    PropertyMissing: false,
                    Matched: expectsExistence,
                    ActualValue: "true");
                return;
            }

            if (!TryReadConditionValue(element, propertyName, out var actualValue))
            {
                result = new ConditionEvaluationResult(
                    Element: element,
                    ElementName: elementInfo.ElementName,
                    ElementStableId: elementInfo.ElementStableId,
                    ElementMissing: false,
                    PropertyMissing: true,
                    Matched: false,
                    ActualValue: null);
                return;
            }

            result = new ConditionEvaluationResult(
                Element: element,
                ElementName: elementInfo.ElementName,
                ElementStableId: elementInfo.ElementStableId,
                ElementMissing: false,
                PropertyMissing: false,
                Matched: MatchesConditionValue(actualValue, expectedValue, matchMode),
                ActualValue: actualValue);
        });

        return result ?? new ConditionEvaluationResult(
            Element: null,
            ElementName: null,
            ElementStableId: null,
            ElementMissing: true,
            PropertyMissing: false,
            Matched: false,
            ActualValue: null);
    }

    private static bool TryReadConditionValue(DependencyObject element, string propertyName, out string? actualValue)
    {
        actualValue = null;
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        switch (NormalizeConditionPropertyName(propertyName))
        {
            case "stableid":
                actualValue = GetStableId(element);
                return true;
            case "name":
                actualValue = (element as FrameworkElement)?.Name;
                return true;
            case "automationid":
                actualValue = GetAutomationId(element);
                return true;
            case "text":
                actualValue = ExtractText(element);
                return true;
            case "isvisible":
                actualValue = (element is UIElement visibleElement && visibleElement.IsVisible).ToString();
                return true;
            case "isenabled":
                actualValue = (element is UIElement enabledElement && enabledElement.IsEnabled).ToString();
                return true;
            case "isloaded":
                actualValue = (element as FrameworkElement)?.IsLoaded.ToString();
                return true;
            case "isfocused":
                actualValue = (element is UIElement focusedElement && focusedElement.IsFocused).ToString();
                return true;
            case "iskeyboardfocused":
                actualValue = (element is IInputElement inputElement && Equals(Keyboard.FocusedElement, inputElement)).ToString();
                return true;
            case "iskeyboardfocuswithin":
                actualValue = (element is UIElement focusWithinElement && focusWithinElement.IsKeyboardFocusWithin).ToString();
                return true;
            case "selectedindex":
                actualValue = (element as Selector)?.SelectedIndex.ToString();
                return true;
            case "selecteditem":
                actualValue = element is Selector selector ? SummarizeValue(selector.SelectedItem) : null;
                return true;
            case "itemcount":
                actualValue = (element as ItemsControl)?.Items.Count.ToString();
                return true;
            case "canexecute":
                actualValue = BuildCommandState(element)?.CanExecute.ToString();
                return actualValue is not null;
            case "windowtitle":
                actualValue = element is Window window ? window.Title : Window.GetWindow(element)?.Title;
                return true;
        }

        var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
        var property = element.GetType().GetProperty(propertyName, flags);
        if (property is { CanRead: true } && property.GetIndexParameters().Length == 0)
        {
            actualValue = FormatConditionValue(property.GetValue(element));
            return true;
        }

        var dependencyProperty = ResolveDependencyProperty(element.GetType(), propertyName);
        if (dependencyProperty is null)
        {
            return false;
        }

        actualValue = FormatConditionValue(element.GetValue(dependencyProperty));
        return true;
    }

    private static string? FormatConditionValue(object? value) =>
        value switch
        {
            null => null,
            DependencyObject dependencyObject => GetStableId(dependencyObject) ?? GetElementDisplayName(dependencyObject),
            _ => value.ToString()
        };

    private static string NormalizeConditionPropertyName(string propertyName) =>
        new string(propertyName
            .Trim()
            .Where(character => character is not '_' and not '-' and not ' ')
            .ToArray())
            .ToLowerInvariant();

    private static bool MatchesConditionValue(string? actualValue, string? expectedValue, string matchMode)
    {
        var actual = actualValue ?? string.Empty;
        var expected = expectedValue ?? string.Empty;

        return matchMode switch
        {
            "not_equals" => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "contains" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "starts_with" => actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            "ends_with" => actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            "exists" => !string.IsNullOrWhiteSpace(actual),
            _ => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
        };
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

    private static bool MatchesElementSearchCriteria(
        WpfElementInfo element,
        WpfWindowInfo ownerWindow,
        FindElementsCriteria criteria,
        out List<string> matchedFields)
    {
        matchedFields = [];

        if (!string.IsNullOrWhiteSpace(criteria.Query))
        {
            var queryMatched =
                TryMatchSearchField(element.Name, criteria.Query, criteria.MatchMode, matchedFields, "Name") |
                TryMatchSearchField(element.AutomationId, criteria.Query, criteria.MatchMode, matchedFields, "AutomationId") |
                TryMatchSearchField(element.StableId, criteria.Query, criteria.MatchMode, matchedFields, "StableId") |
                TryMatchSearchField(element.Text, criteria.Query, criteria.MatchMode, matchedFields, "Text");
            if (!queryMatched)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(criteria.TypeName))
        {
            var typeMatched =
                TryMatchSearchField(element.TypeName, criteria.TypeName, criteria.MatchMode, matchedFields, "TypeName") |
                TryMatchSearchField(element.FullTypeName, criteria.TypeName, criteria.MatchMode, matchedFields, "FullTypeName");
            if (!typeMatched)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(criteria.PropertyName))
        {
            var propertyMatched = element.ObservedProperties.Any(property =>
                MatchesConditionValue(property.PropertyName, criteria.PropertyName, criteria.MatchMode));
            if (!propertyMatched)
            {
                return false;
            }

            AddMatchedField(matchedFields, "PropertyName");
        }

        if (!string.IsNullOrWhiteSpace(criteria.BindingPath))
        {
            var bindingMatched = element.ObservedProperties.Any(property =>
                HasMatchingBindingPath(property.Binding, criteria.BindingPath, criteria.MatchMode));
            if (!bindingMatched)
            {
                return false;
            }

            AddMatchedField(matchedFields, "BindingPath");
        }

        if (!string.IsNullOrWhiteSpace(criteria.OwnerWindowTitle))
        {
            if (!MatchesConditionValue(ownerWindow.Title, criteria.OwnerWindowTitle, criteria.MatchMode))
            {
                return false;
            }

            AddMatchedField(matchedFields, "OwnerWindowTitle");
        }

        if (criteria.IsVisible is { } isVisible)
        {
            if (element.IsVisible != isVisible)
            {
                return false;
            }

            AddMatchedField(matchedFields, "IsVisible");
        }

        if (criteria.IsEnabled is { } isEnabled)
        {
            if (element.IsEnabled != isEnabled)
            {
                return false;
            }

            AddMatchedField(matchedFields, "IsEnabled");
        }

        if (criteria.HasValidationError is { } hasValidationError)
        {
            if ((element.Validation?.HasError ?? false) != hasValidationError)
            {
                return false;
            }

            AddMatchedField(matchedFields, "HasValidationError");
        }

        if (criteria.HasCommand is { } hasCommand)
        {
            if ((element.Command is not null) != hasCommand)
            {
                return false;
            }

            AddMatchedField(matchedFields, "HasCommand");
        }

        return matchedFields.Count > 0;
    }

    private static bool TryMatchSearchField(
        string? actualValue,
        string? expectedValue,
        string matchMode,
        List<string> matchedFields,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(expectedValue) ||
            !MatchesConditionValue(actualValue, expectedValue, matchMode))
        {
            return false;
        }

        AddMatchedField(matchedFields, fieldName);
        return true;
    }

    private static bool HasMatchingBindingPath(WpfBindingInfo? binding, string expectedValue, string matchMode)
    {
        if (binding is null)
        {
            return false;
        }

        if (MatchesConditionValue(binding.Path, expectedValue, matchMode) ||
            MatchesConditionValue(binding.XPath, expectedValue, matchMode))
        {
            return true;
        }

        return binding.ChildBindings.Any(child => HasMatchingBindingPath(child, expectedValue, matchMode));
    }

    private static FoundElementSummary BuildFoundElementSummary(
        WpfWindowInfo ownerWindow,
        WpfElementInfo element,
        List<string> matchedFields)
    {
        var observedPropertyNames = element.ObservedProperties
            .Select(property => property.PropertyName)
            .Where(propertyName => !string.IsNullOrWhiteSpace(propertyName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(propertyName => propertyName, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        var bindingPaths = EnumerateBindingPaths(element)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        return new FoundElementSummary(
            TypeName: element.TypeName,
            FullTypeName: element.FullTypeName,
            Name: NormalizeOptionalText(element.Name),
            AutomationId: NormalizeOptionalText(element.AutomationId),
            StableId: element.StableId,
            Text: NormalizeOptionalText(element.Text),
            DataContextTypeName: element.DataContextTypeName,
            OwnerWindowTitle: ownerWindow.Title,
            OwnerWindowStableId: ownerWindow.StableId,
            VisualPath: element.VisualPath,
            LogicalPath: element.LogicalPath,
            IsVisible: element.IsVisible,
            IsEnabled: element.IsEnabled,
            IsLoaded: element.IsLoaded,
            HasValidationError: element.Validation?.HasError == true,
            ValidationErrors: element.Validation?.Errors ?? [],
            HasCommand: element.Command is not null,
            CommandTypeName: element.Command?.CommandTypeName,
            CommandCanExecute: element.Command?.CanExecute,
            ItemCount: element.ItemsControl?.ItemCount,
            InteropBoundaryKind: element.InteropBoundary?.BoundaryKind,
            IsReference: element.IsReference,
            ReferenceReason: element.ReferenceReason,
            ObservedPropertyNames: observedPropertyNames,
            BindingPaths: bindingPaths,
            MatchedFields: matchedFields);
    }

    private static IEnumerable<string> EnumerateBindingPaths(WpfElementInfo element)
    {
        foreach (var property in element.ObservedProperties)
        {
            foreach (var bindingPath in EnumerateBindingPaths(property.Binding))
            {
                yield return bindingPath;
            }
        }
    }

    private static IEnumerable<string> EnumerateBindingPaths(WpfBindingInfo? binding)
    {
        if (binding is null)
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(binding.Path))
        {
            yield return binding.Path;
        }

        if (!string.IsNullOrWhiteSpace(binding.XPath))
        {
            yield return binding.XPath;
        }

        foreach (var child in binding.ChildBindings)
        {
            foreach (var bindingPath in EnumerateBindingPaths(child))
            {
                yield return bindingPath;
            }
        }
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static void AddMatchedField(List<string> matchedFields, string fieldName)
    {
        if (!matchedFields.Contains(fieldName, StringComparer.Ordinal))
        {
            matchedFields.Add(fieldName);
        }
    }

    private static ElementDetails BuildElementDetails(DependencyObject element)
    {
        var frameworkElement = element as FrameworkElement;
        var selector = element as Selector;
        var buttonBase = element as ButtonBase;
        var command = buttonBase?.Command;
        var commandParameter = buttonBase?.CommandParameter;

        return new ElementDetails(
            StableId: GetStableId(element),
            TypeName: element.GetType().Name,
            FullTypeName: element.GetType().FullName,
            Name: frameworkElement?.Name,
            AutomationId: GetAutomationId(element),
            Text: ExtractText(element),
            IsVisible: element is UIElement visibleElement && visibleElement.IsVisible,
            IsEnabled: element is UIElement enabledElement && enabledElement.IsEnabled,
            IsLoaded: frameworkElement?.IsLoaded ?? false,
            Focusable: element is UIElement focusableElement && focusableElement.Focusable,
            IsFocused: element is UIElement focusedElement && focusedElement.IsFocused,
            IsKeyboardFocused: element is IInputElement inputElement && Equals(Keyboard.FocusedElement, inputElement),
            Width: frameworkElement?.ActualWidth ?? 0d,
            Height: frameworkElement?.ActualHeight ?? 0d,
            DataContextTypeName: frameworkElement?.DataContext?.GetType().FullName,
            VisualChildCount: VisualTreeHelper.GetChildrenCount(element),
            ItemCount: selector?.Items.Count,
            SelectedIndex: selector?.SelectedIndex,
            SelectedItemSummary: selector is null ? null : SummarizeValue(selector.SelectedItem),
            CommandTypeName: command?.GetType().FullName,
            CanExecute: command is null ? null : command.CanExecute(commandParameter),
            CommandParameterSummary: SummarizeValue(commandParameter));
    }

    private static List<BindingDetails> CollectBindingDetails(DependencyObject element, string? propertyName)
    {
        var bindings = new List<BindingDetails>();
        if (!string.IsNullOrWhiteSpace(propertyName))
        {
            var property = ResolveDependencyProperty(element.GetType(), propertyName);
            if (property is null)
            {
                return bindings;
            }

            var details = BuildBindingDetails(element, property);
            if (details is not null)
            {
                bindings.Add(details);
            }

            return bindings;
        }

        var enumerator = element.GetLocalValueEnumerator();
        while (enumerator.MoveNext())
        {
            var entry = enumerator.Current;
            var details = BuildBindingDetails(element, entry.Property);
            if (details is not null)
            {
                bindings.Add(details);
            }
        }

        return bindings;
    }

    private static BindingDetails? BuildBindingDetails(DependencyObject target, DependencyProperty property)
    {
        var expression = BindingOperations.GetBindingExpressionBase(target, property);
        if (expression is null)
        {
            return null;
        }

        var validationErrors = Validation.GetErrors(target)
            .Select(error => error.ErrorContent?.ToString() ?? error.Exception?.Message ?? "Validation error")
            .ToList();

        if (BindingOperations.GetBinding(target, property) is Binding binding)
        {
            var bindingExpression = expression as BindingExpression;
            return new BindingDetails(
                BindingKind: nameof(Binding),
                Status: expression.Status.ToString(),
                TargetPropertyName: property.Name,
                Path: binding.Path?.Path,
                ElementName: binding.ElementName,
                RelativeSourceMode: binding.RelativeSource?.Mode.ToString(),
                SourceTypeName: binding.Source?.GetType().FullName,
                SourceSummary: SummarizeValue(bindingExpression?.DataItem),
                DataItemTypeName: bindingExpression?.DataItem?.GetType().FullName,
                ConverterTypeName: binding.Converter?.GetType().FullName,
                Mode: binding.Mode.ToString(),
                UpdateSourceTrigger: binding.UpdateSourceTrigger.ToString(),
                StringFormat: binding.StringFormat,
                FallbackValue: SummarizeValue(binding.FallbackValue),
                TargetNullValue: SummarizeValue(binding.TargetNullValue),
                HasError: validationErrors.Count > 0,
                Errors: validationErrors,
                ChildBindings: []);
        }

        if (BindingOperations.GetMultiBinding(target, property) is MultiBinding multiBinding)
        {
            return new BindingDetails(
                BindingKind: nameof(MultiBinding),
                Status: expression.Status.ToString(),
                TargetPropertyName: property.Name,
                Path: null,
                ElementName: null,
                RelativeSourceMode: null,
                SourceTypeName: null,
                SourceSummary: null,
                DataItemTypeName: null,
                ConverterTypeName: multiBinding.Converter?.GetType().FullName,
                Mode: multiBinding.Mode.ToString(),
                UpdateSourceTrigger: multiBinding.UpdateSourceTrigger.ToString(),
                StringFormat: multiBinding.StringFormat,
                FallbackValue: SummarizeValue(multiBinding.FallbackValue),
                TargetNullValue: SummarizeValue(multiBinding.TargetNullValue),
                HasError: validationErrors.Count > 0,
                Errors: validationErrors,
                ChildBindings: multiBinding.Bindings.Select(BuildBindingDefinition).ToList());
        }

        if (BindingOperations.GetPriorityBinding(target, property) is PriorityBinding priorityBinding)
        {
            return new BindingDetails(
                BindingKind: nameof(PriorityBinding),
                Status: expression.Status.ToString(),
                TargetPropertyName: property.Name,
                Path: null,
                ElementName: null,
                RelativeSourceMode: null,
                SourceTypeName: null,
                SourceSummary: null,
                DataItemTypeName: null,
                ConverterTypeName: null,
                Mode: BindingMode.Default.ToString(),
                UpdateSourceTrigger: UpdateSourceTrigger.Default.ToString(),
                StringFormat: priorityBinding.StringFormat,
                FallbackValue: SummarizeValue(priorityBinding.FallbackValue),
                TargetNullValue: SummarizeValue(priorityBinding.TargetNullValue),
                HasError: validationErrors.Count > 0,
                Errors: validationErrors,
                ChildBindings: priorityBinding.Bindings.Select(BuildBindingDefinition).ToList());
        }

        return new BindingDetails(
            BindingKind: expression.GetType().Name,
            Status: expression.Status.ToString(),
            TargetPropertyName: property.Name,
            Path: null,
            ElementName: null,
            RelativeSourceMode: null,
            SourceTypeName: null,
            SourceSummary: null,
            DataItemTypeName: null,
            ConverterTypeName: null,
            Mode: null,
            UpdateSourceTrigger: null,
            StringFormat: null,
            FallbackValue: null,
            TargetNullValue: null,
            HasError: validationErrors.Count > 0,
            Errors: validationErrors,
            ChildBindings: []);
    }

    private static BindingDetails BuildBindingDefinition(BindingBase bindingBase)
    {
        if (bindingBase is Binding binding)
        {
            return new BindingDetails(
                BindingKind: nameof(Binding),
                Status: "Definition",
                TargetPropertyName: null,
                Path: binding.Path?.Path,
                ElementName: binding.ElementName,
                RelativeSourceMode: binding.RelativeSource?.Mode.ToString(),
                SourceTypeName: binding.Source?.GetType().FullName,
                SourceSummary: SummarizeValue(binding.Source),
                DataItemTypeName: null,
                ConverterTypeName: binding.Converter?.GetType().FullName,
                Mode: binding.Mode.ToString(),
                UpdateSourceTrigger: binding.UpdateSourceTrigger.ToString(),
                StringFormat: binding.StringFormat,
                FallbackValue: SummarizeValue(binding.FallbackValue),
                TargetNullValue: SummarizeValue(binding.TargetNullValue),
                HasError: false,
                Errors: [],
                ChildBindings: []);
        }

        if (bindingBase is MultiBinding multiBinding)
        {
            return new BindingDetails(
                BindingKind: nameof(MultiBinding),
                Status: "Definition",
                TargetPropertyName: null,
                Path: null,
                ElementName: null,
                RelativeSourceMode: null,
                SourceTypeName: null,
                SourceSummary: null,
                DataItemTypeName: null,
                ConverterTypeName: multiBinding.Converter?.GetType().FullName,
                Mode: multiBinding.Mode.ToString(),
                UpdateSourceTrigger: multiBinding.UpdateSourceTrigger.ToString(),
                StringFormat: multiBinding.StringFormat,
                FallbackValue: SummarizeValue(multiBinding.FallbackValue),
                TargetNullValue: SummarizeValue(multiBinding.TargetNullValue),
                HasError: false,
                Errors: [],
                ChildBindings: multiBinding.Bindings.Select(BuildBindingDefinition).ToList());
        }

        if (bindingBase is PriorityBinding priorityBinding)
        {
            return new BindingDetails(
                BindingKind: nameof(PriorityBinding),
                Status: "Definition",
                TargetPropertyName: null,
                Path: null,
                ElementName: null,
                RelativeSourceMode: null,
                SourceTypeName: null,
                SourceSummary: null,
                DataItemTypeName: null,
                ConverterTypeName: null,
                Mode: null,
                UpdateSourceTrigger: null,
                StringFormat: priorityBinding.StringFormat,
                FallbackValue: SummarizeValue(priorityBinding.FallbackValue),
                TargetNullValue: SummarizeValue(priorityBinding.TargetNullValue),
                HasError: false,
                Errors: [],
                ChildBindings: priorityBinding.Bindings.Select(BuildBindingDefinition).ToList());
        }

        return new BindingDetails(
            BindingKind: bindingBase.GetType().Name,
            Status: "Definition",
            TargetPropertyName: null,
            Path: null,
            ElementName: null,
            RelativeSourceMode: null,
            SourceTypeName: null,
            SourceSummary: null,
            DataItemTypeName: null,
            ConverterTypeName: null,
            Mode: null,
            UpdateSourceTrigger: null,
            StringFormat: null,
            FallbackValue: null,
            TargetNullValue: null,
            HasError: false,
            Errors: [],
            ChildBindings: []);
    }

    private static DependencyProperty? ResolveDependencyProperty(Type elementType, string propertyName)
    {
        var normalizedPropertyName = propertyName
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        var fieldName = normalizedPropertyName.EndsWith("Property", StringComparison.Ordinal)
            ? normalizedPropertyName
            : normalizedPropertyName + "Property";

        for (var current = elementType; current is not null; current = current.BaseType)
        {
            var field = current.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (field?.GetValue(null) is DependencyProperty property)
            {
                return property;
            }
        }

        return null;
    }

    private static bool FocusElement(IInputElement inputElement, DependencyObject element)
    {
        if (element is not UIElement uiElement)
        {
            return false;
        }

        var focused = uiElement.Focus();
        Keyboard.Focus(inputElement);
        return focused || Equals(Keyboard.FocusedElement, inputElement) || uiElement.IsKeyboardFocusWithin;
    }

    private static bool ScrollIntoView(FrameworkElement element)
    {
        element.BringIntoView();
        return true;
    }

    private static string GetRequiredStringArgument(PipeRequest request, string name)
    {
        var value = GetOptionalStringArgument(request, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required argument '{name}'.");
        }

        return value;
    }

    private static string? GetOptionalStringArgument(PipeRequest request, string name) =>
        request.Arguments.TryGetPropertyValue(name, out var node)
            ? node?.GetValue<string>()
            : null;

    private static int? GetOptionalIntArgument(PipeRequest request, string name)
    {
        if (!request.Arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (node is JsonValue jsonString && jsonString.TryGetValue<string>(out var stringValue) &&
            int.TryParse(stringValue, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool? GetOptionalBooleanArgument(PipeRequest request, string name)
    {
        if (!request.Arguments.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        if (node is JsonValue jsonString && jsonString.TryGetValue<string>(out var stringValue) &&
            bool.TryParse(stringValue, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? GetAutomationId(DependencyObject element)
    {
        var automationId = AutomationProperties.GetAutomationId(element);
        return string.IsNullOrWhiteSpace(automationId) ? null : automationId;
    }

    private static IEnumerable<DependencyObject> GetVisualChildren(DependencyObject element)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(element);
        for (var index = 0; index < childCount; index++)
        {
            yield return VisualTreeHelper.GetChild(element, index);
        }
    }

    private static List<DependencyObject> GetLogicalChildren(DependencyObject element)
    {
        var result = new List<DependencyObject>();
        try
        {
            foreach (var child in LogicalTreeHelper.GetChildren(element))
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

    private static List<DependencyObject> GetSupplementalChildren(DependencyObject element)
    {
        var result = new List<DependencyObject>();

        if (element is FrameworkElement frameworkElement)
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

        if (element is Popup popup && popup.Child is DependencyObject popupChild)
        {
            result.Add(popupChild);
        }

        return result;
    }

    private static List<DependencyObject> GetAdornerChildren(DependencyObject element)
    {
        var result = new List<DependencyObject>();
        if (element is not UIElement uiElement)
        {
            return result;
        }

        try
        {
            var adornerLayer = AdornerLayer.GetAdornerLayer(uiElement);
            if (adornerLayer is null)
            {
                return result;
            }

            var adorners = adornerLayer.GetAdorners(uiElement);
            if (adorners is null)
            {
                return result;
            }

            result.AddRange(adorners);
        }
        catch
        {
        }

        return result;
    }

    private static string? ExtractText(DependencyObject element) =>
        element switch
        {
            TextBlock textBlock => textBlock.Text,
            TextBox textBox => textBox.Text,
            ContentControl { Content: string content } => content,
            HeaderedContentControl { Header: string header } => header,
            HeaderedItemsControl { Header: string header } => header,
            Run run => run.Text,
            _ => null
        };

    private static string? SummarizeValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            Type type => type.FullName,
            DependencyObject dependencyObject => $"{dependencyObject.GetType().Name}:{(dependencyObject as FrameworkElement)?.Name}",
            _ => value.ToString()
        };
    }

    private static CommandStateResult? BuildCommandState(DependencyObject element)
    {
        if (element is not ICommandSource commandSource || commandSource.Command is null)
        {
            return null;
        }

        return new CommandStateResult(
            ElementName: GetElementDisplayName(element),
            ElementStableId: GetStableId(element),
            CommandTypeName: commandSource.Command.GetType().FullName,
            CommandName: commandSource.Command.ToString(),
            CanExecute: commandSource.Command.CanExecute(commandSource.CommandParameter),
            CommandParameterSummary: SummarizeValue(commandSource.CommandParameter),
            CommandTargetName: (commandSource.CommandTarget as FrameworkElement)?.Name,
            CommandTargetStableId: commandSource.CommandTarget is DependencyObject target ? GetStableId(target) : null,
            CommandTargetTypeName: commandSource.CommandTarget?.GetType().FullName);
    }

    private static List<PopupState> CollectPopupStates()
    {
        var result = new List<PopupState>();
        if (Application.Current is null)
        {
            return result;
        }

        foreach (var window in Application.Current.Windows.OfType<Window>())
        {
            foreach (var popup in FindAllDescendants<Popup>(window))
            {
                result.Add(new PopupState(
                    Kind: nameof(Popup),
                    OwnerWindow: window.Title,
                    ElementName: (popup.PlacementTarget as FrameworkElement)?.Name,
                    ElementStableId: popup.PlacementTarget is DependencyObject placementTarget ? GetStableId(placementTarget) : null,
                    IsOpen: popup.IsOpen,
                    Summary: popup.Child is null ? null : popup.Child.GetType().Name));
            }

            foreach (var frameworkElement in FindAllDescendants<FrameworkElement>(window))
            {
                if (frameworkElement.ContextMenu is { } contextMenu)
                {
                    result.Add(new PopupState(
                        Kind: nameof(ContextMenu),
                        OwnerWindow: window.Title,
                        ElementName: frameworkElement.Name,
                        ElementStableId: GetStableId(frameworkElement),
                        IsOpen: contextMenu.IsOpen,
                        Summary: contextMenu.Name));
                }

                if (frameworkElement.ToolTip is DependencyObject toolTip)
                {
                    result.Add(new PopupState(
                        Kind: "ToolTip",
                        OwnerWindow: window.Title,
                        ElementName: frameworkElement.Name,
                        ElementStableId: GetStableId(frameworkElement),
                        IsOpen: frameworkElement.IsMouseOver,
                        Summary: ExtractText(toolTip)));
                }
            }
        }

        return result;
    }

    private static SnapshotDiffResult BuildSnapshotDiff(InspectionSnapshot before, InspectionSnapshot after)
    {
        var beforeNodeCount = before.Windows.Sum(window => CountNodes(window.RootElement));
        var afterNodeCount = after.Windows.Sum(window => CountNodes(window.RootElement));
        var beforeTitles = before.Windows.Select(window => window.Title).ToList();
        var afterTitles = after.Windows.Select(window => window.Title).ToList();

        return new SnapshotDiffResult(
            BeforeWindowCount: before.Windows.Count,
            AfterWindowCount: after.Windows.Count,
            BeforeNodeCount: beforeNodeCount,
            AfterNodeCount: afterNodeCount,
            BeforeDispatcherState: before.DispatcherState,
            AfterDispatcherState: after.DispatcherState,
            AddedWindowTitles: afterTitles.Except(beforeTitles).ToList(),
            RemovedWindowTitles: beforeTitles.Except(afterTitles).ToList());
    }

    private static int CountNodes(TreeElementSnapshot root) =>
        1 + root.Children.Sum(CountNodes);

    private static IEnumerable<T> FindAllDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindAllDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RecordTraceEvent(string operation, string? elementName, string? details)
    {
        lock (TraceSync)
        {
            if (_traceSession is null || _traceSession.StoppedAtUtc is not null)
            {
                return;
            }

            AddTraceEventNoLock(operation, elementName, null, details);
        }
    }

    private static void RecordTraceEvent(string operation, DependencyObject? element, string? details)
    {
        string? elementName = null;
        string? elementStableId = null;

        if (element is not null)
        {
            InvokeOnUiThread(() =>
            {
                var elementInfo = DescribeElementCore(element);
                elementName = elementInfo.ElementName;
                elementStableId = elementInfo.ElementStableId;
            });
        }

        lock (TraceSync)
        {
            if (_traceSession is null || _traceSession.StoppedAtUtc is not null)
            {
                return;
            }

            AddTraceEventNoLock(operation, elementName, elementStableId, details);
        }
    }

    private static void AddTraceEventNoLock(string operation, string? elementName, string? elementStableId, string? details)
    {
        if (_traceSession is null)
        {
            return;
        }

        _traceSession.Events.Add(new TraceEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            Operation: operation,
            ElementName: elementName,
            ElementStableId: elementStableId,
            Details: details));

        if (_traceSession.Events.Count > MaxTraceEvents)
        {
            _traceSession.Events.RemoveRange(0, _traceSession.Events.Count - MaxTraceEvents);
        }
    }

    private static void RefreshTraceSubscriptionsIfActive()
    {
        Dictionary<string, ObservedElementState>? currentStates = null;

        lock (TraceSync)
        {
            if (_traceSession is null || _traceSession.StoppedAtUtc is not null)
            {
                return;
            }
        }

        currentStates = CaptureObservedStates();

        lock (TraceSync)
        {
            if (_traceSession is null || _traceSession.StoppedAtUtc is not null)
            {
                return;
            }

            foreach (var traceEvent in BuildObservedStateTraceEvents(_traceSession.ObservedStates, currentStates))
            {
                AddTraceEventNoLock(
                    traceEvent.Operation,
                    traceEvent.ElementName,
                    traceEvent.ElementStableId,
                    traceEvent.Details);
            }

            _traceSession = _traceSession with
            {
                ObservedStates = currentStates
            };
        }
    }

    private static string GetWpfVersion() =>
        typeof(Window).Assembly.GetName().Version?.ToString() ?? "Unknown";

    private static T? FindDescendantByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
        => FindDescendant<T>(root, element => element.Name == name);

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        return FindDescendant(root, predicate, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));
    }

    private static T? FindDescendant<T>(
        DependencyObject root,
        Func<T, bool> predicate,
        HashSet<DependencyObject> visited)
        where T : DependencyObject
    {
        if (!visited.Add(root))
        {
            return null;
        }

        var directVisualChildren = GetVisualChildren(root)
            .Where(child => !ShouldSkip(child))
            .ToList();
        var directLogicalChildren = GetLogicalChildren(root);
        var supplementalChildren = GetSupplementalChildren(root)
            .Where(child => directVisualChildren.All(existing => !ReferenceEquals(existing, child)))
            .Where(child => directLogicalChildren.All(existing => !ReferenceEquals(existing, child)))
            .ToList();
        var adornerChildren = GetAdornerChildren(root)
            .Where(child => directVisualChildren.All(existing => !ReferenceEquals(existing, child)))
            .Where(child => directLogicalChildren.All(existing => !ReferenceEquals(existing, child)))
            .Where(child => supplementalChildren.All(existing => !ReferenceEquals(existing, child)))
            .ToList();
        var searchChildren = directVisualChildren
            .Concat(supplementalChildren)
            .Concat(adornerChildren)
            .Concat(directLogicalChildren.Where(child =>
                directVisualChildren.All(existing => !ReferenceEquals(existing, child)) &&
                supplementalChildren.All(existing => !ReferenceEquals(existing, child)) &&
                adornerChildren.All(existing => !ReferenceEquals(existing, child))));

        foreach (var child in searchChildren)
        {
            if (child is T typed && predicate(typed))
            {
                return typed;
            }

            var found = FindDescendant(child, predicate, visited);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static ResolvedElementInfo DescribeElementCore(DependencyObject element) =>
        new(
            ElementName: GetElementDisplayName(element),
            ElementStableId: GetStableId(element));

    private static Dictionary<string, ObservedElementState> CaptureObservedStates()
    {
        Dictionary<string, ObservedElementState>? states = null;
        InvokeOnUiThread(() => states = BuildObservedStates());
        return states ?? [];
    }

    private static Dictionary<string, ObservedElementState> BuildObservedStates()
    {
        var states = new Dictionary<string, ObservedElementState>(StringComparer.Ordinal);
        var app = Application.Current;
        if (app is null)
        {
            return states;
        }

        var windows = app.Windows.OfType<Window>().ToList();
        for (var index = 0; index < windows.Count; index++)
        {
            CollectObservedStates(
                current: windows[index],
                ownerWindowStableId: $"window:{index}",
                visualPath: "v:0",
                depth: 0,
                states: states,
                visited: new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));
        }

        return states;
    }

    private static void CollectObservedStates(
        DependencyObject current,
        string ownerWindowStableId,
        string visualPath,
        int depth,
        Dictionary<string, ObservedElementState> states,
        HashSet<DependencyObject> visited)
    {
        if (!visited.Add(current))
        {
            return;
        }

        var stableId = $"{ownerWindowStableId}|{visualPath}";
        states[stableId] = BuildObservedState(current, stableId);
        if (depth >= MaxDepth)
        {
            return;
        }

        var directVisualChildren = GetVisualChildren(current)
            .Where(child => !ShouldSkip(child))
            .ToList();
        var directLogicalChildren = GetLogicalChildren(current);
        var supplementalChildren = GetSupplementalChildren(current)
            .Where(child => directVisualChildren.All(existing => !ReferenceEquals(existing, child)))
            .Where(child => directLogicalChildren.All(existing => !ReferenceEquals(existing, child)))
            .ToList();
        var adornerChildren = GetAdornerChildren(current)
            .Where(child => directVisualChildren.All(existing => !ReferenceEquals(existing, child)))
            .Where(child => directLogicalChildren.All(existing => !ReferenceEquals(existing, child)))
            .Where(child => supplementalChildren.All(existing => !ReferenceEquals(existing, child)))
            .ToList();

        var visualLimit = Math.Min(directVisualChildren.Count, MaxChildren);
        for (var index = 0; index < visualLimit; index++)
        {
            CollectObservedStates(
                directVisualChildren[index],
                ownerWindowStableId,
                $"{visualPath}/{index}",
                depth + 1,
                states,
                visited);
        }

        var remainingSlots = Math.Max(0, MaxChildren - visualLimit);
        for (var index = 0; index < Math.Min(supplementalChildren.Count, remainingSlots); index++)
        {
            CollectObservedStates(
                supplementalChildren[index],
                ownerWindowStableId,
                $"{visualPath}/aux{index}",
                depth + 1,
                states,
                visited);
        }

        remainingSlots = Math.Max(0, MaxChildren - visualLimit - Math.Min(supplementalChildren.Count, remainingSlots));
        for (var index = 0; index < Math.Min(adornerChildren.Count, remainingSlots); index++)
        {
            CollectObservedStates(
                adornerChildren[index],
                ownerWindowStableId,
                $"{visualPath}/adorner{index}",
                depth + 1,
                states,
                visited);
        }
    }

    private static ObservedElementState BuildObservedState(DependencyObject element, string stableId)
    {
        var elementInfo = DescribeElementCore(element);
        bool? canExecute = element is ICommandSource commandSource && commandSource.Command is not null
            ? commandSource.Command.CanExecute(commandSource.CommandParameter)
            : null;
        bool? isExpanded = element switch
        {
            Expander expander => expander.IsExpanded,
            TreeViewItem treeViewItem => treeViewItem.IsExpanded,
            MenuItem menuItem => menuItem.IsSubmenuOpen,
            _ => null
        };

        return new ObservedElementState(
            StableId: stableId,
            ElementName: elementInfo.ElementName,
            TypeName: element.GetType().Name,
            AutomationId: GetAutomationId(element),
            Text: ExtractText(element),
            IsVisible: element is UIElement visibleElement && visibleElement.IsVisible,
            IsEnabled: element is UIElement enabledElement && enabledElement.IsEnabled,
            IsLoaded: (element as FrameworkElement)?.IsLoaded ?? false,
            IsFocused: element is UIElement focusedElement && focusedElement.IsFocused,
            IsKeyboardFocused: element is IInputElement inputElement && Equals(Keyboard.FocusedElement, inputElement),
            IsKeyboardFocusWithin: element is UIElement focusWithinElement && focusWithinElement.IsKeyboardFocusWithin,
            SelectedIndex: (element as Selector)?.SelectedIndex,
            ItemCount: (element as ItemsControl)?.Items.Count,
            CanExecute: canExecute,
            IsExpanded: isExpanded);
    }

    private static IEnumerable<TraceEvent> BuildObservedStateTraceEvents(
        IReadOnlyDictionary<string, ObservedElementState> previousStates,
        IReadOnlyDictionary<string, ObservedElementState> currentStates)
    {
        foreach (var stableId in currentStates.Keys.Except(previousStates.Keys).OrderBy(key => key, StringComparer.Ordinal))
        {
            var currentState = currentStates[stableId];
            yield return new TraceEvent(
                TimestampUtc: DateTimeOffset.UtcNow,
                Operation: "element_added",
                ElementName: currentState.ElementName,
                ElementStableId: stableId,
                Details: DescribeObservedState(currentState));
        }

        foreach (var stableId in previousStates.Keys.Except(currentStates.Keys).OrderBy(key => key, StringComparer.Ordinal))
        {
            var previousState = previousStates[stableId];
            yield return new TraceEvent(
                TimestampUtc: DateTimeOffset.UtcNow,
                Operation: "element_removed",
                ElementName: previousState.ElementName,
                ElementStableId: stableId,
                Details: DescribeObservedState(previousState));
        }

        foreach (var stableId in currentStates.Keys.Intersect(previousStates.Keys).OrderBy(key => key, StringComparer.Ordinal))
        {
            var details = BuildObservedStateChangeDetails(previousStates[stableId], currentStates[stableId]);
            if (details is null)
            {
                continue;
            }

            yield return new TraceEvent(
                TimestampUtc: DateTimeOffset.UtcNow,
                Operation: "element_changed",
                ElementName: currentStates[stableId].ElementName,
                ElementStableId: stableId,
                Details: details);
        }
    }

    private static string DescribeObservedState(ObservedElementState state)
    {
        var parts = new List<string>
        {
            $"type={state.TypeName}",
            $"name={FormatTraceValue(state.ElementName)}"
        };

        if (!string.IsNullOrWhiteSpace(state.Text))
        {
            parts.Add($"text={FormatTraceValue(state.Text)}");
        }

        return string.Join("; ", parts);
    }

    private static string? BuildObservedStateChangeDetails(ObservedElementState previousState, ObservedElementState currentState)
    {
        var changes = new List<string>();
        AppendTraceDifference(changes, "Text", previousState.Text, currentState.Text);
        AppendTraceDifference(changes, "IsVisible", previousState.IsVisible, currentState.IsVisible);
        AppendTraceDifference(changes, "IsEnabled", previousState.IsEnabled, currentState.IsEnabled);
        AppendTraceDifference(changes, "IsLoaded", previousState.IsLoaded, currentState.IsLoaded);
        AppendTraceDifference(changes, "IsFocused", previousState.IsFocused, currentState.IsFocused);
        AppendTraceDifference(changes, "IsKeyboardFocused", previousState.IsKeyboardFocused, currentState.IsKeyboardFocused);
        AppendTraceDifference(changes, "IsKeyboardFocusWithin", previousState.IsKeyboardFocusWithin, currentState.IsKeyboardFocusWithin);
        AppendTraceDifference(changes, "SelectedIndex", previousState.SelectedIndex, currentState.SelectedIndex);
        AppendTraceDifference(changes, "ItemCount", previousState.ItemCount, currentState.ItemCount);
        AppendTraceDifference(changes, "CanExecute", previousState.CanExecute, currentState.CanExecute);
        AppendTraceDifference(changes, "IsExpanded", previousState.IsExpanded, currentState.IsExpanded);
        return changes.Count == 0 ? null : string.Join("; ", changes);
    }

    private static void AppendTraceDifference(List<string> changes, string propertyName, object? previousValue, object? currentValue)
    {
        if (Equals(previousValue, currentValue))
        {
            return;
        }

        changes.Add($"{propertyName}:{FormatTraceValue(previousValue)}->{FormatTraceValue(currentValue)}");
    }

    private static string FormatTraceValue(object? value) =>
        value switch
        {
            null => "null",
            string text => $"'{text}'",
            _ => value.ToString() ?? "null"
        };

    private static void InvokeOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private static void Log(string message)
    {
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), $"wpfi_helper_{Environment.ProcessId}.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (LogSync)
            {
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private sealed record InspectionSnapshot(
        int ProcessId,
        string WpfVersion,
        string DispatcherState,
        List<object> RuntimeAssemblies,
        List<WindowSnapshot> Windows)
    {
        public DateTimeOffset? CollectedAtUtc { get; init; }
    }

    private sealed record WindowSnapshot(
        string Title,
        double Width,
        double Height,
        string WindowState,
        TreeElementSnapshot RootElement);

    private sealed record TreeElementSnapshot(
        string TypeName,
        string Name,
        string? AutomationId,
        string? Text,
        string? DataContextTypeName,
        bool IsVisible,
        bool IsEnabled,
        double Width,
        double Height,
        List<TreeElementSnapshot> Children);

    private sealed record ElementDetailsEnvelope(
        string RequestedElement,
        ElementDetails Details);

    private sealed record ElementDetails(
        string? StableId,
        string TypeName,
        string? FullTypeName,
        string? Name,
        string? AutomationId,
        string? Text,
        bool IsVisible,
        bool IsEnabled,
        bool IsLoaded,
        bool Focusable,
        bool IsFocused,
        bool IsKeyboardFocused,
        double Width,
        double Height,
        string? DataContextTypeName,
        int VisualChildCount,
        int? ItemCount,
        int? SelectedIndex,
        string? SelectedItemSummary,
        string? CommandTypeName,
        bool? CanExecute,
        string? CommandParameterSummary);

    private sealed record BindingDetailsEnvelope(
        string RequestedElement,
        string? PropertyName,
        List<BindingDetails> Bindings);

    private sealed record FindElementsCriteria(
        string? Query,
        string MatchMode,
        string? TypeName,
        string? PropertyName,
        string? BindingPath,
        string? OwnerWindowTitle,
        bool? IsVisible,
        bool? IsEnabled,
        bool? HasValidationError,
        bool? HasCommand,
        bool IncludeReferences,
        int MaxResults);

    private sealed record FindElementsResult(
        FindElementsCriteria Criteria,
        int ScannedElementCount,
        int TotalMatchCount,
        bool Truncated,
        List<FoundElementSummary> Elements);

    private sealed record FoundElementSummary(
        string TypeName,
        string? FullTypeName,
        string? Name,
        string? AutomationId,
        string? StableId,
        string? Text,
        string? DataContextTypeName,
        string OwnerWindowTitle,
        string? OwnerWindowStableId,
        string? VisualPath,
        string? LogicalPath,
        bool IsVisible,
        bool IsEnabled,
        bool IsLoaded,
        bool HasValidationError,
        List<string> ValidationErrors,
        bool HasCommand,
        string? CommandTypeName,
        bool? CommandCanExecute,
        int? ItemCount,
        string? InteropBoundaryKind,
        bool IsReference,
        string? ReferenceReason,
        List<string> ObservedPropertyNames,
        List<string> BindingPaths,
        List<string> MatchedFields);

    private sealed record BindingDetails(
        string BindingKind,
        string Status,
        string? TargetPropertyName,
        string? Path,
        string? ElementName,
        string? RelativeSourceMode,
        string? SourceTypeName,
        string? SourceSummary,
        string? DataItemTypeName,
        string? ConverterTypeName,
        string? Mode,
        string? UpdateSourceTrigger,
        string? StringFormat,
        string? FallbackValue,
        string? TargetNullValue,
        bool HasError,
        List<string> Errors,
        List<BindingDetails> ChildBindings);

    private sealed record CommandStateResult(
        string ElementName,
        string? ElementStableId,
        string? CommandTypeName,
        string? CommandName,
        bool CanExecute,
        string? CommandParameterSummary,
        string? CommandTargetName,
        string? CommandTargetStableId,
        string? CommandTargetTypeName);

    private sealed record PopupListResult(
        List<PopupState> Popups);

    private sealed record PopupState(
        string Kind,
        string OwnerWindow,
        string? ElementName,
        string? ElementStableId,
        bool IsOpen,
        string? Summary);

    private sealed record ActionResult(
        string Action,
        string ElementName,
        string? ElementStableId,
        bool Succeeded);

    private sealed record SelectionResult(
        string ElementName,
        string? ElementStableId,
        int SelectedIndex,
        string? SelectedItemSummary);

    private sealed record ToggleResult(
        string ElementName,
        string? ElementStableId,
        bool IsExpanded);

    private sealed record WaitForIdleResult(
        int TimeoutMs,
        bool ReachedIdle,
        long WaitedMs);

    private sealed record WaitForConditionResult(
        string ElementName,
        string? ElementStableId,
        string PropertyName,
        string MatchMode,
        string? ExpectedValue,
        string? ActualValue,
        bool Matched,
        long WaitedMs);

    private sealed record TraceSession(
        DateTimeOffset StartedAtUtc,
        InspectionSnapshot StartSnapshot,
        List<TraceEvent> Events,
        Dictionary<string, ObservedElementState> ObservedStates)
    {
        public DateTimeOffset? StoppedAtUtc { get; init; }

        public InspectionSnapshot? StopSnapshot { get; init; }
    }

    private sealed record TraceEvent(
        DateTimeOffset TimestampUtc,
        string Operation,
        string? ElementName,
        string? ElementStableId,
        string? Details);

    private sealed record TraceStateResult(
        bool IsActive,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? StoppedAtUtc,
        int EventCount,
        SnapshotDiffResult? Diff);

    private sealed record TraceDetailsResult(
        bool IsActive,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? StoppedAtUtc,
        List<TraceEvent> Events,
        SnapshotDiffResult Diff);

    private sealed record SnapshotDiffResult(
        int BeforeWindowCount,
        int AfterWindowCount,
        int BeforeNodeCount,
        int AfterNodeCount,
        string BeforeDispatcherState,
        string AfterDispatcherState,
        List<string> AddedWindowTitles,
        List<string> RemovedWindowTitles);

    private sealed record ConditionEvaluationResult(
        DependencyObject? Element,
        string? ElementName,
        string? ElementStableId,
        bool ElementMissing,
        bool PropertyMissing,
        bool Matched,
        string? ActualValue);

    private sealed record ResolvedElementInfo(
        string ElementName,
        string? ElementStableId);

    private sealed record ObservedElementState(
        string StableId,
        string ElementName,
        string TypeName,
        string? AutomationId,
        string? Text,
        bool IsVisible,
        bool IsEnabled,
        bool IsLoaded,
        bool IsFocused,
        bool IsKeyboardFocused,
        bool IsKeyboardFocusWithin,
        int? SelectedIndex,
        int? ItemCount,
        bool? CanExecute,
        bool? IsExpanded);
}
