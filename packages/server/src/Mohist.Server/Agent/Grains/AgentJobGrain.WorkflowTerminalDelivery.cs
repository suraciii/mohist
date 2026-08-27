namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private void StageWorkflowTerminalEvent(
        AgentJobStatus status,
        string? message,
        string? output,
        string? failureReason,
        string? failureCategory,
        string[]? artifactUploadIds,
        int? exitCode,
        string? addTasksJson)
    {
        var origin = State.Input?.WorkflowOrigin;
        if (origin is null || State.PendingWorkflowTerminalEvent is not null)
            return;

        State.PendingWorkflowTerminalEvent = new PendingWorkflowAgentTerminalEvent(
            EventId: $"agent-job:{Key}:workflow-terminal",
            Origin: origin,
            Status: status,
            Message: message,
            Output: output,
            ArtifactUploadIds: artifactUploadIds,
            FailureReason: failureReason,
            FailureCategory: failureCategory,
            ExitCode: exitCode,
            ResultFingerprint: State.AcceptedReportFingerprint,
            AgentSessionId: State.Input?.AgentSessionId,
            InitialInputId: State.Input?.InitialInputId,
            InitialTurnId: State.Input?.InitialTurnId,
            RecordedAt: _timeProvider.GetUtcNow(),
            AddTasksJson: addTasksJson);
    }

    private async Task EmitWorkflowTerminalEventAsync(PendingWorkflowAgentTerminalEvent pending)
    {
        try
        {
            var envelope = AgentJobLineage.BuildWorkflowTerminalEnvelope(
                Key,
                pending,
                AgentJobLineage.BuildExtensions(State.Input, State.RoutedPlan));
            await _eventStore.AppendAsync(envelope, CancellationToken.None);
            _dispatchSignal.Wake();
            State.PendingWorkflowTerminalEvent = null;
            await PersistAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AgentJob {Id} Workflow terminal event is retained for retry", Key);
            await EnsureRecoveryReminderAsync();
        }
    }
}
