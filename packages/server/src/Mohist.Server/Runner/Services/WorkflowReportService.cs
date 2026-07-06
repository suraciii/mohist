using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Stateless translation + direct-to-owner routing for a workflow work report.
/// Replaces the old RunnerGrain-relayed report path: the runner reports a
/// workId + result, this service reconstructs the work item from the persisted
/// run, translates the result via <see cref="WorkflowItemTranslator"/>, and
/// reports direct to the owning <see cref="IWorkflowGrain"/>. The owning grain
/// is the idempotent arbiter (Accepted | Stale); both are acks.
/// (design/workflow/scheduling.md §Report.) Lives in the Services layer so the
/// API layer does not depend on the Data layer directly.
/// </summary>
public sealed class WorkflowReportService : IScopedService
{
    private readonly IGrainFactory _grains;
    private readonly WorkflowRunQuerier _workflowRuns;
    private readonly WorkflowItemTranslator _translator;
    private readonly ILogger<WorkflowReportService> _log;

    public WorkflowReportService(
        IGrainFactory grains,
        WorkflowRunQuerier workflowRuns,
        WorkflowItemTranslator translator,
        ILogger<WorkflowReportService> log)
    {
        _grains = grains;
        _workflowRuns = workflowRuns;
        _translator = translator;
        _log = log;
    }

    /// <summary>
    /// Reports a workflow work result direct to the owning grain. Returns the
    /// ack (<c>"accepted"</c> / <c>"stale"</c>), or <c>"missing-workflow"</c>
    /// when the run cannot be loaded.
    /// </summary>
    public async Task<(string Ack, string? WorkflowStatus)> ReportAsync(
        string runnerId,
        string workflowRunId,
        string workId,
        WorkResult result,
        CancellationToken ct = default)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null)
            return ("missing-workflow", null);

        var stage = run.CurrentStage();
        var workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId);

        var task = stage.FindRunningTaskByWork(workId, runnerId);
        if (task is not null)
        {
            var item = WorkItem.Task(
                stage.Id, task.WorkId ?? task.Id, task.Title, task.Uses,
                task.WithInput, task.Artifacts, task.SetVars, task.Recovery);
            var outcome = await _translator.TranslateResultAsync(item, result, workflowRunId, run);
            ReportAck ack = outcome switch
            {
                WorkflowItemTranslator.InboundOutcome.Task t => await workflow.ReportTaskOutcomeAsync(runnerId, workId, t.Value),
                _ => ReportAck.Stale,
            };
            return (ack.ToString().ToLowerInvariant(), await workflow.GetRunStatusAsync());
        }

        if (string.Equals(stage.ChecksWorkId, workId, StringComparison.Ordinal))
        {
            var pendingChecks = stage.Checks
                .Where(c => c.Status is StageCheckStatus.Pending or StageCheckStatus.Running)
                .Select(c => new CheckItem(c.Name, c.Title, c.Uses, c.WithInput))
                .ToList();
            var item = WorkItem.Checks(stage.Id, workId, pendingChecks);
            var outcome = await _translator.TranslateResultAsync(item, result, workflowRunId, run);
            ReportAck ack = outcome switch
            {
                WorkflowItemTranslator.InboundOutcome.Checks c => await workflow.ReportCheckOutcomeAsync(runnerId, workId, c.Value),
                _ => ReportAck.Stale,
            };
            return (ack.ToString().ToLowerInvariant(), await workflow.GetRunStatusAsync());
        }

        return (ReportAck.Stale.ToString().ToLowerInvariant(), null);
    }
}
