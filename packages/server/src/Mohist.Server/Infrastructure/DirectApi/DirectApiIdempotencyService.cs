using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.DirectApi;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.DirectApi;

public static class DirectApiCommands
{
    public const string Launch = "launch";
    public const string Followup = "followup";
    public const string Stop = "stop";
}

public static class DirectApiMappingStates
{
    public const string Pending = "pending";
    public const string Completed = "completed";
    public const string Rejected = "rejected";
}

public sealed record DirectApiMappingClaim(
    DirectApiIdempotencyMappingRow Mapping,
    bool Created);

public sealed record DirectApiLaunchOutcome(
    string CoordinatorKey,
    string? JobId = null,
    string? SessionId = null,
    string? InputId = null,
    string? TurnId = null,
    string? RejectionCode = null,
    string? RejectionReason = null);

/// <summary>
/// Owns the relational request fence shared by the direct launch,
/// follow-up, and stop commands. The insert is committed separately from
/// the canonical operation so a process loss leaves a retryable pending row.
/// </summary>
public sealed class DirectApiIdempotencyService : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public DirectApiIdempotencyService(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<DirectApiMappingClaim> GetOrCreateAsync(
        string command,
        string scopeKey,
        string callerKeyId,
        string fingerprint,
        string? turnId,
        string initialOutcome,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.DirectApiIdempotencyMappings
            .FirstOrDefaultAsync(
                row => row.Command == command && row.ScopeKey == scopeKey,
                ct);
        if (existing is not null)
            return new DirectApiMappingClaim(existing, Created: false);

        var mapping = new DirectApiIdempotencyMappingRow
        {
            Command = command,
            ScopeKey = scopeKey,
            CallerKeyId = callerKeyId,
            Fingerprint = fingerprint,
            State = DirectApiMappingStates.Pending,
            Outcome = initialOutcome,
            TurnId = turnId,
            CreatedAt = _timeProvider.GetUtcNow(),
        };
        db.DirectApiIdempotencyMappings.Add(mapping);
        try
        {
            await db.SaveChangesAsync(ct);
            return new DirectApiMappingClaim(mapping, Created: true);
        }
        catch (DbUpdateException)
        {
            // Another request can win the composite unique key between the
            // lookup and insert. The winner is the durable request fence;
            // reload it and let the caller classify fingerprint reuse.
            db.ChangeTracker.Clear();
            existing = await db.DirectApiIdempotencyMappings
                .FirstOrDefaultAsync(
                    row => row.Command == command && row.ScopeKey == scopeKey,
                    ct);
            if (existing is null)
                throw;
            return new DirectApiMappingClaim(existing, Created: false);
        }
    }

    public async Task<DirectApiIdempotencyMappingRow> CompleteAsync(
        string command,
        string scopeKey,
        string state,
        string outcome,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var mapping = await db.DirectApiIdempotencyMappings
            .FirstOrDefaultAsync(
                row => row.Command == command && row.ScopeKey == scopeKey,
                ct)
            ?? throw new InvalidOperationException("The direct API mapping disappeared before completion.");

        if (mapping.State == DirectApiMappingStates.Pending)
        {
            mapping.State = state;
            mapping.Outcome = outcome;
            mapping.CompletedAt = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        return mapping;
    }

    public static T ReadOutcome<T>(DirectApiIdempotencyMappingRow mapping)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(mapping.Outcome))
            throw new InvalidOperationException("The direct API mapping has no outcome.");
        return JSON.Deserialize<T>(mapping.Outcome)
            ?? throw new InvalidOperationException("The direct API mapping outcome is invalid.");
    }
}
