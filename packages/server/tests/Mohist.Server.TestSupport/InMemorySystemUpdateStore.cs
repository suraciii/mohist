using Mohist.Server.SystemInfo;

namespace Mohist.Server.TestSupport;

internal sealed class InMemorySystemUpdateStore : ISystemUpdateStore
{
    private readonly object _gate = new();
    private SystemUpdateJobState? _latest;
    private string? _lockOwner;

    public Task<SystemUpdateJobState?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(_latest);
    }

    public Task<bool> TryAcquireLockAsync(string jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_lockOwner is not null || _latest is { Status: "running" or "waiting-for-reconnect" })
                return Task.FromResult(false);

            _lockOwner = jobId;
            return Task.FromResult(true);
        }
    }

    public Task ReleaseLockAsync(string jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_lockOwner == jobId)
                _lockOwner = null;
        }

        return Task.CompletedTask;
    }

    public Task<bool> ReleaseStaleLockAsync(string jobId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_lockOwner is not null && _lockOwner != jobId)
                return Task.FromResult(false);

            _lockOwner = null;
            return Task.FromResult(true);
        }
    }

    public Task SaveAsync(SystemUpdateJobState state, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            _latest = state;

        return Task.CompletedTask;
    }

    public Task<bool> SaveIfCurrentAsync(
        SystemUpdateJobState expected,
        SystemUpdateJobState next,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_latest is null
                || _latest.JobId != expected.JobId
                || _latest.Status != expected.Status)
            {
                return Task.FromResult(false);
            }

            _latest = next;
            return Task.FromResult(true);
        }
    }
}
