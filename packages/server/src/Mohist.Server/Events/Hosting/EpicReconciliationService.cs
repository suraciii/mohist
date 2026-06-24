using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Orleans;

namespace Mohist.Server.Events.Hosting;

/// <summary>
/// Safety-net sweep for the event-driven auto-done path. The in-memory
/// CloudEvent bus used to signal <c>com.mohist.issue.work-completed</c>
/// is at-most-once and swallows publish failures, so a missed event
/// would leave a ready epic in <c>active</c>. This service periodically
/// walks <c>active</c> epics and re-invokes
/// <see cref="IEpicGrain.AutoMarkDoneIfReadyAsync"/>, which is
/// idempotent and short-circuits terminal/paused epics. The cadence
/// mirrors <c>IssueWorkflowReconciliationService</c>: runs once a day
/// by default, tunable for tests via <see cref="ReconciliationPeriod"/>.
///
/// Lives outside the <c>Epic</c> feature slice to honor the
/// "feature directories only contain Domain/Grains/Services"
/// convention; only depends on <c>IEpicGrain</c> via the
/// Orleans <see cref="IGrainFactory"/> abstraction, so it cannot form
/// a slice-internal cycle (mirrors <c>EpicAutoDoneHandler</c>'s
/// top-level placement under <c>Events/Subscriptions</c>).
/// </summary>
public sealed class EpicReconciliationService : BackgroundService
{
    public static TimeSpan ReconciliationPeriod = TimeSpan.FromDays(1);

    private const int CandidateBatchSize = 500;

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IGrainFactory _grains;
    private readonly ILogger<EpicReconciliationService> _log;

    public EpicReconciliationService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IGrainFactory grains,
        ILogger<EpicReconciliationService> log)
    {
        _dbFactory = dbFactory;
        _grains = grains;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(ReconciliationPeriod, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileReadyEpicsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "EpicReconciliationService sweep failed");
            }

            try
            {
                await Task.Delay(ReconciliationPeriod, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Test seam — invokes the same candidate-walk the hosted loop runs
    /// without waiting for the timer. Safe to call repeatedly: each
    /// per-epic call goes through
    /// <see cref="IEpicGrain.AutoMarkDoneIfReadyAsync"/>, which is
    /// idempotent.
    /// </summary>
    public async Task ReconcileOnceAsync(CancellationToken ct = default) =>
        await ReconcileReadyEpicsAsync(ct);

    private async Task ReconcileReadyEpicsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // (ProjectId, Status, CreatedAt) index on EpicRow supports this
        // filter efficiently. We only consider active epics; terminal
        // and paused epics are intentionally skipped because the grain's
        // AutoMarkDoneIfReadyAsync would no-op them anyway, and we want
        // the sweep's candidate set to be a small, focused working set
        // (active epics that *might* be stuck-pending-done because a
        // work-completed event was lost).
        var candidates = await db.Epics.AsNoTracking()
            .Where(e => e.Status == "active")
            .Select(e => new { e.ProjectId, e.Id })
            .Take(CandidateBatchSize)
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        _log.LogInformation("Reconciling {Count} active epics", candidates.Count);
        foreach (var row in candidates)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var grain = _grains.GetGrain<IEpicGrain>($"{row.ProjectId}:{row.Id}");
                await grain.AutoMarkDoneIfReadyAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Failed to reconcile epic {EpicId} in project {ProjectId}",
                    row.Id, row.ProjectId);
            }
        }
    }
}
