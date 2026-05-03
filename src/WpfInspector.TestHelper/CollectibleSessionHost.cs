using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace WpfInspector.TestHelper;

internal static class CollectibleSessionHost
{
    private const string WorkerAssemblyFileName = "WpfInspector.TestRuntime.dll";
    private const string WorkerEntryTypeName = "WpfInspector.TestHelper.SessionEntryPoint";
    private const string WorkerEntryMethodName = "RunAsync";
    private static readonly object Sync = new();
    private static readonly object LogSync = new();

    private static int _activeSessionCount;
    private static int _startedSessionCount;
    private static int _completedSessionCount;
    private static int _successfulUnloadCount;
    private static int _failedUnloadCount;
    private static string? _lastError;
    private static TaskCompletionSource<bool> _idleSignal = CreateCompletedSignal();

    internal static void StartSession(string fullPipeName)
    {
        lock (Sync)
        {
            if (_activeSessionCount == 0)
            {
                _idleSignal = CreatePendingSignal();
            }

            _activeSessionCount++;
            _startedSessionCount++;
        }

        _ = Task.Run(() => RunSessionAsync(fullPipeName));
    }

    internal static InjectionHostDiagnostics GetDiagnostics()
    {
        lock (Sync)
        {
            return new InjectionHostDiagnostics(
                ActiveSessionCount: _activeSessionCount,
                StartedSessionCount: _startedSessionCount,
                CompletedSessionCount: _completedSessionCount,
                SuccessfulUnloadCount: _successfulUnloadCount,
                FailedUnloadCount: _failedUnloadCount,
                LastError: _lastError);
        }
    }

    internal static async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        Task idleTask;
        lock (Sync)
        {
            idleTask = _idleSignal.Task;
        }

        if (idleTask.IsCompleted)
        {
            return true;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await idleTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task RunSessionAsync(string fullPipeName)
    {
        var unloadSucceeded = false;
        string? error = null;

        try
        {
            unloadSucceeded = await ExecuteWorkerSessionAsync(fullPipeName).ConfigureAwait(false);
            if (!unloadSucceeded)
            {
                error = "Collectible worker AssemblyLoadContext did not unload within the timeout window.";
                Log(error);
                Log(DescribeCurrentLoadContexts());
            }
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            Log($"Worker session failed: {exception}");
        }
        finally
        {
            lock (Sync)
            {
                _activeSessionCount--;
                _completedSessionCount++;

                if (unloadSucceeded)
                {
                    _successfulUnloadCount++;
                }
                else
                {
                    _failedUnloadCount++;
                }

                if (!string.IsNullOrWhiteSpace(error))
                {
                    _lastError = error;
                }

                if (_activeSessionCount == 0)
                {
                    _idleSignal.TrySetResult(true);
                }
            }
        }
    }

    private static async Task<bool> ExecuteWorkerSessionAsync(string fullPipeName)
    {
        var workerAssemblyPath = ResolveWorkerAssemblyPath();
        if (!File.Exists(workerAssemblyPath))
        {
            throw new FileNotFoundException(
                "Collectible worker assembly was not found.",
                workerAssemblyPath);
        }

        var unloadMarker = InvokeWorkerInCollectibleContext(workerAssemblyPath, fullPipeName);

        return await WaitForUnloadAsync(unloadMarker, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static UnloadMarker InvokeWorkerInCollectibleContext(
        string workerAssemblyPath,
        string fullPipeName)
    {
        var loadContext = new InjectedWorkerLoadContext(workerAssemblyPath);
        var weakReference = new WeakReference(loadContext, trackResurrection: false);

        try
        {
            ExecuteWorkerEntry(loadContext, workerAssemblyPath, fullPipeName);
        }
        finally
        {
            loadContext.Unload();
        }

        return new UnloadMarker(weakReference, loadContext.Name ?? string.Empty);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExecuteWorkerEntry(
        InjectedWorkerLoadContext loadContext,
        string workerAssemblyPath,
        string fullPipeName)
    {
        var workerAssembly = loadContext.LoadFromAssemblyPath(workerAssemblyPath);
        var workerEntryType = workerAssembly.GetType(WorkerEntryTypeName, throwOnError: true, ignoreCase: false);
        var workerEntryMethod = workerEntryType?.GetMethod(
            WorkerEntryMethodName,
            BindingFlags.Public | BindingFlags.Static);

        if (workerEntryMethod is null)
        {
            throw new MissingMethodException(WorkerEntryTypeName, WorkerEntryMethodName);
        }

        WaitForWorkerInvocation(workerEntryMethod, fullPipeName);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WaitForWorkerInvocation(MethodInfo workerEntryMethod, string fullPipeName)
    {
        object? invocationResult;
        try
        {
            invocationResult = workerEntryMethod.Invoke(null, [fullPipeName]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }

        switch (invocationResult)
        {
            case Task<int> taskWithExitCode:
                EnsureZeroExitCode(taskWithExitCode.GetAwaiter().GetResult());
                return;
            case ValueTask<int> valueTaskWithExitCode:
                EnsureZeroExitCode(valueTaskWithExitCode.GetAwaiter().GetResult());
                return;
            case Task task:
                task.GetAwaiter().GetResult();
                return;
            case ValueTask valueTask:
                valueTask.GetAwaiter().GetResult();
                return;
            case int exitCode:
                EnsureZeroExitCode(exitCode);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unexpected worker entry return type: {invocationResult?.GetType().FullName ?? "null"}");
        }
    }

    private static void EnsureZeroExitCode(int exitCode)
    {
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Collectible worker returned exit code {exitCode}.");
        }
    }

    private static async Task<bool> WaitForUnloadAsync(UnloadMarker unloadMarker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!IsWorkerContextUnloaded(unloadMarker) && DateTime.UtcNow < deadline)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(50).ConfigureAwait(false);
        }

        return IsWorkerContextUnloaded(unloadMarker);
    }

    private static string ResolveWorkerAssemblyPath()
    {
        var hostAssemblyPath = typeof(EntryPoint).Assembly.Location;
        var hostDirectory = Path.GetDirectoryName(hostAssemblyPath);
        if (string.IsNullOrWhiteSpace(hostDirectory))
        {
            hostDirectory = AppContext.BaseDirectory;
        }

        return Path.Combine(hostDirectory, WorkerAssemblyFileName);
    }

    private static TaskCompletionSource<bool> CreatePendingSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> CreateCompletedSignal()
    {
        var signal = CreatePendingSignal();
        signal.TrySetResult(true);
        return signal;
    }

    private static void Log(string message)
    {
        try
        {
            var logPath = Path.Combine(Path.GetTempPath(), $"wpfi_host_{Environment.ProcessId}.log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (LogSync)
            {
                File.AppendAllText(logPath, line);
            }
        }
        catch
        {
        }
    }

    private static string DescribeCurrentLoadContexts()
    {
        var contexts = AssemblyLoadContext.All
            .Select(context =>
            {
                var assemblies = context.Assemblies
                    .Select(assembly => assembly.GetName().Name ?? "<unknown>")
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                return $"{context.Name ?? "<default>"} [collectible={context.IsCollectible}] => {string.Join(", ", assemblies)}";
            })
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();

        return "AssemblyLoadContext snapshot: " + string.Join(" || ", contexts);
    }

    private static bool IsWorkerContextUnloaded(UnloadMarker unloadMarker)
    {
        if (!unloadMarker.WeakReference.IsAlive)
        {
            return true;
        }

        return !AssemblyLoadContext.All.Any(context =>
            string.Equals(context.Name, unloadMarker.LoadContextName, StringComparison.Ordinal));
    }

    private sealed class InjectedWorkerLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public InjectedWorkerLoadContext(string workerAssemblyPath)
            : base(name: $"WpfInspector.TestRuntime::{Guid.NewGuid():N}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(workerAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath is null ? nint.Zero : LoadUnmanagedDllFromPath(libraryPath);
        }
    }

    private sealed record UnloadMarker(
        WeakReference WeakReference,
        string LoadContextName);
}

public sealed record InjectionHostDiagnostics(
    int ActiveSessionCount,
    int StartedSessionCount,
    int CompletedSessionCount,
    int SuccessfulUnloadCount,
    int FailedUnloadCount,
    string? LastError);
