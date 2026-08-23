using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Workspace-scoped dedup store for the ambiguity chooser. The claim fence
/// and the original input facts are one durable write so a winner retry can
/// only reuse the exact same message facts and candidate snapshot.
/// </summary>
public sealed class SlackAmbiguousPromptStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public SlackAmbiguousPromptStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public Task<SlackAmbiguousPromptResult> TryClaimAsync(
        string projectId,
        string workspaceTeamId,
        string conversationId,
        string messageTs,
        string? threadTs,
        string winningConnectionId,
        IReadOnlyList<string> mentionedConnectionIds,
        CancellationToken ct = default) =>
        TryClaimAsync(
            projectId,
            workspaceTeamId,
            conversationId,
            messageTs,
            threadTs,
            winningConnectionId,
            mentionedConnectionIds.Select(connectionId =>
                new SlackSelectionCandidateReference(projectId, connectionId)).ToArray(),
            senderSlackUserId: "legacy",
            taskText: string.Empty,
            filesJson: "[]",
            ambiguityKind: SlackAmbiguityKinds.Legacy,
            ct);

    public async Task<SlackAmbiguousPromptResult> TryClaimAsync(
        string projectId,
        string workspaceTeamId,
        string conversationId,
        string messageTs,
        string? threadTs,
        string winningConnectionId,
        IReadOnlyList<SlackSelectionCandidateReference> candidates,
        string senderSlackUserId,
        string taskText,
        string filesJson,
        string ambiguityKind,
        CancellationToken ct = default)
    {
        ValidateRequired(projectId, nameof(projectId));
        ValidateRequired(workspaceTeamId, nameof(workspaceTeamId));
        ValidateRequired(conversationId, nameof(conversationId));
        ValidateRequired(messageTs, nameof(messageTs));
        ValidateRequired(winningConnectionId, nameof(winningConnectionId));
        ValidateRequired(senderSlackUserId, nameof(senderSlackUserId));
        ValidateRequired(ambiguityKind, nameof(ambiguityKind));
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count < 2 || candidates.Any(candidate =>
                string.IsNullOrWhiteSpace(candidate.ProjectId)
                || string.IsNullOrWhiteSpace(candidate.ConnectionId)))
            throw new ArgumentException("At least two complete candidate references are required.", nameof(candidates));
        if (!SlackAmbiguityKinds.IsDefined(ambiguityKind))
            throw new ArgumentException("Unknown ambiguity kind.", nameof(ambiguityKind));
        if (filesJson is null)
            throw new ArgumentNullException(nameof(filesJson));
        if (taskText is null)
            throw new ArgumentNullException(nameof(taskText));

        var now = _timeProvider.GetUtcNow();
        var dispatchRef = PromptDispatchRef(workspaceTeamId, conversationId, messageTs);
        var candidateSnapshot = candidates
            .Distinct()
            .ToArray();
        if (candidateSnapshot.Length < 2
            || !candidateSnapshot.Any(candidate => string.Equals(
                candidate.ConnectionId, winningConnectionId, StringComparison.Ordinal)))
            throw new ArgumentException("The winner must be one of the complete candidate references.", nameof(candidates));
        var candidateJson = JSON.Serialize(candidateSnapshot);
        var mentionedConnectionIds = candidateSnapshot
            .Select(candidate => candidate.ConnectionId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var mentionedJson = JSON.Serialize(mentionedConnectionIds);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "SlackAmbiguousPrompts" (
                "Id", "ProjectId", "WorkspaceTeamId", "ConversationId", "MessageTs",
                "ThreadTs", "WinningConnectionId", "MentionedConnectionIdsJson",
                "SenderSlackUserId", "TaskText", "FilesJson", "AmbiguityKind",
                "CandidateReferencesJson", "SelectionState", "AttemptCount",
                "PromptedAt", "CreatedAt", "UpdatedAt")
            VALUES (
                {$"slkamb_{Guid.NewGuid():N}"}, {projectId}, {workspaceTeamId},
                {conversationId}, {messageTs}, {threadTs}, {winningConnectionId},
                {mentionedJson}, {senderSlackUserId}, {taskText}, {filesJson},
                {ambiguityKind}, {candidateJson}, {SlackSelectionStates.Pending}, 0,
                {now}, {now}, {now})
            ON CONFLICT("WorkspaceTeamId", "ConversationId", "MessageTs") DO NOTHING;
            """, ct);

        var existing = await db.SlackAmbiguousPrompts.AsNoTracking()
            .Where(row => row.WorkspaceTeamId == workspaceTeamId
                && row.ConversationId == conversationId
                && row.MessageTs == messageTs)
            .Select(row => new SlackAmbiguousPromptSnapshot(
                row.Id,
                row.ProjectId,
                row.WorkspaceTeamId,
                row.ConversationId,
                row.MessageTs,
                row.WinningConnectionId,
                row.ThreadTs,
                row.MentionedConnectionIdsJson,
                row.SenderSlackUserId,
                row.TaskText,
                row.FilesJson,
                row.AmbiguityKind,
                row.CandidateReferencesJson,
                row.SelectionState,
                row.ChosenProjectId,
                row.ChosenConnectionId,
                row.DispatchKind,
                row.SelectionSessionId,
                row.SelectionInputId,
                row.SelectionTurnId,
                row.AttemptCount,
                row.LastAttemptAt,
                row.FinishedAt,
                row.SettleReason,
                row.PromptedAt,
                row.CreatedAt,
                row.UpdatedAt))
            .SingleAsync(ct);

        var claimed = inserted > 0;
        if (!claimed && string.Equals(existing.WinningConnectionId, winningConnectionId, StringComparison.Ordinal))
        {
            var deliveryExists = await db.SlackOutboxRows.AsNoTracking()
                .AnyAsync(row => row.ProjectId == existing.ProjectId
                    && row.ConnectionId == winningConnectionId
                    && row.Kind == SlackOutboxKinds.UserAction
                    && row.DispatchRef == dispatchRef, ct);
            claimed = !deliveryExists;
        }

        return new SlackAmbiguousPromptResult(
            claimed,
            existing,
            DeserializeMentioned(existing.MentionedConnectionIdsJson));
    }

    public async Task<SlackAmbiguousPromptSnapshot?> FindAsync(
        string workspaceTeamId,
        string conversationId,
        string messageTs,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackAmbiguousPrompts.AsNoTracking()
            .Where(row => row.WorkspaceTeamId == workspaceTeamId
                && row.ConversationId == conversationId
                && row.MessageTs == messageTs)
            .Select(row => new SlackAmbiguousPromptSnapshot(
                row.Id, row.ProjectId, row.WorkspaceTeamId, row.ConversationId,
                row.MessageTs, row.WinningConnectionId, row.ThreadTs,
                row.MentionedConnectionIdsJson, row.SenderSlackUserId, row.TaskText,
                row.FilesJson, row.AmbiguityKind, row.CandidateReferencesJson,
                row.SelectionState, row.ChosenProjectId, row.ChosenConnectionId,
                row.DispatchKind, row.SelectionSessionId, row.SelectionInputId,
                row.SelectionTurnId, row.AttemptCount, row.LastAttemptAt,
                row.FinishedAt, row.SettleReason, row.PromptedAt, row.CreatedAt,
                row.UpdatedAt))
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// Atomically records the selection and all pre-allocated execution ids.
    /// The Pending predicate is the single decision fence: only the caller
    /// that changes the row may dispatch work for the ambiguous message.
    /// </summary>
    public async Task<SlackAmbiguousPromptDecisionResult> TryDecideAsync(
        string workspaceTeamId,
        string conversationId,
        string messageTs,
        string chosenProjectId,
        string chosenConnectionId,
        string dispatchKind,
        string selectionSessionId,
        string selectionInputId,
        string selectionTurnId,
        CancellationToken ct = default)
    {
        ValidateRequired(workspaceTeamId, nameof(workspaceTeamId));
        ValidateRequired(conversationId, nameof(conversationId));
        ValidateRequired(messageTs, nameof(messageTs));
        ValidateRequired(chosenProjectId, nameof(chosenProjectId));
        ValidateRequired(chosenConnectionId, nameof(chosenConnectionId));
        ValidateRequired(dispatchKind, nameof(dispatchKind));
        ValidateRequired(selectionSessionId, nameof(selectionSessionId));
        ValidateRequired(selectionInputId, nameof(selectionInputId));
        ValidateRequired(selectionTurnId, nameof(selectionTurnId));
        if (dispatchKind is not (SlackSelectionDispatchKinds.RootLaunch
            or SlackSelectionDispatchKinds.ThreadLaunch
            or SlackSelectionDispatchKinds.ThreadFollowup))
            throw new ArgumentException("Unknown selection dispatch kind.", nameof(dispatchKind));

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var changed = await db.SlackAmbiguousPrompts
            .Where(row => row.WorkspaceTeamId == workspaceTeamId
                && row.ConversationId == conversationId
                && row.MessageTs == messageTs
                && row.SelectionState == SlackSelectionStates.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.SelectionState, SlackSelectionStates.Decided)
                .SetProperty(row => row.ChosenProjectId, chosenProjectId)
                .SetProperty(row => row.ChosenConnectionId, chosenConnectionId)
                .SetProperty(row => row.DispatchKind, dispatchKind)
                .SetProperty(row => row.DecidedAt, now)
                .SetProperty(row => row.SelectionSessionId, selectionSessionId)
                .SetProperty(row => row.SelectionInputId, selectionInputId)
                .SetProperty(row => row.SelectionTurnId, selectionTurnId)
                .SetProperty(row => row.UpdatedAt, now), ct);

        var snapshot = await FindAsync(workspaceTeamId, conversationId, messageTs, ct)
            ?? throw new InvalidOperationException("The ambiguity claim disappeared while recording a selection.");
        return new SlackAmbiguousPromptDecisionResult(changed > 0, snapshot);
    }

    /// <summary>
    /// Returns the rows that need one bounded obligation pass. The caller
    /// supplies the retry interval so the database remains the scheduling
    /// fence: a second worker cannot immediately claim the same Decided row.
    /// The query is project/state/updated-at shaped to use the selection
    /// obligation index rather than scanning finished history.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListProjectIdsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackAmbiguousPrompts.AsNoTracking()
            .Where(row => row.SelectionState == SlackSelectionStates.Pending
                || row.SelectionState == SlackSelectionStates.Decided)
            .Select(row => row.ProjectId)
            .Distinct()
            .OrderBy(projectId => projectId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SlackAmbiguousPromptSnapshot>> ListByStateAsync(
        string projectId,
        string selectionState,
        DateTimeOffset updatedBefore,
        CancellationToken ct = default)
    {
        ValidateRequired(projectId, nameof(projectId));
        ValidateRequired(selectionState, nameof(selectionState));
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SlackAmbiguousPrompts
            .FromSqlInterpolated($"""
                SELECT * FROM "SlackAmbiguousPrompts"
                WHERE "ProjectId" = {projectId}
                  AND "SelectionState" = {selectionState}
                  AND "UpdatedAt" <= {updatedBefore}
                ORDER BY "UpdatedAt"
                """)
            .AsNoTracking()
            .ToListAsync(ct);
        return rows.Select(ToSnapshot).ToArray();
    }

    public async Task<IReadOnlyList<SlackAmbiguousPromptSnapshot>> ListSettledSinceAsync(
        DateTimeOffset cutoff,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SlackAmbiguousPrompts.AsNoTracking()
            .Where(row => row.SelectionState == SlackSelectionStates.Settled)
            .ToListAsync(ct);
        return rows
            .Where(row => row.FinishedAt is not null && row.FinishedAt >= cutoff)
            .Select(ToSnapshot)
            .ToArray();
    }

    /// <summary>
    /// Claims a Decided row for dispatch and records the attempt before any
    /// provider or Agent call. This is deliberately separate from the
    /// Pending-to-Decided selection fence: recovery never changes the chosen
    /// candidate or re-runs click-time authorization.
    /// </summary>
    public async Task<bool> TryBeginDispatchAsync(
        string rowId,
        DateTimeOffset now,
        TimeSpan retryInterval,
        CancellationToken ct = default)
    {
        var retryCutoff = now - retryInterval;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SlackAmbiguousPrompts"
            SET "AttemptCount" = "AttemptCount" + 1,
                "LastAttemptAt" = {now},
                "UpdatedAt" = {now}
            WHERE "Id" = {rowId}
              AND "SelectionState" = {SlackSelectionStates.Decided}
              AND ("LastAttemptAt" IS NULL OR "LastAttemptAt" <= {retryCutoff});
            """, ct);
        return changed == 1;
    }

    public async Task<bool> MarkCompletedAsync(
        string rowId,
        string? result,
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SlackAmbiguousPrompts"
            SET "SelectionState" = {SlackSelectionStates.Completed},
                "FinishedAt" = {now},
                "SettleReason" = {result},
                "UpdatedAt" = {now}
            WHERE "Id" = {rowId}
              AND "SelectionState" = {SlackSelectionStates.Decided};
            """, ct);
        return changed == 1;
    }

    public async Task<bool> TrySettleAsync(
        string rowId,
        string expectedState,
        string reason,
        CancellationToken ct = default)
    {
        ValidateRequired(rowId, nameof(rowId));
        ValidateRequired(expectedState, nameof(expectedState));
        ValidateRequired(reason, nameof(reason));
        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SlackAmbiguousPrompts"
            SET "SelectionState" = {SlackSelectionStates.Settled},
                "FinishedAt" = {now},
                "SettleReason" = {reason},
                "UpdatedAt" = {now}
            WHERE "Id" = {rowId}
              AND "SelectionState" = {expectedState};
            """, ct);
        return changed == 1;
    }

    public async Task<int> DeleteFinishedBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SlackAmbiguousPrompts
            .Where(row => row.SelectionState == SlackSelectionStates.Completed
                || row.SelectionState == SlackSelectionStates.Settled)
            .ToListAsync(ct);
        rows = rows
            .Where(row => row.FinishedAt is not null && row.FinishedAt < cutoff)
            .ToList();
        if (rows.Count == 0)
            return 0;
        db.SlackAmbiguousPrompts.RemoveRange(rows);
        return await db.SaveChangesAsync(ct);
    }

    public static string PromptDispatchRef(string workspaceTeamId, string conversationId, string messageTs) =>
        $"slack-ambiguous:{workspaceTeamId}:{conversationId}:{messageTs}";

    public static string SettlementDispatchRef(string workspaceTeamId, string conversationId, string messageTs) =>
        $"slack-ambiguous-outcome:{workspaceTeamId}:{conversationId}:{messageTs}";

    private static SlackAmbiguousPromptSnapshot ToSnapshot(SlackAmbiguousPromptRow row) =>
        new(
            row.Id,
            row.ProjectId,
            row.WorkspaceTeamId,
            row.ConversationId,
            row.MessageTs,
            row.WinningConnectionId,
            row.ThreadTs,
            row.MentionedConnectionIdsJson,
            row.SenderSlackUserId,
            row.TaskText,
            row.FilesJson,
            row.AmbiguityKind,
            row.CandidateReferencesJson,
            row.SelectionState,
            row.ChosenProjectId,
            row.ChosenConnectionId,
            row.DispatchKind,
            row.SelectionSessionId,
            row.SelectionInputId,
            row.SelectionTurnId,
            row.AttemptCount,
            row.LastAttemptAt,
            row.FinishedAt,
            row.SettleReason,
            row.PromptedAt,
            row.CreatedAt,
            row.UpdatedAt);

    private static void ValidateRequired(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);
    }

    private static IReadOnlyList<string> DeserializeMentioned(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JSON.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}

public sealed record SlackAmbiguousPromptSnapshot(
    string Id,
    string ProjectId,
    string WorkspaceTeamId,
    string ConversationId,
    string MessageTs,
    string WinningConnectionId,
    string? ThreadTs,
    string MentionedConnectionIdsJson,
    string SenderSlackUserId,
    string TaskText,
    string FilesJson,
    string AmbiguityKind,
    string CandidateReferencesJson,
    string SelectionState,
    string? ChosenProjectId,
    string? ChosenConnectionId,
    string? DispatchKind,
    string? SelectionSessionId,
    string? SelectionInputId,
    string? SelectionTurnId,
    int AttemptCount,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? FinishedAt,
    string? SettleReason,
    DateTimeOffset PromptedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SlackAmbiguousPromptResult(
    bool Claimed,
    SlackAmbiguousPromptSnapshot Snapshot,
    IReadOnlyList<string> MentionedConnectionIds)
{
    public string RowId => Snapshot.Id;
    public string WinningConnectionId => Snapshot.WinningConnectionId;
    public string? ThreadTs => Snapshot.ThreadTs;
}

public sealed record SlackAmbiguousPromptDecisionResult(
    bool Decided,
    SlackAmbiguousPromptSnapshot Snapshot);
