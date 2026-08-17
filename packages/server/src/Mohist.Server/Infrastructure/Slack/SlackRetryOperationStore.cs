using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

public sealed class SlackRetryOperationStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _time;

    public SlackRetryOperationStore(IDbContextFactory<MohistDbContext> dbFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    public async Task<SlackRetryOperationClaimResult> CreateOrLoadAsync(
        SlackRetryOperationDraft draft,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var now = _time.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var existing = await db.SlackRetryOperations
            .SingleOrDefaultAsync(row => row.ProjectId == draft.ProjectId && row.ActionKey == draft.ActionKey, ct);
        if (existing is not null)
        {
            await transaction.CommitAsync(ct);
            return new SlackRetryOperationClaimResult(existing, false);
        }

        var row = new SlackRetryOperationRow
        {
            Id = $"slkretry_{draft.ActionKey}",
            ProjectId = draft.ProjectId,
            ActionKey = draft.ActionKey,
            ConnectionId = draft.ConnectionId,
            SessionId = draft.SessionId,
            FailedInputId = draft.FailedInputId,
            FailedTurnId = draft.FailedTurnId,
            DispatchRef = draft.DispatchRef,
            WorkspaceTeamId = draft.Source.WorkspaceTeamId,
            ConversationId = draft.Source.ConversationId,
            MessageTs = draft.Source.MessageTs,
            ThreadTs = draft.ThreadTs,
            OriginalDirectMessage = draft.OriginalDirectMessage,
            ActorSlackUserId = draft.ActorSlackUserId,
            RetryDispatchKey = draft.RetryDispatchKey,
            AttemptKind = draft.AttemptKind,
            PreMintedSessionId = draft.PreMintedSessionId,
            PreMintedInputId = draft.PreMintedInputId,
            PreMintedTurnId = draft.PreMintedTurnId,
            FollowupOperationId = draft.FollowupOperationId,
            State = SlackRetryOperationStates.DispatchPending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SlackRetryOperations.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new SlackRetryOperationClaimResult(row, true);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await using var retryDb = await _dbFactory.CreateDbContextAsync(ct);
            var winner = await retryDb.SlackRetryOperations.SingleAsync(
                candidate => candidate.ProjectId == draft.ProjectId && candidate.ActionKey == draft.ActionKey,
                ct);
            return new SlackRetryOperationClaimResult(winner, false);
        }
    }

    public async Task<SlackRetryOperationRow?> GetAsync(
        string projectId,
        string actionKey,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackRetryOperations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ProjectId == projectId && row.ActionKey == actionKey, ct);
    }

    public async Task<SlackRetryOperationRow?> RecordAdmissionAsync(
        string projectId,
        string actionKey,
        string inputId,
        string turnId,
        string followupOperationId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackRetryOperations.SingleOrDefaultAsync(
            candidate => candidate.ProjectId == projectId && candidate.ActionKey == actionKey, ct);
        if (row is null)
            return null;
        row.PreMintedInputId ??= inputId;
        row.PreMintedTurnId ??= turnId;
        row.FollowupOperationId ??= followupOperationId;
        row.UpdatedAt = _time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<SlackRetryOperationRow?> CompleteAsync(
        string projectId,
        string actionKey,
        string outcome,
        string? reason,
        string? sessionId,
        string? inputId,
        string? turnId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackRetryOperations.SingleOrDefaultAsync(
            candidate => candidate.ProjectId == projectId && candidate.ActionKey == actionKey, ct);
        if (row is null)
            return null;
        if (row.Outcome is null)
        {
            row.State = SlackRetryOperationStates.Completed;
            row.Outcome = outcome;
            row.ResultReason = reason;
            row.ResultSessionId = sessionId;
            row.ResultInputId = inputId;
            row.ResultTurnId = turnId;
            row.RecoveryLeaseId = null;
            row.RecoveryLeaseExpiresAt = null;
            row.UpdatedAt = _time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return row;
    }

    public async Task<SlackRetryOperationRow?> ClaimRecoveryAsync(
        string projectId,
        string actionKey,
        string leaseId,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var expires = now.Add(leaseDuration);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SlackRetryOperations"
            SET "RecoveryLeaseId" = {leaseId},
                "RecoveryLeaseExpiresAt" = {expires},
                "UpdatedAt" = {now}
            WHERE "ProjectId" = {projectId}
              AND "ActionKey" = {actionKey}
              AND "State" = {SlackRetryOperationStates.DispatchPending}
              AND ("RecoveryLeaseExpiresAt" IS NULL OR "RecoveryLeaseExpiresAt" <= {now})
            """, ct);
        return changed == 0
            ? null
            : await db.SlackRetryOperations.AsNoTracking().SingleAsync(
                row => row.ProjectId == projectId && row.ActionKey == actionKey, ct);
    }

    public async Task<IReadOnlyList<SlackRetryOperationRow>> ListDuePendingAsync(
        DateTimeOffset now,
        int limit = 100,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SlackRetryOperations.AsNoTracking()
            .Where(row => row.State == SlackRetryOperationStates.DispatchPending)
            .ToListAsync(ct);
        return rows
            .Where(row => row.RecoveryLeaseExpiresAt is null || row.RecoveryLeaseExpiresAt <= now)
            .OrderBy(row => row.UpdatedAt)
            .Take(limit)
            .ToArray();
    }

    public static string ActionKey(string actionValue) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actionValue))).ToLowerInvariant();

    public static string RetryDispatchKey(string projectId, string actionKey) =>
        $"slack-retry:{projectId}:{actionKey}";

    public static string ResultReference(string actionKey) =>
        $"slack-retry-result:{actionKey}";

}
