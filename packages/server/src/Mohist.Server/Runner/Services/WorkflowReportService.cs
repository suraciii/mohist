using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans;

namespace Mohist.Server.Runner.Services;

public sealed class WorkflowReportService : IScopedService
{
    private readonly IGrainFactory _grains;
    private readonly WorkflowRunQuerier _workflowRuns;
    private readonly WorkflowItemTranslator _translator;

    public WorkflowReportService(
        IGrainFactory grains,
        WorkflowRunQuerier workflowRuns,
        WorkflowItemTranslator translator)
    {
        _grains = grains;
        _workflowRuns = workflowRuns;
        _translator = translator;
    }

    public async Task<(string Ack, string? WorkflowStatus)> ReportAsync(
        string runnerId,
        string workflowRunId,
        string workId,
        string? taskRunId,
        WorkResult result,
        CancellationToken ct = default)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null)
            return ("missing-workflow", null);

        var item = run.FindReportShape(taskRunId, workId);
        if (item is null)
            return (ReportAck.Stale.ToString().ToLowerInvariant(), null);

        var workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        // Workflow task settlement is v1 fail-closed. A plain WorkResult can
        // still be consumed by AgentJob, but it can never enter the legacy
        // Workflow task settlement path.
        if (item.IsTask && result.CompletionBoundary is null)
            return (ReportAck.Stale.ToString().ToLowerInvariant(), await workflow.GetRunStatusAsync());

        var report = _translator.TranslateResult(item, result, workflowRunId);
        if (report is WorkflowItemTranslator.InboundReport.Unknown unknown && item.IsTask)
        {
            var unknownAck = await workflow.ObserveAgentResultUnknownAsync(
                runnerId,
                taskRunId ?? string.Empty,
                workId,
                unknown.ReasonCode,
                unknown.Message);
            if (unknownAck != ReportAck.Stale)
                return (unknownAck.ToString().ToLowerInvariant(), await workflow.GetRunStatusAsync());

            report = new WorkflowItemTranslator.InboundReport.Task(unknown.Fallback);
        }

        ReportAck ack = report switch
        {
            WorkflowItemTranslator.InboundReport.Task task when item.IsTask && taskRunId is not null =>
                await workflow.ReceiveTaskReportAsync(
                    runnerId,
                    workId,
                    task.Value with { TaskRunId = taskRunId }),
            WorkflowItemTranslator.InboundReport.Checks checks when item.IsChecks =>
                await workflow.ReceiveCheckReportAsync(runnerId, workId, checks.Value),
            _ => ReportAck.Stale,
        };
        return (ack.ToString().ToLowerInvariant(), await workflow.GetRunStatusAsync());
    }
}
