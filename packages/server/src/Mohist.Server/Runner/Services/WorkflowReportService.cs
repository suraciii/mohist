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
        WorkResult result,
        CancellationToken ct = default)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null)
            return ("missing-workflow", null);

        var workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        var workerId = runnerId;

        var activeWork = run.FindReportableWork(workId, workerId);
        if (activeWork is null)
            return (ReportAck.Stale.ToString().ToLowerInvariant(), null);

        var report = _translator.TranslateResult(activeWork.Item, result, workflowRunId);
        if (report is WorkflowItemTranslator.InboundReport.Unknown unknown && activeWork.IsTask)
        {
            var binding = activeWork.TaskRunId is { } taskRunId
                ? run.FindBoundAgentExecution(taskRunId, workId, workerId)
                : null;
            var unknownAck = binding is not null
                ? await workflow.ObserveAgentExecutionAsync(new AgentExecutionObservation(
                    binding,
                    AgentExecutionObservationKind.Unknown,
                    unknown.ReasonCode,
                    unknown.Message))
                : await workflow.ObserveAgentResultUnknownAsync(
                    workerId,
                    workId,
                    unknown.ReasonCode,
                    unknown.Message);
            if (unknownAck != ReportAck.Stale)
                return (unknownAck.ToString().ToLowerInvariant(), await workflow.GetRunStatusAsync());

            report = new WorkflowItemTranslator.InboundReport.Task(unknown.Fallback);
        }

        ReportAck ack = report switch
        {
            WorkflowItemTranslator.InboundReport.Task t when activeWork.IsTask =>
                await workflow.ReceiveTaskReportAsync(workerId, workId, t.Value),
            WorkflowItemTranslator.InboundReport.Checks c when activeWork.IsChecks =>
                await workflow.ReceiveCheckReportAsync(workerId, workId, c.Value),
            _ => ReportAck.Stale,
        };
        return (ack.ToString().ToLowerInvariant(), await workflow.GetRunStatusAsync());
    }
}
