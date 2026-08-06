using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Slack.Services;

/// <summary>
/// The active lease as the durable authority records it. Carries no target
/// and no secrets; the caller already knows the target, and secrets are
/// resolved separately into lease <em>response</em> DTOs only.
/// <see cref="CredentialFingerprint"/> is the one-way fingerprint of the
/// credential generation this lease was issued against; it is fencing
/// metadata, never the secret itself.
/// </summary>
public sealed record SlackLeaseRecord(
    string LeaseId,
    string Kind,
    int Generation,
    string AdapterId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string? CredentialFingerprint);

/// <summary>
/// Durable, per-target authority for Socket lease generation fencing. Every
/// mutation is atomic and encodes the fencing rule it names, so a stale
/// adapter holding a superseded or expired lease can never renew or confirm.
/// </summary>
public interface ISlackLeaseStore
{
    /// <summary>
    /// Issues a new lease, bumping the target generation. Any prior lease is
    /// superseded; its lease id becomes stale and cannot renew.
    /// <paramref name="credentialFingerprint"/> pins the credential
    /// generation the lease was issued against so renewal and hello can fail
    /// closed once the credential is resupplied.
    /// </summary>
    Task<SlackLeaseRecord> IssueAsync(
        string targetKey,
        string kind,
        string adapterId,
        DateTimeOffset expiresAt,
        DateTimeOffset now,
        string? credentialFingerprint,
        CancellationToken ct = default);

    /// <summary>
    /// Extends the lease matching <paramref name="leaseId"/> + <paramref name="adapterId"/>
    /// only while it is still the active, unexpired lease. Returns null when
    /// the lease was superseded, expired, or unknown.
    /// </summary>
    Task<SlackLeaseRecord?> RenewAsync(
        string targetKey,
        string leaseId,
        string adapterId,
        DateTimeOffset expiresAt,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Confirms a single validation lease hello: the lease must be the active
    /// validation lease and unexpired. On success bumps the generation and
    /// clears the active lease, fencing it against any further renew or hello.
    /// </summary>
    Task<bool> ConfirmHelloAsync(
        string targetKey,
        string leaseId,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<SlackLeaseRecord?> GetActiveAsync(string targetKey, CancellationToken ct = default);

    Task<int> GetGenerationAsync(string targetKey, CancellationToken ct = default);
}

/// <summary>
/// Deterministic in-memory <see cref="ISlackLeaseStore"/>. Mirrors the
/// atomic fencing semantics of the EF-backed store so the lease core is
/// exercisable in-process and unit-tested without a database. Not the
/// production authority.
/// </summary>
public sealed class InMemorySlackLeaseStore : ISlackLeaseStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _counter;

    public Task<SlackLeaseRecord> IssueAsync(
        string targetKey, string kind, string adapterId, DateTimeOffset expiresAt, DateTimeOffset now,
        string? credentialFingerprint, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var leaseId = NewLeaseId();
            if (!_entries.TryGetValue(targetKey, out var entry))
            {
                entry = new Entry { Generation = 0 };
                _entries[targetKey] = entry;
            }
            entry.Generation++;
            entry.Lease = new ActiveLease(leaseId, kind, entry.Generation, adapterId, now, expiresAt, credentialFingerprint);
            return Task.FromResult(ToRecord(entry.Lease));
        }
    }

    public Task<SlackLeaseRecord?> RenewAsync(
        string targetKey, string leaseId, string adapterId, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(targetKey, out var entry) || entry.Lease is not { } lease)
                return Task.FromResult<SlackLeaseRecord?>(null);
            if (!Matches(lease, leaseId, adapterId) || lease.ExpiresAt <= now)
                return Task.FromResult<SlackLeaseRecord?>(null);
            lease = lease with { ExpiresAt = expiresAt };
            entry.Lease = lease;
            return Task.FromResult<SlackLeaseRecord?>(ToRecord(lease));
        }
    }

    public Task<bool> ConfirmHelloAsync(string targetKey, string leaseId, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(targetKey, out var entry) || entry.Lease is not { } lease)
                return Task.FromResult(false);
            if (lease.Kind != SlackLeaseKind.Validation
                || !Matches(lease, leaseId, lease.AdapterId)
                || lease.ExpiresAt <= now)
                return Task.FromResult(false);
            entry.Generation++;
            entry.Lease = null;
            return Task.FromResult(true);
        }
    }

    public Task<SlackLeaseRecord?> GetActiveAsync(string targetKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(targetKey, out var entry) && entry.Lease is { } lease
                ? Task.FromResult<SlackLeaseRecord?>(ToRecord(lease))
                : Task.FromResult<SlackLeaseRecord?>(null);
        }
    }

    public Task<int> GetGenerationAsync(string targetKey, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.TryGetValue(targetKey, out var entry) ? entry.Generation : 0);
        }
    }

    private static bool Matches(ActiveLease lease, string leaseId, string adapterId) =>
        string.Equals(lease.LeaseId, leaseId, StringComparison.Ordinal)
        && string.Equals(lease.AdapterId, adapterId, StringComparison.Ordinal);

    private static SlackLeaseRecord ToRecord(ActiveLease lease) =>
        new(lease.LeaseId, lease.Kind, lease.Generation, lease.AdapterId, lease.IssuedAt, lease.ExpiresAt,
            lease.CredentialFingerprint);

    private string NewLeaseId() => $"lease_{++_counter}";

    private sealed class Entry
    {
        public int Generation { get; set; }
        public ActiveLease? Lease { get; set; }
    }

    private sealed record ActiveLease(
        string LeaseId, string Kind, int Generation, string AdapterId, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt,
        string? CredentialFingerprint);
}
