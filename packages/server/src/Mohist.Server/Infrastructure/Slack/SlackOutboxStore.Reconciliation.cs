using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Re-send and reconciliation entry points of the Slack outbox: a delivery
/// without a confirmed outcome is never queued again blindly, and a
/// dead-lettered one returns to the reconciliation path that requires provider
/// evidence before any provider call.
/// </summary>
public sealed partial class SlackOutboxStore
{
    private static void EnsureClaimOwnership(SlackOutboxRow row, string? adapterId)
    {
        if (!string.IsNullOrWhiteSpace(adapterId)
            && (row.State != SlackOutboxStates.Claimed
                || !string.Equals(row.ClaimedByAdapterId, adapterId, StringComparison.Ordinal)))
        {
            throw new SlackOutboxStateException(
                row.Id,
                expectedState: $"claimed by adapter '{adapterId}'",
                actualState: row.State);
        }
    }

    /// <summary>
    /// Operator-initiated re-send request for a delivery without a confirmed
    /// outcome. It never queues a mutation directly: a DeliveryUncertain row
    /// stays uncertain and is left to the adapter's reconciliation path, which
    /// re-posts the original payload only when provider history proves the
    /// mutation absent, and a DeadLettered row returns to DeliveryUncertain
    /// with a fresh retention window so it passes that same reconciliation
    /// before any provider call. The row keeps its original Conversation,
    /// thread, dispatch reference, and payload, so the request can neither
    /// redirect the result nor leak the content to another target. Returns
    /// null when the row is in no state that can be reconciled, or when the
    /// owner is no longer live and Enabled, so the route surfaces a 409.
    /// </summary>
    public async Task<SlackDeliveryReconciliationRequest?> RequestDeliveryReconciliationAsync(
        string projectId,
        string connectionId,
        string id,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id is required.", nameof(id));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackOutboxRows
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.ConnectionId == connectionId && r.Id == id, ct)
            ?? throw new SlackOutboxRowNotFoundException(id);
        if (row.State == SlackOutboxStates.DeliveryUncertain)
            return new SlackDeliveryReconciliationRequest(row.Id, row.State, Revived: false);
        if (row.State != SlackOutboxStates.DeadLettered)
            return null;
        if (!await IsReconciliationOwnerLiveAsync(db, row, ct))
            return null;

        var revived = await ReviveDeadLetteredForReconciliationAsync(db, row.Id, row.UpdatedAt, ct);
        return revived
            ? new SlackDeliveryReconciliationRequest(row.Id, SlackOutboxStates.DeliveryUncertain, Revived: true)
            : null;
    }

    /// <summary>
    /// Returns a dead-lettered content row to the reconciliation path. The row
    /// keeps its original payload, Conversation, thread, and dispatch
    /// reference; a fresh retention window bounds how long it may wait, and
    /// the adapter re-posts the original content only when provider history
    /// proves the mutation absent.
    /// </summary>
    private async Task<bool> ReviveDeadLetteredForReconciliationAsync(
        MohistDbContext db,
        string rowId,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var changed = await db.SlackOutboxRows
            .Where(candidate => candidate.Id == rowId
                && candidate.State == SlackOutboxStates.DeadLettered
                && candidate.UpdatedAt == expectedUpdatedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.State, SlackOutboxStates.DeliveryUncertain)
                .SetProperty(candidate => candidate.DeliveryUncertainAt, now)
                .SetProperty(candidate => candidate.DeadLetteredAt, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LastError, "re-send requested; awaiting provider reconciliation")
                .SetProperty(candidate => candidate.UpdatedAt, now), ct);
        return changed == 1;
    }

    /// <summary>
    /// Re-checks the current authorization before a re-send: the Connection
    /// must still exist, be Enabled, and not be deleted — a revoked or
    /// disabled binding never revives a delivery.
    /// </summary>
    private static async Task<bool> IsReconciliationOwnerLiveAsync(
        MohistDbContext db,
        SlackOutboxRow row,
        CancellationToken ct) =>
        row.OwnerKind == SlackDeliveryOwnerKinds.Manager
            ? await db.SlackWorkspaceEnrollments.AnyAsync(enrollment =>
                enrollment.Id == row.ConnectionId
                && enrollment.Lifecycle == SlackEnrollmentLifecycle.Active
                && enrollment.DeletedAt == null, ct)
            : await db.AgentConnections.AnyAsync(connection =>
                connection.ProjectId == row.ProjectId
                && connection.Id == row.ConnectionId
                && connection.DeletedAt == null
                && connection.DesiredState == DesiredStateKind.Enabled, ct);

    public async Task<SlackOutboxRow?> FindRowAsync(
        string projectId,
        string ownerKind,
        string connectionId,
        string id,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(ownerKind))
            throw new ArgumentException("OwnerKind is required.", nameof(ownerKind));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id is required.", nameof(id));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackOutboxRows.AsNoTracking().FirstOrDefaultAsync(row =>
            row.ProjectId == projectId
            && row.OwnerKind == ownerKind
            && row.ConnectionId == connectionId
            && row.Id == id, ct);
    }
}
