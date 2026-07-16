using Mohist.Server.SystemInfo;

namespace Mohist.Server.SpecTests.Support;

public sealed class InMemorySystemUpdateStore : ISystemUpdateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SystemUpdateJobState? _latest;
    private string? _lockOwner;

    public async Task<SystemUpdateJobState?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _latest;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryAcquireLockAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lockOwner is not null || _latest is not null && SystemUpdateService.IsActive(_latest))
                return false;
            _lockOwner = jobId;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseLockAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lockOwner == jobId)
                _lockOwner = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ReleaseStaleLockAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lockOwner is not null && _lockOwner != jobId)
                return false;
            _lockOwner = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SystemUpdateJobState state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _latest = state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SaveIfCurrentAsync(
        SystemUpdateJobState expected,
        SystemUpdateJobState next,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_latest is null
                || _latest.JobId != expected.JobId
                || _latest.Status != expected.Status)
            {
                return false;
            }
            _latest = next;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
