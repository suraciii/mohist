using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;

namespace Mohist.Server.Issue.Services;

/// <summary>
/// Sweeps issues that are stuck in InProgress (with a non-null
/// ActiveWorkflowRunId) and reconciles them by triggering
/// <c>GetWorkflowStatusAsync</c>, which performs the lazy reconciliation
/// against the workflow's persisted state. Companion to the lazy path
/// in <see cref="IssueGrain.GetWorkflowStatusAsync"/>; covers the long
/// tail of issues nobody opens.
///
/// Runs once a day by default; the period is tunable for tests via the
/// <see cref="ReconciliationPeriod"/> static field.
/// </summary>
public sealed class IssueWorkflowReconciliationService : BackgroundService
{
    public static TimeSpan ReconciliationPeriod = TimeSpan.FromDays(1);

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IGrainFactory _grains;
    private readonly ILogger<IssueWorkflowReconciliationService> _log;

    public IssueWorkflowReconciliationService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IGrainFactory grains,
        ILogger<IssueWorkflowReconciliationService> log)
    {
        _dbFactory = dbFactory;
        _grains = grains;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Don't run on host startup — wait one period first so the rest of
        // the host has time to settle. Subsequent runs happen on the period.
        try
        {
            await Task.Delay(ReconciliationPeriod, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileStuckIssuesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "IssueWorkflowReconciliationService sweep failed");
            }

            try
            {
                await Task.Delay(ReconciliationPeriod, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReconcileStuckIssuesAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Find issues still in InProgress with a workflow pointer set. We
        // deliberately skip issues that are not InProgress — they have
        // already been reconciled or were never in a workflow.
        // IssueRow.State is a JSON blob; we can't easily filter by status in
        // SQL. Pull the candidates (issues with a workflow pointer set)
        // and let the per-issue grain do the InProgress guard during
        // GetWorkflowStatusAsync → ReconcileWithWorkflowTerminalStateAsync.
        // For 500-row batches this is fast; the daily cadence keeps the
        // working set bounded.
        var stuck = await db.Issues.AsNoTracking()
            .Where(i => i.WorkflowRunId != null)
            .Select(i => new { i.IssueId, i.ProjectId, i.WorkflowRunId })
            .Take(500)
            .ToListAsync(ct);

        if (stuck.Count == 0) return;

        _log.LogInformation("Reconciling {Count} stuck issues", stuck.Count);
        foreach (var row in stuck)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(row.WorkflowRunId)) continue;
            try
            {
                var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(row.IssueId));
                await grain.GetWorkflowStatusAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to reconcile issue {IssueId}", row.IssueId);
            }
        }
    }
}
