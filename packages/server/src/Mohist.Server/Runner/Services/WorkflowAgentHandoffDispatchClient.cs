using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans;

namespace Mohist.Server.Runner.Services;

/// <summary>
/// Drives the durable Workflow Agent handoff fence. Keeping the three grain
/// calls behind this port lets the translator render only persisted task
/// input while the handoff grain remains the owner of admission and replay.
/// </summary>
public interface IWorkflowAgentHandoffDispatchClient
{
    Task<WorkflowAgentHandoffDispatchResult> DispatchAsync(WorkflowAgentHandoffCommand command);
}

public sealed record WorkflowAgentHandoffDispatchResult(
    WorkflowAgentInvocation? Invocation,
    WorkflowAgentHandoffRejection? Rejection);

public sealed class WorkflowAgentHandoffDispatchClient(
    IGrainFactory grains) : IWorkflowAgentHandoffDispatchClient, Infrastructure.Hosting.IScopedService
{
    public async Task<WorkflowAgentHandoffDispatchResult> DispatchAsync(
        WorkflowAgentHandoffCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var handoff = grains.GetGrain<IWorkflowAgentHandoffGrain>(
            WorkflowAgentHandoffCodec.KeyFor(
                command.ProjectId,
                command.WorkflowRunId,
                command.TaskRunId,
                command.CommandId));
        var prepared = await handoff.PrepareAsync(command);
        if (prepared.Disposition == WorkflowAgentHandoffDisposition.Rejected)
            return new(null, prepared.Rejection);

        var fingerprint = WorkflowAgentHandoffCodec.Fingerprint(command);
        var accepted = await handoff.AcceptAsync(
            new WorkflowAgentHandoffAcceptance(command.CommandId, fingerprint));
        if (accepted.Disposition == WorkflowAgentHandoffDisposition.Rejected)
            return new(null, accepted.Rejection);

        var invocation = accepted.Invocation ?? prepared.Invocation
            ?? throw new InvalidOperationException(
                $"Workflow Agent handoff for task '{command.CommandId}' has no invocation after acceptance.");
        // Persist ownership before activation. AgentJob admission reads the
        // runner runtime view while it chooses a slot; the linkage must be
        // present first so the still-running Workflow task is suppressed and
        // the AgentJob is admitted as the single active owner.
        var link = new AgentInvocationLink(
            InvocationId: invocation.InvocationId,
            TaskRunId: invocation.TaskRunId,
            WorkId: invocation.CommandId,
            JobId: invocation.JobKey,
            SessionId: invocation.SessionId,
            InputId: invocation.InputId,
            TurnId: invocation.TurnId);
        await grains.GetGrain<IWorkflowGrain>(command.WorkflowRunId)
            .BindAgentInvocationAsync(link);

        var activated = await handoff.ActivateAsync();
        return new(activated.Invocation ?? accepted.Invocation ?? prepared.Invocation, null);
    }
}
