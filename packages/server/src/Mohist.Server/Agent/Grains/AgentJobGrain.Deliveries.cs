using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private void StageTerminalDeliveryEvent(
        AgentJobStatus status,
        string? message,
        string? output,
        string? failureReason,
        string? failureCategory,
        string[]? artifactUploadIds,
        int? exitCode)
    {
        var origin = State.ManualPlan?.ConnectionOrigin;
        if (origin is null || State.PendingTerminalDeliveryEvent is not null)
            return;

        State.PendingTerminalDeliveryEvent = new PendingTerminalDeliveryEvent(
            AgentJobSessionDeliveryIds.TerminalDeliveryEventId(Key),
            origin,
            status,
            message,
            failureReason,
            failureCategory,
            artifactUploadIds?.Length ?? 0,
            exitCode,
            _timeProvider.GetUtcNow(),
            output);
    }

    private async Task EmitTerminalDeliveryEventAsync(PendingTerminalDeliveryEvent pending)
    {
        try
        {
            var envelope = BuildTerminalDeliveryEnvelope(pending);
            await _eventStore.AppendAsync(envelope, CancellationToken.None);
            EventDispatcherPoke.PokeAfterCommit(GrainFactory, _log, nameof(AgentJobGrain), _backgroundTasks);
            State.PendingTerminalDeliveryEvent = null;
            await PersistAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AgentJob {Id} terminal delivery event is retained for retry", Key);
        }
    }

    private void StageSubagentTerminalEvent(AgentJobStatus status)
    {
        if (status is not (AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled)
            || State.Input?.SpawnOrigin is null
            || State.LaunchVisibility != AgentLaunchVisibility.Visible
            || State.PendingSubagentTerminalEvent is not null)
            return;

        // Only an accepted (visible) delegation owes a terminal callback;
        // a provisional or rejected launch was never attached to a parent
        // SessionParentLink, so a cancelled job here must stay silent.
        State.PendingSubagentTerminalEvent = new PendingSubagentTerminalEvent(
            AgentJobSessionDeliveryIds.SubagentTerminalEventId(Key),
            State.Input.SpawnOrigin,
            status,
            $"agent-job:{Key}",
            _timeProvider.GetUtcNow());
    }

    private async Task EmitSubagentTerminalEventAsync(PendingSubagentTerminalEvent pending)
    {
        try
        {
            await _eventStore.AppendAsync(BuildSubagentTerminalEnvelope(pending), CancellationToken.None);
            EventDispatcherPoke.PokeAfterCommit(GrainFactory, _log, nameof(AgentJobGrain), _backgroundTasks);
            State.PendingSubagentTerminalEvent = null;
            await PersistAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AgentJob {Id} subagent terminal event is retained for retry", Key);
        }
    }

    /// <summary>
    /// Stages the workflow-terminal delivery obligation. Only a job whose
    /// input carries the <see cref="AgentJobWorkflowInvocation"/>
    /// discriminator (a Workflow handoff) owes the Workflow side a typed
    /// terminal; direct and routed launches stage nothing. The obligation
    /// freezes the full typed payload — invocation identity, terminal
    /// facts, the boundary completion evaluation parsed from the
    /// runner-reported output, and the recorded timestamp — so emission and
    /// reminder retries never re-read mutable state.
    /// </summary>
    private void StageWorkflowTerminalDelivery(
        AgentJobStatus status,
        string? message,
        string? output,
        string? failureReason,
        string? failureCategory,
        string[]? artifactUploadIds,
        int? exitCode)
    {
        var invocation = State.Input?.WorkflowInvocation;
        if (invocation is null
            || State.PendingWorkflowTerminalDelivery is not null
            || status is not (AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled))
        {
            return;
        }

        var input = State.Input!;
        State.PendingWorkflowTerminalDelivery = new PendingWorkflowTerminalDelivery(
            EventId: AgentJobSessionDeliveryIds.WorkflowTerminalEventId(Key),
            InvocationId: invocation.InvocationId,
            ProjectId: input.ProjectId,
            WorkflowRunId: input.WorkflowRunId ?? string.Empty,
            TaskRunId: invocation.TaskRunId,
            WorkId: invocation.WorkId,
            JobId: Key,
            SessionId: input.AgentSessionId,
            InputId: input.InitialInputId,
            TurnId: input.InitialTurnId,
            Status: status,
            Message: message,
            Output: output,
            FailureReason: failureReason,
            FailureCategory: failureCategory,
            ExitCode: exitCode,
            ArtifactUploadIds: artifactUploadIds,
            Evaluation: AgentJobCompletionEvaluationCodec.Parse(output),
            RecordedAt: _timeProvider.GetUtcNow());
    }

    private async Task EmitWorkflowTerminalDeliveryAsync(PendingWorkflowTerminalDelivery pending)
    {
        try
        {
            var envelope = BuildWorkflowTerminalEnvelope(pending);
            await _eventStore.AppendAsync(envelope, CancellationToken.None);
            EventDispatcherPoke.PokeAfterCommit(GrainFactory, _log, nameof(AgentJobGrain), _backgroundTasks);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} failed to append {Type} event (eventId={EventId}); reminder will retry",
                Key, EventCatalog.ReverseDns.AgentJobWorkflowTerminal, pending.EventId);
            await EnsureRecoveryReminderAsync();
            return;
        }
        State.PendingWorkflowTerminalDelivery = null;
        await PersistAsync();
        _log.LogInformation(
            "AgentJob {Id} emitted {Type} event (eventId={EventId}, status={Status}, invocation={Invocation})",
            Key,
            EventCatalog.ReverseDns.AgentJobWorkflowTerminal,
            pending.EventId,
            pending.Status,
            pending.InvocationId);
    }

    internal CloudEvent BuildWorkflowTerminalEnvelope(PendingWorkflowTerminalDelivery pending)
    {
        var extensions = AgentJobLineage.BuildExtensions(State.Input, State.RoutedPlan);
        var projectId = extensions.TryGetValue(EventCatalog.Lineage.ProjectId, out var pid) ? pid : null;
        var issue = extensions.TryGetValue(EventCatalog.Lineage.Issue, out var iss) ? iss : null;
        var epic = extensions.TryGetValue(EventCatalog.Lineage.Epic, out var epi) ? epi : null;
        var workflowRunId = extensions.TryGetValue(EventCatalog.Lineage.WorkflowRunId, out var wri) ? wri : null;
        var agentId = extensions.TryGetValue(EventCatalog.Lineage.AgentId, out var aid) ? aid : null;
        ProducerConformance.Assert(EventProducerFamily.AgentJob, extensions, new ProducerLineageContext(
            ProjectId: projectId,
            Issue: issue,
            Epic: epic,
            WorkflowRunId: workflowRunId,
            AgentId: agentId));
        return AgentJobLineage.BuildWorkflowTerminalEnvelope(Key, pending, extensions);
    }

    internal CloudEvent BuildSubagentTerminalEnvelope(PendingSubagentTerminalEvent pending) =>
        AgentJobLineage.BuildSubagentTerminalEnvelope(Key, pending);

    internal CloudEvent BuildTerminalDeliveryEnvelope(PendingTerminalDeliveryEvent obligation)
    {
        var extensions = AgentJobLineage.BuildExtensions(State.Input, State.RoutedPlan);
        var sessionLaunchPrompt = State.Input?.Prompt
            ?? State.ManualPlan?.Prompt
            ?? State.RoutedPlan?.Prompt;
        return AgentJobLineage.BuildTerminalDeliveryEnvelope(
            Key,
            obligation,
            extensions,
            sessionLaunchPrompt);
    }
}
