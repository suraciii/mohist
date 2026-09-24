using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Idempotency;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Idempotency;

public static class IdempotencyCommands
{
    public const string Launch = "launch";
    public const string Followup = "followup";
    public const string Stop = "stop";
    public const string IssueStart = "issue_start";
    public const string WorkflowControl = "workflow_control";
}

public static class IdempotencyMappingStates
{
    public const string Pending = "pending";
    public const string Completed = "completed";
    public const string Rejected = "rejected";
}

/// <summary>
/// The value facts a keyed caller classifies: whether the fence created the
/// row, the request scope the caller supplied, and the row's decision state.
/// The API layer works with these facts and never observes the fence row.
/// </summary>
public sealed record IdempotencyClaim(
    bool Created,
    string ScopeKey,
    string Fingerprint,
    string State,
    string? Outcome,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? TurnId = null,
    string? FrozenTarget = null,
    bool StopOutcomeUnknown = false);

/// <summary>
/// Owns the relational request fence shared by the external Agent API's
/// launch, follow-up, and stop commands and the control-plane writes. The
/// insert is committed separately from the canonical operation so a process
/// loss leaves a retryable pending row.
/// </summary>
public sealed class IdempotencyFence : IScopedService
{
    public static readonly TimeSpan PendingLease = TimeSpan.FromSeconds(30);

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public IdempotencyFence(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<IdempotencyClaim?> FindAsync(
        string command,
        string scopeKey,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.IdempotencyMappings.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Command == command && row.ScopeKey == scopeKey,
                ct);
        return row is null ? null : ClaimOf(row, Created: false);
    }

    public async Task<IdempotencyClaim> GetOrCreateAsync(
        string command,
        string scopeKey,
        string callerKeyId,
        string fingerprint,
        string? turnId,
        string? initialOutcome,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.IdempotencyMappings
            .FirstOrDefaultAsync(
                row => row.Command == command && row.ScopeKey == scopeKey,
                ct);
        if (existing is not null)
            return ClaimOf(existing, Created: false);

        var mapping = new IdempotencyMappingRow
        {
            Command = command,
            ScopeKey = scopeKey,
            CallerKeyId = callerKeyId,
            Fingerprint = fingerprint,
            State = IdempotencyMappingStates.Pending,
            Outcome = initialOutcome,
            TurnId = turnId,
            CreatedAt = _timeProvider.GetUtcNow(),
        };
        db.IdempotencyMappings.Add(mapping);
        try
        {
            await db.SaveChangesAsync(ct);
            return ClaimOf(mapping, Created: true);
        }
        catch (DbUpdateException) when (command == IdempotencyCommands.Stop && turnId is not null)
        {
            // The composite key conflict is the caller's own replay/reuse
            // path. A missing composite winner for stop means the filtered
            // pending-turn index won instead, so do not persist the losing
            // caller's mapping or let the provider exception escape.
            db.ChangeTracker.Clear();
            existing = await db.IdempotencyMappings
                .FirstOrDefaultAsync(
                    row => row.Command == command && row.ScopeKey == scopeKey,
                    ct);
            if (existing is not null)
                return ClaimOf(existing, Created: false);

            var pending = await db.IdempotencyMappings
                .FirstOrDefaultAsync(
                    row => row.Command == IdempotencyCommands.Stop
                        && row.TurnId == turnId
                        && row.State == IdempotencyMappingStates.Pending,
                    ct);
            if (pending is not null)
                return ClaimOf(
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
            existing = await db.IdempotencyMappings
                .FirstOrDefaultAsync(
                    row => row.Command == command && row.ScopeKey == scopeKey,
                    ct);
            if (existing is null)
                throw;
            return ClaimOf(existing, Created: false);
        }
    }

    /// <summary>
    /// Projects a fence row into the value facts its caller classifies. The
    /// row type stays behind the fence boundary.
    /// </summary>
    private static IdempotencyClaim ClaimOf(
        IdempotencyMappingRow row,
        bool Created,
        bool StopOutcomeUnknown = false) => new(
        Created: Created,
        ScopeKey: row.ScopeKey,
        Fingerprint: row.Fingerprint,
        State: row.State,
        Outcome: row.Outcome,
        CreatedAt: row.CreatedAt,
        CompletedAt: row.CompletedAt,
        TurnId: row.TurnId,
        FrozenTarget: row.FrozenTarget,
        StopOutcomeUnknown: StopOutcomeUnknown);

    public async Task<IdempotencyClaim?> FindPendingStopAsync(
        string turnId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.IdempotencyMappings.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Command == IdempotencyCommands.Stop
                    && row.TurnId == turnId
                    && row.State == IdempotencyMappingStates.Pending,
                ct);
        return row is null ? null : ClaimOf(row, Created: false);
    }

    public async Task<IdempotencyClaim> FreezeStopTargetAsync(
        string scopeKey,
        string frozenTarget,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var mapping = await db.IdempotencyMappings
            .FirstOrDefaultAsync(
                row => row.Command == IdempotencyCommands.Stop && row.ScopeKey == scopeKey,
                ct)
            ?? throw new InvalidOperationException("The request fence row disappeared before freezing its target.");

        if (mapping.State == IdempotencyMappingStates.Pending
            && string.IsNullOrWhiteSpace(mapping.FrozenTarget))
        {
            mapping.FrozenTarget = frozenTarget;
            await db.SaveChangesAsync(ct);
        }

        return ClaimOf(mapping, Created: false);
    }

    /// <summary>
    /// Records a classified outcome (accepted or a rejection the Server
    /// decided) on a pending row. Rows that already carry an outcome are
    /// left untouched so a replay always observes the first decision.
    /// </summary>
    public async Task<IdempotencyClaim> CompleteAsync(
        string command,
        string scopeKey,
        string state,
        string outcome,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var mapping = await db.IdempotencyMappings
            .FirstOrDefaultAsync(
                row => row.Command == command && row.ScopeKey == scopeKey,
                ct)
            ?? throw new InvalidOperationException("The request fence row disappeared before completion.");

        if (mapping.State == IdempotencyMappingStates.Pending)
        {
            mapping.State = state;
            mapping.Outcome = outcome;
            mapping.CompletedAt = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        return ClaimOf(mapping, Created: false);
    }

    /// <summary>
    /// Removes a still-pending row after an unclassified failure so the
    /// operation stays retryable. An outcome that was already recorded is
    /// never removed.
    /// </summary>
    public async Task AbandonPendingAsync(
        string command,
        string scopeKey,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.IdempotencyMappings
            .Where(row => row.Command == command
                && row.ScopeKey == scopeKey
                && row.State == IdempotencyMappingStates.Pending)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<IdempotencyClaim> FreezeCompletedOutcomeAsync(
        string command,
        string scopeKey,
        string expectedOutcome,
        string frozenOutcome,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.IdempotencyMappings
            .Where(row => row.Command == command
                && row.ScopeKey == scopeKey
                && row.State == IdempotencyMappingStates.Completed
                && row.Outcome == expectedOutcome)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.Outcome, frozenOutcome),
                ct);
        var row = await db.IdempotencyMappings.AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Command == command && row.ScopeKey == scopeKey,
                ct)
            ?? throw new InvalidOperationException("The request fence row disappeared while freezing its response.");
        return ClaimOf(row, Created: false);
    }

    public static T ReadOutcome<T>(string? outcome)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(outcome))
            throw new InvalidOperationException("The request fence row has no outcome.");
        return JSON.Deserialize<T>(outcome)
            ?? throw new InvalidOperationException("The request fence row outcome is invalid.");
    }
}
