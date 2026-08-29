using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Slack;

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
        if (row.State is not (SlackOutboxStates.Claimed or SlackOutboxStates.Pending or SlackOutboxStates.DeliveryUncertain))
            throw new SlackOutboxStateException(id, expectedState: "claimed|pending|delivery_uncertain", actualState: row.State);
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
}
