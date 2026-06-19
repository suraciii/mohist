using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services.Sessions;
using Orleans.Runtime;

namespace Mohist.Server.Workflow.Services;

public sealed class WorkflowSessionHealthService(
    AgentSessionQuery agentSessionQuery,
    IGrainFactory grainFactory,
    ILogger<WorkflowSessionHealthService> log)
{
    public async Task CheckAndEnforceAsync(
        string? taskId,
        string? stage,
        string workflowRunId,
        WorkflowRun run,
        Func<IReadOnlyList<WorkflowEvent>, Task> commitAsync,
        string context = "retry",
        CancellationToken ct = default)
    {
        var usage = await QueryUsageAsync(taskId, stage, workflowRunId, ct);
        var verdict = WorkflowSessionHealthGate.Evaluate(usage.Percent);

        if (verdict == HealthVerdict.Block)
        {
            var events = run.BlockStageWithContextExhaustion(taskId, usage.Percent, usage.SessionId);
            log.LogWarning(
                "Workflow {Id} {Context} blocked: session context at {Percent:0.##}% (task={TaskId}, stage={Stage}, sessionId={SessionId})",
                workflowRunId, context, usage.Percent ?? 0d, taskId ?? "(none)", stage ?? "(none)", usage.SessionId ?? "(unknown)");
            await commitAsync(events);

            throw new WorkflowSessionContextExhaustedException(
                WorkflowSessionHealthGate.BuildBlockingMessage(usage.Percent),
                usage.Percent, stage, taskId);
        }

        if (verdict == HealthVerdict.Warn)
        {
            log.LogWarning(
                "Workflow {Id} {Context} proceeding with elevated session context usage {Percent:0.##}% (task={TaskId}, stage={Stage}, sessionId={SessionId})",
                workflowRunId, context, usage.Percent ?? 0d, taskId ?? "(none)", stage ?? "(none)", usage.SessionId ?? "(unknown)");
        }
    }

    private async Task<(double? Percent, string? SessionId)> QueryUsageAsync(
        string? taskId, string? stage, string workflowRunId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(stage))
            return (null, null);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentSessionQueryMetadataKeys.WorkflowRunId] = workflowRunId,
            [AgentSessionQueryMetadataKeys.WorkId] = taskId,
            [AgentSessionQueryMetadataKeys.Stage] = stage,
        };

        AgentSessionRecord? record;
        try
        {
            record = await agentSessionQuery.FirstByLabelsAsync(labels, ct: ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Workflow {Id} session lookup for task {TaskId} stage {Stage} failed; treating as healthy",
                workflowRunId, taskId, stage);
            return (null, null);
        }

        if (record is null) return (null, null);

        try
        {
            var info = await grainFactory.GetGrain<IAgentSessionGrain>(record.Session.Id).GetAsync();
            if (info is null) return (null, null);
            var percent = AgentSessionJsonHelper.ContextUsagePercent(info.ContextWindowUsed, info.ContextWindowSize);
            return (percent, record.Session.Id);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Workflow {Id} session grain lookup for {SessionId} failed; treating as healthy",
                workflowRunId, record.Session.Id);
            return (null, null);
        }
    }
}
