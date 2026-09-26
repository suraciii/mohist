using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Slack.Domain;

namespace Mohist.Server.Infrastructure.Slack;

public sealed partial class SlackOutboxStore
{
    public async Task<int> MarkDeliveryUncertainAsync(string projectId, string id, string? reason, CancellationToken ct = default)
        => await MarkDeliveryUncertainAsync(projectId, id, reason, adapterId: null, ct: ct);

    public async Task<int> MarkDeliveryUncertainAsync(
        string projectId,
        string id,
        string? reason,
        string? adapterId,
        CancellationToken ct = default,
        string? expectedState = null,
        DateTimeOffset? expectedUpdatedAt = null)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id is required.", nameof(id));

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (expectedState is not null)
        {
            if (expectedState != SlackOutboxStates.Claimed)
                throw new ArgumentException("ExpectedState must be Claimed for a claim-timeout settlement.", nameof(expectedState));
            if (expectedUpdatedAt is null)
                throw new ArgumentException("ExpectedUpdatedAt is required for a claim-timeout settlement.", nameof(expectedUpdatedAt));

            return await db.SlackOutboxRows
                .Where(row => row.ProjectId == projectId
                    && row.Id == id
                    && row.State == expectedState
                    && row.UpdatedAt == expectedUpdatedAt.Value)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.State, SlackOutboxStates.DeliveryUncertain)
                    .SetProperty(row => row.DeliveryUncertainAt, now)
                    .SetProperty(row => row.LastError, reason)
                    .SetProperty(row => row.UpdatedAt, now), ct);
        }

        var row = await db.SlackOutboxRows.FirstOrDefaultAsync(r => r.ProjectId == projectId && r.Id == id, ct)
            ?? throw new SlackOutboxRowNotFoundException(id);
        // A replayed acknowledgement of an outcome that is already unknown is a
        // duplicate, not a settlement: the unknown state, its first observation,
        // and the notice already authored for it all stand.
        if (row.State == SlackOutboxStates.DeliveryUncertain)
            return 0;
        if (row.State is not (SlackOutboxStates.Claimed or SlackOutboxStates.Pending))
            throw new SlackOutboxStateException(id, expectedState: "claimed|pending", actualState: row.State);
        EnsureClaimOwnership(row, adapterId);
        row.State = SlackOutboxStates.DeliveryUncertain;
        row.DeliveryUncertainAt = now;
        row.LastError = reason;
        row.UpdatedAt = now;
        return await db.SaveChangesAsync(ct);
    }

    public async Task<int> MarkDeadLetteredAsync(
        string projectId,
        string id,
        string? reason,
        string expectedState,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Id is required.", nameof(id));
        if (expectedState is not (SlackOutboxStates.Pending or SlackOutboxStates.DeliveryUncertain))
            throw new ArgumentException(
                "ExpectedState must be Pending or DeliveryUncertain for a dead-letter settlement.",
                nameof(expectedState));

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackOutboxRows
            .Where(row => row.ProjectId == projectId
                && row.Id == id
                && row.State == expectedState
                && row.UpdatedAt == expectedUpdatedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.State, SlackOutboxStates.DeadLettered)
                .SetProperty(row => row.DeadLetteredAt, now)
                .SetProperty(row => row.LastError, reason)
                .SetProperty(row => row.UpdatedAt, now), ct);
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

    /// <summary>
    /// Content rows that settled without a confirmed outcome but whose notice
    /// intent is missing. The settlement and the notice authoring are separate
    /// writes, so this scan is what makes the notice obligation recoverable:
    /// an interruption between them — or a single authoring failure — is
    /// repaired by a later sweep, and the notice enqueue is idempotent on the
    /// notice dispatch key, so a repair never posts a second notice.
    /// Bounded like the other sweeps: at most <paramref name="batchSize"/>
    /// rows per dispatch, and <paramref name="afterRowId"/> lets the caller
    /// resume after the rows it already examined so a candidate that cannot be
    /// authored never occupies the first slots of every tick. Rows whose owner
    /// is no longer live are excluded here — their obligation stays recorded
    /// on the row, but they can neither receive a notice nor block the
    /// owners that can.
    /// </summary>
    public async Task<IReadOnlyList<SlackOutboxRow>> ListSettledContentRowsMissingNoticeAsync(
        int batchSize,
        string? afterRowId = null,
        CancellationToken ct = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        const string noticePrefix = SlackDeliveryNoticeAuthor.DispatchPrefix;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.SlackOutboxRows.AsNoTracking()
            .Where(row => (row.Kind == SlackOutboxKinds.TerminalResult
                    || row.Kind == SlackOutboxKinds.ReplaceableProgress)
                && (row.State == SlackOutboxStates.DeliveryUncertain
                    || row.State == SlackOutboxStates.DeadLettered)
                && ((row.OwnerKind == SlackDeliveryOwnerKinds.Manager
                        && db.SlackWorkspaceEnrollments.Any(enrollment =>
                            enrollment.Id == row.ConnectionId
                            && enrollment.Lifecycle == SlackEnrollmentLifecycle.Active
                            && enrollment.DeletedAt == null))
                    || (row.OwnerKind == SlackDeliveryOwnerKinds.Connection
                        && db.AgentConnections.Any(connection =>
                            connection.ProjectId == row.ProjectId
                            && connection.Id == row.ConnectionId
                            && connection.DeletedAt == null)))
                && !db.SlackOutboxRows.Any(notice =>
                    notice.OwnerKind == row.OwnerKind
                    && notice.ConnectionId == row.ConnectionId
                    && notice.DispatchRef == noticePrefix + row.Id));
        if (afterRowId is not null)
            query = query.Where(row => string.Compare(row.Id, afterRowId) > 0);
        return await query
            .OrderBy(row => row.Id)
            .Take(batchSize)
            .ToListAsync(ct);
    }
}
