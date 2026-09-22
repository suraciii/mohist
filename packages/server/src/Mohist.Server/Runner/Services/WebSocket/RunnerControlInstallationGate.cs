namespace Mohist.Server.Runner.Services.WebSocket;

internal sealed class RunnerControlConnectionReservation(Guid connectionId)
{
    public Guid ConnectionId { get; } = connectionId;
}

internal sealed class RunnerControlInstallationGate
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    public async Task<IDisposable> AcquireAsync(string runnerId, Action? waiting, CancellationToken ct)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(runnerId, out entry!))
            {
                entry = new Entry();
                _entries.Add(runnerId, entry);
            }
            entry.References++;
        }

        try
        {
            waiting?.Invoke();
            await entry.Gate.WaitAsync(ct);
            return new Lease(this, runnerId, entry);
        }
        catch
        {
            ReleaseReference(runnerId, entry);
            throw;
        }
    }

    private void Release(string runnerId, Entry entry)
    {
        entry.Gate.Release();
        ReleaseReference(runnerId, entry);
    }

    private void ReleaseReference(string runnerId, Entry entry)
    {
        lock (_sync)
        {
            entry.References--;
            if (entry.References == 0
                && _entries.TryGetValue(runnerId, out var current)
                && ReferenceEquals(current, entry))
                _entries.Remove(runnerId);
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References { get; set; }
    }

    private sealed class Lease(RunnerControlInstallationGate owner, string runnerId, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(runnerId, entry);
        }
    }
}
