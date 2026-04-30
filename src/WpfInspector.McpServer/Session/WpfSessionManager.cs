using System.Collections.Concurrent;

namespace WpfInspector.McpServer.Session;

internal sealed class WpfSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, WpfSession> _sessions = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly string _baseDirectory = AppContext.BaseDirectory;

    public async Task<WpfSession> GetOrCreateSessionAsync(int pid, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(pid, out var existing) && existing.IsConnected)
            return existing;

        await _createLock.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(pid, out existing) && existing.IsConnected)
                return existing;

            if (_sessions.TryRemove(pid, out var stale))
                await stale.DisposeAsync();

            var session = await WpfSession.CreateAsync(pid, _baseDirectory, ct);
            _sessions[pid] = session;
            return session;
        }
        finally
        {
            _createLock.Release();
        }
    }

    public async Task<bool> DisconnectAsync(int pid)
    {
        if (!_sessions.TryRemove(pid, out var session))
            return false;

        await session.DisposeAsync();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        var sessions = _sessions.Values.ToArray();
        _sessions.Clear();
        await Task.WhenAll(sessions.Select(s => s.DisposeAsync().AsTask()));
        _createLock.Dispose();
    }
}
