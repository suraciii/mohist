using Mohist.Server.Infrastructure.Data.Events;

namespace Mohist.Server.TestSupport;

/// <summary>
/// In-memory <see cref="IDispatchStreamLeaseStore"/> mirroring the SQL
/// store's semantics: exclusive claim, expiry steal, backoff parking, and
/// owner-checked mutation. Used by dispatcher unit specs; the SQL store's
/// own behavior is covered by L1 application tests against real SQLite.
/// </summary>
public sealed class FakeDispatchStreamLeaseStore : IDispatchStreamLeaseStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Origin, string Source), LeaseRow> _rows = [];

    public Func<string, string, bool>? ThrowOnPark { get; set; }

    public int Rows
    {
        get
        {
            lock (_gate)
            {
                return _rows.Count;
            }
        }
    }

    public Task<int?> ClaimAsync(
        string origin,
        string source,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_rows.TryGetValue((origin, source), out var row))
            {
                var parked = row.NextAttemptAt is { } next && next > now;
                var live = row.LeaseUntil > now && row.LeaseOwner != owner;
                if (parked || live)
                    return Task.FromResult<int?>(null);
                var attempts = row.Attempts;
                _rows[(origin, source)] = row with
                {
                    LeaseOwner = owner,
                    LeaseUntil = now + leaseDuration,
                    NextAttemptAt = null,
                };
                return Task.FromResult<int?>(attempts);
            }

            _rows[(origin, source)] = new LeaseRow(owner, now + leaseDuration, 0, null, null);
            return Task.FromResult<int?>(0);
        }
    }

    public Task<bool> ParkAsync(
        string origin,
        string source,
        string owner,
        int attempts,
        DateTimeOffset nextAttemptAt,
        string? lastError,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (ThrowOnPark?.Invoke(origin, source) == true)
            throw new InvalidOperationException($"simulated park failure for {origin}/{source}");
        lock (_gate)
        {
            if (!_rows.TryGetValue((origin, source), out var row) || row.LeaseOwner != owner)
                return Task.FromResult(false);
            _rows[(origin, source)] = row with
            {
                Attempts = attempts,
                NextAttemptAt = nextAttemptAt,
                // Parked leases expire at the backoff gate: any worker may
                // reclaim the stream when the next attempt is due.
                LeaseUntil = nextAttemptAt,
                LastError = lastError,
                UpdatedAt = now,
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> TouchAsync(
        string origin,
        string source,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue((origin, source), out var row) || row.LeaseOwner != owner)
                return Task.FromResult(false);
            _rows[(origin, source)] = row with { LeaseUntil = now + leaseDuration, UpdatedAt = now };
            return Task.FromResult(true);
        }
    }

    public Task<bool> ResetAttemptsAsync(
        string origin,
        string source,
        string owner,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue((origin, source), out var row) || row.LeaseOwner != owner)
                return Task.FromResult(false);
            _rows[(origin, source)] = row with { Attempts = 0, NextAttemptAt = null, LastError = null, UpdatedAt = now };
            return Task.FromResult(true);
        }
    }

    public Task ReleaseAsync(
        string origin,
        string source,
        string owner,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_rows.TryGetValue((origin, source), out var row) && row.LeaseOwner == owner)
                _rows.Remove((origin, source));
        }
        return Task.CompletedTask;
    }

    public Task<int> CountParkedAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.Values.Count(row => row.NextAttemptAt is { } next && next > now));
        }
    }

    public (int Attempts, string? LastError)? Snapshot(string origin, string source)
    {
        lock (_gate)
        {
            return _rows.TryGetValue((origin, source), out var row)
                ? (row.Attempts, row.LastError)
                : null;
        }
    }

    private sealed record LeaseRow(
        string LeaseOwner,
        DateTimeOffset LeaseUntil,
        int Attempts,
        DateTimeOffset? NextAttemptAt,
        string? LastError)
    {
        public DateTimeOffset UpdatedAt { get; init; }
    }
}
