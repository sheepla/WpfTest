using System.IO.Pipes;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WpfInspector.InspectionPayload;

namespace WpfInspector.UITests;

public sealed class InProcessWpfAppFixture : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private Thread? _uiThread;
    private Dispatcher? _dispatcher;
    private Application? _application;
    private ObservabilityFixtureWindow? _window;
    private RpcPipeConnection? _rpc;

    public async Task InitializeAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _uiThread = new Thread(() =>
        {
            try
            {
                _application = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };

                _dispatcher = Dispatcher.CurrentDispatcher;
                _window = new ObservabilityFixtureWindow();
                _window.Loaded += (_, _) => started.TrySetResult();
                _window.Show();

                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                started.TrySetException(exception);
            }
        });

        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.IsBackground = true;
        _uiThread.Start();

        await started.Task.WaitAsync(StartupTimeout);
        await WaitUntilAsync(
            window => FindDescendant<TextBox>(window, static textBox => textBox.Name == "ValidationFixtureTextBox") is TextBox textBox
                && string.Equals(textBox.Text, "bad", StringComparison.Ordinal),
            StartupTimeout,
            "Fixture surfaces did not finish initializing.");

        _rpc = await RpcPipeConnection.StartAsync(StartupTimeout);
    }

    public async Task DisposeAsync()
    {
        if (_rpc is not null)
        {
            await _rpc.DisposeAsync();
            _rpc = null;
        }

        if (_dispatcher is not null)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _window?.Close();
                _application?.Shutdown();
            });
        }

        if (_uiThread is not null)
        {
            _uiThread.Join(millisecondsTimeout: 5_000);
        }
    }

    private Task<T> InvokeAsync<T>(Func<ObservabilityFixtureWindow, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return GetDispatcher().InvokeAsync(() => action(GetWindow())).Task;
    }

    private Task InvokeAsync(Action<ObservabilityFixtureWindow> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return GetDispatcher().InvokeAsync(() => action(GetWindow())).Task;
    }

    private async Task WaitUntilAsync(
        Func<ObservabilityFixtureWindow, bool> predicate,
        TimeSpan? timeout = null,
        string? failureMessage = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var deadline = DateTime.UtcNow + (timeout ?? DefaultWaitTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await InvokeAsync(predicate))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(failureMessage ?? "Timed out waiting for the in-process WPF app state.");
    }

    public Task<WpfInspector.Core.Models.WpfInspectionResult> InspectCurrentProcessAsync() =>
        Task.Run(SelfInspection.InspectCurrentProcess);

    public Task ResetAsync() =>
        InvokeAsync(window => window.ResetState());

    public Task<RpcResponse> SendRequestAsync(string operation, object? arguments = null) =>
        GetRpc().SendRequestAsync(operation, arguments);

    public async Task<T> SendSuccessfulRequestAsync<T>(string operation, object? arguments = null)
    {
        var response = await SendRequestAsync(operation, arguments);
        if (!response.Success)
        {
            throw new InvalidOperationException(
                $"RPC '{operation}' failed: {response.ErrorCode}: {response.ErrorMessage}");
        }

        return DeserializeData<T>(response);
    }

    public async Task<JsonObject> SendSuccessfulObjectAsync(string operation, object? arguments = null)
    {
        var response = await SendRequestAsync(operation, arguments);
        if (!response.Success)
        {
            throw new InvalidOperationException(
                $"RPC '{operation}' failed: {response.ErrorCode}: {response.ErrorMessage}");
        }

        return response.Data as JsonObject
            ?? throw new InvalidOperationException(
                $"RPC '{operation}' returned {response.Data?.GetType().Name ?? "null"} instead of JsonObject.");
    }

    public static T DeserializeData<T>(RpcResponse response)
    {
        if (response.Data is null)
        {
            throw new InvalidOperationException("RPC response did not contain data.");
        }

        return response.Data.Deserialize<T>(JsonOptions)
            ?? throw new InvalidOperationException($"Could not deserialize RPC data to {typeof(T).Name}.");
    }

    private Dispatcher GetDispatcher() =>
        _dispatcher ?? throw new InvalidOperationException("The in-process dispatcher is not available.");

    private ObservabilityFixtureWindow GetWindow() =>
        _window ?? throw new InvalidOperationException("The in-process fixture window is not available.");

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T typed && predicate(typed))
            {
                return typed;
            }

            var found = FindDescendant(child, predicate);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private RpcPipeConnection GetRpc() =>
        _rpc ?? throw new InvalidOperationException("The helper RPC connection is not available.");

    private sealed class RpcPipeConnection : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private RpcPipeConnection(NamedPipeServerStream pipe, StreamReader reader, StreamWriter writer)
        {
            _pipe = pipe;
            _reader = reader;
            _writer = writer;
        }

        public static async Task<RpcPipeConnection> StartAsync(TimeSpan timeout)
        {
            var pipeName = $"wpfinspector-uitest-{Guid.NewGuid():N}";
            var fullPipeName = $@"\\.\pipe\{pipeName}";
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                StartEntryPoint(fullPipeName);

                using var cts = new CancellationTokenSource(timeout);
                await pipe.WaitForConnectionAsync(cts.Token);

                var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024,
                    leaveOpen: true);
                var writer = new StreamWriter(
                    pipe,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                var ready = await reader.ReadLineAsync(cts.Token);
                if (!string.Equals(ready, "READY", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unexpected helper greeting: {ready}");
                }

                return new RpcPipeConnection(pipe, reader, writer);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        public async Task<RpcResponse> SendRequestAsync(string operation, object? arguments)
        {
            await _lock.WaitAsync();
            try
            {
                var request = new RpcRequest
                {
                    Operation = operation,
                    Arguments = ToJsonObject(arguments)
                };

                await _writer.WriteLineAsync(Encode(request));
                var payload = await _reader.ReadLineAsync()
                    ?? throw new InvalidOperationException("The helper pipe closed without returning a response.");

                return Decode<RpcResponse>(payload)
                    ?? throw new InvalidOperationException($"Could not decode helper response: {payload}");
            }
            finally
            {
                _lock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _writer.WriteLineAsync("EXIT");
            }
            catch
            {
            }

            _writer.Dispose();
            _reader.Dispose();
            await _pipe.DisposeAsync();
            _lock.Dispose();
        }

        private static void StartEntryPoint(string fullPipeName)
        {
            var bytes = Encoding.UTF8.GetBytes(fullPipeName + '\0');
            var buffer = Marshal.AllocHGlobal(bytes.Length);

            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                var exitCode = WpfInspector.TestHelper.EntryPoint.Run(buffer, bytes.Length);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"TestHelper.EntryPoint.Run returned {exitCode}.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static JsonObject ToJsonObject(object? arguments)
        {
            if (arguments is null)
            {
                return [];
            }

            if (arguments is JsonObject jsonObject)
            {
                return (JsonObject)jsonObject.DeepClone();
            }

            if (JsonSerializer.SerializeToNode(arguments, JsonOptions) is JsonObject node)
            {
                return node;
            }

            throw new InvalidOperationException("RPC arguments must serialize to a JSON object.");
        }

        private static string Encode<T>(T value)
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            return "RPC:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        private static T? Decode<T>(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload) || !payload.StartsWith("RPC:", StringComparison.Ordinal))
            {
                return default;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload["RPC:".Length..]));
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
    }
}

public sealed class RpcRequest
{
    public string Version { get; init; } = "wpfinspector.rpc.v1";

    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");

    public string Operation { get; init; } = string.Empty;

    public JsonObject Arguments { get; init; } = [];
}

public sealed class RpcResponse
{
    public string Version { get; init; } = "wpfinspector.rpc.v1";

    public string? RequestId { get; init; }

    public bool Success { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public JsonNode? Data { get; init; }
}
