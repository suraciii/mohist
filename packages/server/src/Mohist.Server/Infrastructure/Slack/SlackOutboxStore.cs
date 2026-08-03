using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Persistence boundary for the Slack outbound outbox. Producers write
/// rows via <see cref="EnqueueAsync"/>; the dispatcher reminder and the
/// <c>mohist-slack</c> adapter advance the state machine via
/// <see cref="ClaimAsync"/>, <see cref="MarkDeliveredAsync"/>,
/// <see cref="MarkDeliveryUncertainAsync"/>, and
/// <see cref="MarkDeadLetteredAsync"/>. State transitions are atomic
/// against the row's <see cref="SlackOutboxRow.State"/> — concurrent
/// attempts to advance the same row are rejected with
/// <see cref="SlackOutboxStateException"/> so no two consumers
/// (dispatcher reminder + adapter) race past each other.
/// </summary>
/// <remarks>
/// Replaceable progress merging is keyed on
/// <c>(ConnectionId, DispatchRef, Kind = ReplaceableProgress, State =
/// Pending)</c>. When the row leaves Pending the merge target is gone
/// and a new ReplaceableProgress for the same ref starts a fresh row;
/// the dispatcher's sweep plus the adapter's claim both move rows out
/// of Pending, so merges are safe.
/// </remarks>
public sealed class SlackOutboxStore : IScopedService, IAgentConnectionProviderCleanup
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISlackConnectionHealthBackpressurer _healthBackpressurer;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<SlackProviderOptions> _options;

    public SlackOutboxStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        ISlackConnectionHealthBackpressurer healthBackpressurer,
        TimeProvider timeProvider,
        IOptions<SlackProviderOptions> options)
    {
        _dbFactory = dbFactory;
        _healthBackpressurer = healthBackpressurer;
        _timeProvider = timeProvider;
        _options = options;
    }

    /// <summary>
    /// Enqueues a new outbox row. For ReplaceableProgress drafts whose
    /// <c>(ConnectionId, DispatchRef)</c> matches a Pending row of the
    /// same kind, the existing row's payload is updated in place —
    /// older progress is collapsed into the latest before the adapter
    /// can claim it. Non-replaceable drafts always insert; capacity is
    /// checked first because the spec says terminal / failure /
    /// user-action rows MUST NOT be silently dropped, so when the
    /// outbox is full we surface that by flipping the Connection to
    /// Degraded(Backpressured) instead of throwing the draft away.
    /// </summary>
    public async Task<SlackOutboxEnqueueResult> EnqueueAsync(SlackOutboxDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (draft.Kind == SlackOutboxKinds.ReplaceableProgress
            && !string.IsNullOrEmpty(draft.DispatchRef))
        {
            var merge = await TryMergeReplaceableAsync(db, draft, ct);
            if (merge is not null)
            {
                await transaction.CommitAsync(ct);
                return merge;
            }
        }
        else
        {
            var pendingRows = await db.SlackOutboxRows
                .Where(row => row.ConnectionId == draft.ConnectionId
                    && row.State == SlackOutboxStates.Pending)
                .CountAsync(ct);
            if (pendingRows >= _options.Value.OutboxCapacityPerConnection)
            {
                await transaction.RollbackAsync(ct);
                await _healthBackpressurer.FlipBackpressuredAsync(
                    draft.ProjectId,
                    draft.ConnectionId,
                    SlackProviderBackpressureReasons.OutboxOverflow,
                    ct);
                throw new SlackOutboxCapacityExceededException(
                    draft.ProjectId, draft.ConnectionId, _options.Value.OutboxCapacityPerConnection);
            }
        }

        var now = _timeProvider.GetUtcNow();
        var row = new SlackOutboxRow
        {
            Id = $"slkout_{Guid.NewGuid():N}",
            ProjectId = draft.ProjectId,
            ConnectionId = draft.ConnectionId,
            WorkspaceTeamId = draft.WorkspaceTeamId,
            ConversationId = draft.ConversationId,
            ThreadTs = draft.ThreadTs,
            Kind = draft.Kind,
            State = SlackOutboxStates.Pending,
            DispatchRef = draft.DispatchRef,
            PayloadJson = draft.PayloadJson,
            AttemptCount = 0,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SlackOutboxRows.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new SlackOutboxEnqueueResult(row.Id, MergedIntoExisting: false);
    }

    public async Task<SlackOutboxEnqueueResult> EnqueueRequiredAsync(SlackOutboxDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);
        if (!SlackOutboxKinds.IsTerminal(draft.Kind) || string.IsNullOrWhiteSpace(draft.DispatchRef))
            throw new ArgumentException("Required deliveries need a terminal kind and dispatch reference.", nameof(draft));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await IsEnabledConnectionAsync(db, draft.ProjectId, draft.ConnectionId, ct))
        {
            await transaction.CommitAsync(ct);
            return new SlackOutboxEnqueueResult(string.Empty, MergedIntoExisting: false, Suppressed: true);
        }
        var existing = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == draft.ConnectionId
                && row.Kind == draft.Kind
                && row.DispatchRef == draft.DispatchRef)
            .Select(row => new { row.Id })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            await transaction.CommitAsync(ct);
            return new SlackOutboxEnqueueResult(existing.Id, MergedIntoExisting: true);
        }

        var pendingRows = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == draft.ConnectionId && row.State == SlackOutboxStates.Pending)
            .CountAsync(ct);
        var backpressured = pendingRows >= _options.Value.OutboxCapacityPerConnection;
        var now = _timeProvider.GetUtcNow();
        var row = new SlackOutboxRow
        {
            Id = $"slkout_{Guid.NewGuid():N}",
            ProjectId = draft.ProjectId,
            ConnectionId = draft.ConnectionId,
            WorkspaceTeamId = draft.WorkspaceTeamId,
            ConversationId = draft.ConversationId,
            ThreadTs = draft.ThreadTs,
            Kind = draft.Kind,
            State = SlackOutboxStates.Pending,
            DispatchRef = draft.DispatchRef,
            PayloadJson = draft.PayloadJson,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.SlackOutboxRows.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDispatchRefConflict(ex))
        {
            db.Entry(row).State = EntityState.Detached;
            var duplicate = await db.SlackOutboxRows.AsNoTracking()
                .Where(r => r.ConnectionId == draft.ConnectionId
                    && r.Kind == draft.Kind
                    && r.DispatchRef == draft.DispatchRef)
                .Select(r => new { r.Id })
                .FirstOrDefaultAsync(ct);
            await transaction.CommitAsync(ct);
            return new SlackOutboxEnqueueResult(duplicate?.Id ?? string.Empty, MergedIntoExisting: true);
        }

        if (backpressured)
            await _healthBackpressurer.FlipBackpressuredAsync(
                draft.ProjectId, draft.ConnectionId, SlackProviderBackpressureReasons.OutboxOverflow, ct);
        return new SlackOutboxEnqueueResult(row.Id, MergedIntoExisting: false);
    }

    public async Task<SlackOutboxEnqueueResult?> PromotePendingProgressAsync(
        SlackOutboxDraft terminalDraft,
        string? progressDispatchRef = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(terminalDraft);
        ValidateDraft(terminalDraft);
        if (!SlackOutboxKinds.IsTerminal(terminalDraft.Kind) || string.IsNullOrWhiteSpace(terminalDraft.DispatchRef))
            throw new ArgumentException("Terminal promotion needs a terminal kind and dispatch reference.", nameof(terminalDraft));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var progress = await db.SlackOutboxRows.FirstOrDefaultAsync(row =>
            row.ProjectId == terminalDraft.ProjectId
            && row.ConnectionId == terminalDraft.ConnectionId
            && row.DispatchRef == (progressDispatchRef ?? terminalDraft.DispatchRef)
            && row.Kind == SlackOutboxKinds.ReplaceableProgress
            && row.State == SlackOutboxStates.Pending, ct);
        if (progress is null)
            return null;

        progress.Kind = terminalDraft.Kind;
        progress.DispatchRef = terminalDraft.DispatchRef;
        progress.PayloadJson = terminalDraft.PayloadJson;
        progress.ThreadTs = terminalDraft.ThreadTs;
        progress.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new SlackOutboxEnqueueResult(progress.Id, MergedIntoExisting: true);
    }

    public async Task<SlackOutboxEnqueueResult> UpsertReplaceableProgressAsync(
        SlackOutboxDraft draft,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);
        if (draft.Kind != SlackOutboxKinds.ReplaceableProgress || string.IsNullOrWhiteSpace(draft.DispatchRef))
            throw new ArgumentException("The draft must be replaceable progress.", nameof(draft));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.SlackOutboxRows.FirstOrDefaultAsync(row =>
            row.ConnectionId == draft.ConnectionId
            && row.DispatchRef == draft.DispatchRef
            && row.Kind == SlackOutboxKinds.ReplaceableProgress, ct);
        var now = _timeProvider.GetUtcNow();
        if (existing is not null)
        {
            var previousPayload = SlackDeliveryPayload.Parse(existing.PayloadJson);
            var nextPayload = SlackDeliveryPayload.Parse(draft.PayloadJson);
            existing.PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                nextPayload with
                {
                    ProviderMessageIdentity = nextPayload.ProviderMessageIdentity
                        ?? previousPayload.ProviderMessageIdentity,
                });
            existing.ThreadTs = draft.ThreadTs;
            existing.State = SlackOutboxStates.Pending;
            existing.NextAttemptAt = now;
            existing.ClaimedAt = null;
            existing.ClaimedByAdapterId = null;
            existing.DeliveredAt = null;
            existing.DeliveryUncertainAt = null;
            existing.DeadLetteredAt = null;
            existing.LastError = null;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return new SlackOutboxEnqueueResult(existing.Id, MergedIntoExisting: true);
        }

        return await EnqueueAsync(draft, ct);
    }

    private static bool IsDispatchRefConflict(DbUpdateException ex)
    {
        for (var inner = (Exception?)ex; inner is not null; inner = inner.InnerException)
        {
            var message = inner.Message;
            if (message.Contains("UX_SlackOutboxRows_ConnectionId_DispatchRef_Kind", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static Task<bool> IsEnabledConnectionAsync(
        MohistDbContext db,
        string projectId,
        string connectionId,
        CancellationToken ct) =>
        db.AgentConnections.AnyAsync(connection =>
            connection.ProjectId == projectId
            && connection.Id == connectionId
            && connection.DeletedAt == null
            && connection.DesiredState == DesiredStateKind.Enabled,
            ct);

    private async Task<SlackOutboxEnqueueResult?> TryMergeReplaceableAsync(
        MohistDbContext db,
        SlackOutboxDraft draft,
        CancellationToken ct)
    {
        var existing = await db.SlackOutboxRows
            .Where(row => row.ConnectionId == draft.ConnectionId
                && row.DispatchRef == draft.DispatchRef
                && row.Kind == SlackOutboxKinds.ReplaceableProgress
                && row.State == SlackOutboxStates.Pending)
            .FirstOrDefaultAsync(ct);
        if (existing is null)
            return null;

        existing.PayloadJson = draft.PayloadJson;
        existing.ThreadTs = draft.ThreadTs;
        existing.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new SlackOutboxEnqueueResult(existing.Id, MergedIntoExisting: true);
    }

    /// <summary>
    /// Atomically transitions one Pending row to Claimed for the
    /// <paramref name="adapterId"/>. Returns null when no Pending row
    /// is available — the dispatcher uses this to distinguish "queue
    /// empty" from "claim succeeded". Concurrent calls race in SQLite:
    /// only the first wins; the second observes a row whose State has
    /// already moved to Claimed and the search excludes it.
    /// </summary>
    public async Task<SlackOutboxEntry?> ClaimAsync(string projectId, string connectionId, string adapterId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(adapterId))
            throw new ArgumentException("AdapterId is required.", nameof(adapterId));

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var candidates = await db.SlackOutboxRows
            .Where(row => row.ProjectId == projectId
                && row.ConnectionId == connectionId
                && row.State == SlackOutboxStates.Pending
                && db.AgentConnections.Any(connection =>
                    connection.ProjectId == projectId
                    && connection.Id == connectionId
                    && connection.DeletedAt == null
                    && connection.DesiredState == DesiredStateKind.Enabled))
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
        var candidate = candidates.FirstOrDefault(row =>
            row.NextAttemptAt is null || row.NextAttemptAt <= now);
        if (candidate is null)
            return null;

        var changed = await db.SlackOutboxRows
            .Where(row => row.Id == candidate.Id
                && row.ProjectId == projectId
                && row.ConnectionId == connectionId
                && row.State == SlackOutboxStates.Pending
                && db.AgentConnections.Any(connection =>
                    connection.ProjectId == projectId
                    && connection.Id == connectionId
                    && connection.DeletedAt == null
                    && connection.DesiredState == DesiredStateKind.Enabled))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, SlackOutboxStates.Claimed)
                .SetProperty(row => row.ClaimedAt, now)
                .SetProperty(row => row.ClaimedByAdapterId, adapterId)
                .SetProperty(row => row.UpdatedAt, now), ct);
        if (changed == 0)
            return null;
        candidate.State = SlackOutboxStates.Claimed;
        candidate.ClaimedAt = now;
        candidate.ClaimedByAdapterId = adapterId;
        candidate.UpdatedAt = now;
        return ToEntry(candidate);
    }

    public async Task<SlackOutboxEntry?> ClaimUncertainAsync(string projectId, string connectionId, string adapterId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var candidate = await db.SlackOutboxRows
            .Where(row => row.ProjectId == projectId
                && row.ConnectionId == connectionId
                && row.State == SlackOutboxStates.DeliveryUncertain
                && db.AgentConnections.Any(connection =>
                    connection.ProjectId == projectId
                    && connection.Id == connectionId
                    && connection.DeletedAt == null
                    && connection.DesiredState == DesiredStateKind.Enabled))
            .OrderBy(row => row.Id)
            .FirstOrDefaultAsync(ct);
        if (candidate is null)
            return null;

        var changed = await db.SlackOutboxRows
            .Where(row => row.Id == candidate.Id && row.State == SlackOutboxStates.DeliveryUncertain)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, SlackOutboxStates.Claimed)
                .SetProperty(row => row.ClaimedAt, now)
                .SetProperty(row => row.ClaimedByAdapterId, adapterId)
                .SetProperty(row => row.UpdatedAt, now), ct);
        if (changed == 0)
            return null;
        candidate.State = SlackOutboxStates.Claimed;
        candidate.ClaimedAt = now;
        candidate.ClaimedByAdapterId = adapterId;
        candidate.UpdatedAt = now;
        return ToEntry(candidate);
    }

    public async Task<int> MarkDeliveredAsync(string projectId, string id, CancellationToken ct = default)
        => await MarkDeliveredAsync(projectId, id, null, ct);

    public async Task<int> MarkDeliveredAsync(
        string projectId,
        string id,
        SlackProviderMessageIdentity? providerIdentity,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id is required.", nameof(id));

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackOutboxRows.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == id, ct)
            ?? throw new SlackOutboxRowNotFoundException(id);
        if (row.State is not (SlackOutboxStates.Claimed or SlackOutboxStates.Pending or SlackOutboxStates.DeliveryUncertain))
            throw new SlackOutboxStateException(id, expectedState: "claimed|pending|delivery_uncertain", actualState: row.State);
        if (row.State == SlackOutboxStates.DeliveryUncertain && providerIdentity is null)
            throw new SlackOutboxStateException(id, expectedState: "reconciled provider identity", actualState: row.State);
        if (row.State == SlackOutboxStates.Pending)
        {
            row.ClaimedAt = now;
            row.ClaimedByAdapterId = "direct";
        }
        row.State = SlackOutboxStates.Delivered;
        row.DeliveryUncertainAt = null;
        if (providerIdentity is not null)
            row.PayloadJson = AttachProviderIdentity(row.PayloadJson, providerIdentity.Value);
        row.DeliveredAt = now;
        row.UpdatedAt = now;
        return await db.SaveChangesAsync(ct);
    }

    private static string AttachProviderIdentity(string payloadJson, SlackProviderMessageIdentity identity)
    {
        var payload = SlackDeliveryPayload.Parse(payloadJson);
        return System.Text.Json.JsonSerializer.Serialize(payload with { ProviderMessageIdentity = identity });
    }

    public async Task<int> MarkDeliveryUncertainAsync(string projectId, string id, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id is required.", nameof(id));

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackOutboxRows.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == id, ct)
            ?? throw new SlackOutboxRowNotFoundException(id);
        if (row.State is not (SlackOutboxStates.Claimed or SlackOutboxStates.Pending or SlackOutboxStates.DeliveryUncertain))
            throw new SlackOutboxStateException(id, expectedState: "claimed|pending|delivery_uncertain", actualState: row.State);
        row.State = SlackOutboxStates.DeliveryUncertain;
        row.DeliveryUncertainAt = now;
        row.LastError = reason;
        row.UpdatedAt = now;
        return await db.SaveChangesAsync(ct);
    }

    public async Task<int> MarkDeadLetteredAsync(string projectId, string id, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id is required.", nameof(id));

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackOutboxRows.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == id, ct)
            ?? throw new SlackOutboxRowNotFoundException(id);
        if (row.State == SlackOutboxStates.DeadLettered)
            return 0;
        row.State = SlackOutboxStates.DeadLettered;
        row.DeadLetteredAt = now;
        row.LastError = reason;
        row.UpdatedAt = now;
        return await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Schedules a retry for a transient failure: increments attempt
    /// count, transitions to Pending with <see cref="SlackOutboxRow.NextAttemptAt"/>
    /// set to <c>now + backoff(attempts)</c>. Returning to Pending
    /// means the merge target for ReplaceableProgress is fresh, which
    /// is correct: the dispatcher sweep, not a retry, decides whether
    /// the next attempt collides with a newer progress.
    /// </summary>
    public async Task<int> ScheduleRetryAsync(string projectId, string id, string? reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id is required.", nameof(id));

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackOutboxRows.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == id, ct)
            ?? throw new SlackOutboxRowNotFoundException(id);
        row.State = SlackOutboxStates.Pending;
        row.AttemptCount++;
        row.NextAttemptAt = now + Backoff(row.AttemptCount);
        row.DeliveryUncertainAt = null;
        row.LastError = reason;
        row.UpdatedAt = now;
        return await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Transitions a DeliveryUncertain row back to Pending for an
    /// operator-initiated resend. The duplicate warning is the client's
    /// responsibility; this method is the single transition. Reusing
    /// <see cref="ScheduleRetryAsync"/>'s effect (Pending state, attempt
    /// budget still applies via <see cref="SlackProviderOptions.OutboxMaxAttempts"/>)
    /// keeps one retry path and one attempt budget so a resent uncertain
    /// row is still dead-lettered if it keeps failing — it cannot live
    /// forever. State is guarded to DeliveryUncertain only; any other
    /// state returns 0 so the route can surface a 409.
    /// </summary>
    public async Task<int> ResendUncertainAsync(string projectId, string connectionId, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id is required.", nameof(id));

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackOutboxRows.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ConnectionId == connectionId && r.Id == id, ct)
            ?? throw new SlackOutboxRowNotFoundException(id);
        if (row.State != SlackOutboxStates.DeliveryUncertain)
            return 0;
        row.State = SlackOutboxStates.Pending;
        row.AttemptCount++;
        row.NextAttemptAt = now;
        row.DeliveryUncertainAt = null;
        row.UpdatedAt = now;
        return await db.SaveChangesAsync(ct);
    }

    public async Task<SlackOutboxList> ListAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SlackOutboxRows.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.ConnectionId == connectionId)
            .OrderBy(row => row.Id)
            .ToListAsync(ct);
        return new SlackOutboxList(rows.Select(ToEntry).ToList());
    }

    public async Task<int> CountPendingAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackOutboxRows
            .Where(row => row.ProjectId == projectId
                && row.ConnectionId == connectionId
                && row.State == SlackOutboxStates.Pending)
            .CountAsync(ct);
    }

    /// <summary>
    /// Cascade-deletes outbox rows for one Connection. Idempotent.
    /// Called from <c>AgentConnectionStore.DeleteAsync</c> alongside
    /// credentials + inbox cleanup so a deleted Connection leaves no
    /// provider state behind.
    /// </summary>
    public async Task<int> DeleteForConnectionAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackOutboxRows
            .Where(row => row.ProjectId == projectId && row.ConnectionId == connectionId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> PrunePendingStatusMutationsAsync(
        string projectId,
        string connectionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SlackOutboxRows
            .Where(row => row.ProjectId == projectId
                && row.ConnectionId == connectionId
                && row.State == SlackOutboxStates.Pending
                && row.Kind == SlackOutboxKinds.ReactionMutation)
            .ToListAsync(ct);
        var stale = rows.Where(row => IsReactionMutation(row.PayloadJson)).ToList();
        if (stale.Count == 0)
            return 0;
        db.SlackOutboxRows.RemoveRange(stale);
        return await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Used by the dispatcher to scan Pending rows whose retry budget
    /// has been exhausted. Bounded by
    /// <see cref="SlackProviderOptions.OutboxMaxAttempts"/> so a stuck
    /// delivery cannot live forever in the table; the dispatcher's
    /// job is to dead-letter them, not to retry indefinitely.
    /// </summary>
    public async Task<IReadOnlyList<SlackOutboxRow>> ListPendingReadyForRetryAsync(int batchSize, CancellationToken ct = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        var maxAttempts = _options.Value.OutboxMaxAttempts;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackOutboxRows
            .Where(row => row.State == SlackOutboxStates.Pending
                && row.AttemptCount >= maxAttempts)
            .OrderBy(row => row.Id)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SlackOutboxRow>> ListClaimedPastTimeoutAsync(int batchSize, CancellationToken ct = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        var cutoff = _timeProvider.GetUtcNow() - _options.Value.OutboxClaimTimeout;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SlackOutboxRows
            .Where(row => row.State == SlackOutboxStates.Claimed)
            .ToListAsync(ct);
        return rows
            .Where(row => row.ClaimedAt is not null && row.ClaimedAt <= cutoff)
            .OrderBy(row => row.Id)
            .Take(batchSize)
            .ToList();
    }

    public async Task<IReadOnlyList<SlackOutboxRow>> ListUncertainPastTimeoutAsync(int batchSize, CancellationToken ct = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        var cutoff = _timeProvider.GetUtcNow() - _options.Value.OutboxUncertainTimeout;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SlackOutboxRows
            .Where(row => row.State == SlackOutboxStates.DeliveryUncertain)
            .ToListAsync(ct);
        return rows
            .Where(row => row.DeliveryUncertainAt is not null && row.DeliveryUncertainAt <= cutoff)
            .OrderBy(row => row.Id)
            .Take(batchSize)
            .ToList();
    }

    public TimeSpan Backoff(int attemptCount)
    {
        if (attemptCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptCount));
        var multiplier = Math.Pow(2, Math.Min(attemptCount - 1, 62));
        var ticks = Math.Min(_options.Value.OutboxBaseBackoff.Ticks * multiplier, _options.Value.OutboxMaxBackoff.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private static void ValidateDraft(SlackOutboxDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.ProjectId))
            throw new ArgumentException("ProjectId is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.ConnectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.WorkspaceTeamId))
            throw new ArgumentException("WorkspaceTeamId is required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.ConversationId))
            throw new ArgumentException("ConversationId is required.", nameof(draft));
        if (!SlackOutboxKinds.IsDefined(draft.Kind))
            throw new ArgumentException($"Kind '{draft.Kind}' is not one of the defined Slack outbox kinds.", nameof(draft));
        if (string.IsNullOrEmpty(draft.PayloadJson))
            throw new ArgumentException("PayloadJson is required.", nameof(draft));
        if (draft.Kind == SlackOutboxKinds.ReplaceableProgress && string.IsNullOrWhiteSpace(draft.DispatchRef))
            throw new ArgumentException("DispatchRef is required for ReplaceableProgress.", nameof(draft));
    }

    private static bool IsReactionMutation(string payloadJson)
    {
        try
        {
            var operation = SlackDeliveryPayload.Parse(payloadJson).Operation;
            return operation is SlackDeliveryOperations.ReactionAdd or SlackDeliveryOperations.ReactionRemove;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static SlackOutboxEntry ToEntry(SlackOutboxRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        ConnectionId = row.ConnectionId,
        WorkspaceTeamId = row.WorkspaceTeamId,
        ConversationId = row.ConversationId,
        ThreadTs = row.ThreadTs,
        Kind = row.Kind,
        State = row.State,
        DispatchRef = row.DispatchRef,
        PayloadJson = row.PayloadJson,
        AttemptCount = row.AttemptCount,
        NextAttemptAt = row.NextAttemptAt,
        ClaimedAt = row.ClaimedAt,
        ClaimedByAdapterId = row.ClaimedByAdapterId,
        DeliveredAt = row.DeliveredAt,
        DeliveryUncertainAt = row.DeliveryUncertainAt,
        DeadLetteredAt = row.DeadLetteredAt,
        LastError = row.LastError,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };
}
