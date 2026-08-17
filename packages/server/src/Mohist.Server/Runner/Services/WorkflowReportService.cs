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
        var report = _translator.TranslateResult(item, result, workflowRunId);
        if (report is WorkflowItemTranslator.InboundReport.Unknown unknown && item.IsTask)
        {
            // Issue-628 T-005: a non-authoritative <c>unknown</c> result
            // is an observation only. When the workflow grain's Agent
            // settlement rejects the observation as <c>Stale</c> — which
            // includes a matching attempt that has already been durably
            // <c>Blocked</c> — the report adapter must acknowledge stale
            // and MUST NOT forward the translator's <c>TaskReportStatus.Failed</c>
            // fallback to <c>ReceiveTaskReportAsync</c>. Doing so would
            // re-author a <c>TaskFailed</c> event for an attempt that
            // the durable settlement has already classified as
            // <c>Blocked</c>, mutating the blocked state and
            // re-introducing the run into Runner activeWorks, capacity,
            // and missing-redelivery reconciliation.
            var unknownAck = await workflow.ObserveAgentResultUnknownAsync(
                runnerId,
                taskRunId ?? string.Empty,
                workId,
                unknown.ReasonCode,
                unknown.Message);
            if (unknownAck == ReportAck.Stale)
                return (ReportAck.Stale.ToString().ToLowerInvariant(), await workflow.GetRunStatusAsync());

            return (unknownAck.ToString().ToLowerInvariant(), await workflow.GetRunStatusAsync());
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
