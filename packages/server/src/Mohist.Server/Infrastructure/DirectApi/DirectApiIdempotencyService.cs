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
    bool Created,
    bool StopOutcomeUnknown = false);

public sealed record DirectApiLaunchOutcome(
    string CoordinatorKey,
    string? JobId = null,
    string? SessionId = null,
    string? InputId = null,
    string? TurnId = null,
    string? RejectionCode = null,
    string? RejectionReason = null);

public sealed record DirectApiFollowupOutcome(
    string ProjectId,
    string SessionId,
    string? AgentId,
    string? InputId = null,
    string? TurnId = null,
    string? RejectionCode = null,
    string? RejectionReason = null,
    string? SnapshotJson = null);

public sealed record DirectApiStopOutcome(
    string ProjectId,
    string SessionId,
    string TurnId,
    string OperationId);

public sealed record DirectApiFrozenStopTarget(
    string ProjectId,
    string SessionId,
    string TurnId,
    long TurnRevision,
    long ContextGeneration,
    DirectApiFrozenStopBinding Binding,
    DateTimeOffset? DeadlineAt,
    string OperationId);

public sealed record DirectApiFrozenStopBinding(
    string? RunnerId,
    string? SourceKind,
    string? WorkflowRunId,
    string? SessionName,
    string? Runtime,
    string? RuntimeSessionId,
    string? WorkDir);

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

    public async Task<DirectApiIdempotencyMappingRow?> FindAsync(
        string command,
        string scopeKey,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DirectApiIdempotencyMappings.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Command == command && row.ScopeKey == scopeKey,
                ct);
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
        catch (DbUpdateException) when (command == DirectApiCommands.Stop && turnId is not null)
        {
            // The composite key conflict is the caller's own replay/reuse
            // path. A missing composite winner for stop means the filtered
            // pending-turn index won instead, so do not persist the losing
            // caller's mapping or let the provider exception escape.
            db.ChangeTracker.Clear();
            existing = await db.DirectApiIdempotencyMappings
                .FirstOrDefaultAsync(
                    row => row.Command == command && row.ScopeKey == scopeKey,
                    ct);
            if (existing is not null)
                return new DirectApiMappingClaim(existing, Created: false);

            var pending = await db.DirectApiIdempotencyMappings
                .FirstOrDefaultAsync(
                    row => row.Command == DirectApiCommands.Stop
                        && row.TurnId == turnId
                        && row.State == DirectApiMappingStates.Pending,
                    ct);
            if (pending is not null)
                return new DirectApiMappingClaim(
                    pending,
                    Created: false,
                    StopOutcomeUnknown: true);
            throw;
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

    public async Task<DirectApiIdempotencyMappingRow?> FindPendingStopAsync(
        string turnId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DirectApiIdempotencyMappings
            .FirstOrDefaultAsync(
                row => row.Command == DirectApiCommands.Stop
                    && row.TurnId == turnId
                    && row.State == DirectApiMappingStates.Pending,
                ct);
    }

    public async Task<DirectApiIdempotencyMappingRow> FreezeStopTargetAsync(
        string scopeKey,
        string frozenTarget,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var mapping = await db.DirectApiIdempotencyMappings
            .FirstOrDefaultAsync(
                row => row.Command == DirectApiCommands.Stop && row.ScopeKey == scopeKey,
                ct)
            ?? throw new InvalidOperationException("The direct API stop mapping disappeared before freezing its target.");

        if (mapping.State == DirectApiMappingStates.Pending
            && string.IsNullOrWhiteSpace(mapping.FrozenTarget))
        {
            mapping.FrozenTarget = frozenTarget;
            await db.SaveChangesAsync(ct);
        }

        return mapping;
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

    public async Task<DirectApiIdempotencyMappingRow> FreezeCompletedOutcomeAsync(
        string command,
        string scopeKey,
        string expectedOutcome,
        string frozenOutcome,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.DirectApiIdempotencyMappings
            .Where(row => row.Command == command
                && row.ScopeKey == scopeKey
                && row.State == DirectApiMappingStates.Completed
                && row.Outcome == expectedOutcome)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.Outcome, frozenOutcome),
                ct);
        return await db.DirectApiIdempotencyMappings.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Command == command && row.ScopeKey == scopeKey,
                ct)
            ?? throw new InvalidOperationException("The direct API mapping disappeared while freezing its response.");
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
