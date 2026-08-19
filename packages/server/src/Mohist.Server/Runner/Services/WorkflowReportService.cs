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
        CancellationToken ct = default,
        string? agentSessionId = null,
        string? agentTurnId = null,
        string? runtime = null,
        string? runtimeSessionId = null)
    {
        var run = await _workflowRuns.LoadAsync(workflowRunId, ct);
        if (run is null)
            return ("missing-workflow", null);

        var item = run.FindReportShape(taskRunId, workId);
        if (item is null)
            return (ReportAck.Stale.ToString().ToLowerInvariant(), null);

        var isAgentTask = item.IsTask && IsAgentTask(item.Uses);
        var agentBinding = isAgentTask
            ? TryCreateAgentBinding(
                taskRunId,
                workId,
                runnerId,
                agentSessionId,
                agentTurnId,
                runtime,
                runtimeSessionId)
            : null;
        if (isAgentTask && agentBinding is null)
        {
            // Workflow Agent results are never accepted from the reusable
            // task/work/Runner tuple. The runtime identity must come from the
            // same executed turn, otherwise an incomplete or stale receipt is
            // acknowledged without side effects.
            return (ReportAck.Stale.ToString().ToLowerInvariant(), null);
        }

        var workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        var report = _translator.TranslateResult(item, result, workflowRunId);
        if (report is WorkflowItemTranslator.InboundReport.Unknown unknown && item.IsTask)
        {
            // Unknown is an observation, never an inferred task failure. Agent
            // observations use the same complete execution binding as terminal
            // reports; non-Agent work retains the existing tuple path.
            var unknownAck = isAgentTask
                ? await workflow.ObserveAgentExecutionAsync(new AgentExecutionObservation(
                    agentBinding!,
                    AgentExecutionObservationKind.Unknown,
                    unknown.ReasonCode,
                    unknown.Message))
                : await workflow.ObserveAgentResultUnknownAsync(
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
                    task.Value with { TaskRunId = taskRunId },
                    agentBinding),
            WorkflowItemTranslator.InboundReport.Checks checks when item.IsChecks =>
                await workflow.ReceiveCheckReportAsync(runnerId, workId, checks.Value),
            _ => ReportAck.Stale,
        };
        return (ack.ToString().ToLowerInvariant(), await workflow.GetRunStatusAsync());
    }

    private static bool IsAgentTask(string? uses) =>
        string.Equals(uses, "mohist/agent", StringComparison.Ordinal)
        || string.Equals(uses, "mohist/opencode", StringComparison.Ordinal)
        || string.Equals(uses, "mohist/pi", StringComparison.Ordinal);

    private static AgentExecutionBinding? TryCreateAgentBinding(
        string? taskRunId,
        string workId,
        string runnerId,
        string? agentSessionId,
        string? agentTurnId,
        string? runtime,
        string? runtimeSessionId) =>
        string.IsNullOrWhiteSpace(taskRunId)
        || string.IsNullOrWhiteSpace(workId)
        || string.IsNullOrWhiteSpace(runnerId)
        || string.IsNullOrWhiteSpace(agentSessionId)
        || string.IsNullOrWhiteSpace(agentTurnId)
        || string.IsNullOrWhiteSpace(runtime)
        || string.IsNullOrWhiteSpace(runtimeSessionId)
            ? null
            : new AgentExecutionBinding(
                taskRunId,
                workId,
                runnerId,
                agentSessionId,
                agentTurnId,
                runtime,
                runtimeSessionId);
}
