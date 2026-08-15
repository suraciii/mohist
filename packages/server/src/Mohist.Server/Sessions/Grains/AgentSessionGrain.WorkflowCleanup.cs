using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    public async Task<WorkflowAgentSessionCleanupReceipt> AcceptWorkflowCleanupAsync(
        AcceptWorkflowAgentSessionCleanupCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.CleanupOperationId)
            || string.IsNullOrWhiteSpace(command.Prompt)
            || string.IsNullOrWhiteSpace(command.WorkflowRunId)
            || string.IsNullOrWhiteSpace(command.TaskRunId)
            || string.IsNullOrWhiteSpace(command.WorkId)
            || string.IsNullOrWhiteSpace(command.RunnerId)
            || string.IsNullOrWhiteSpace(command.AgentSessionId)
            || string.IsNullOrWhiteSpace(command.Runtime)
            || string.IsNullOrWhiteSpace(command.RuntimeSessionId))
        {
            throw new ArgumentException("Workflow cleanup requires a complete execution identity.", nameof(command));
        }

        var session = await GetRequiredAsync();
        ValidateWorkflowCleanupTarget(session, command);
        var inputId = CleanupInputId(command.CleanupOperationId);
        var turnId = CleanupTurnId(command.CleanupOperationId);
        var originalBinding = ResolveWorkflowCleanupBinding(session, command);
        var existing = ResolveExistingWorkflowCleanup(session, inputId, turnId, command);
        if (existing is not null)
        {
            return new WorkflowAgentSessionCleanupReceipt(
                command.CleanupOperationId,
                inputId,
                turnId,
                SessionId);
        }
        if (_sessionWorkPort is null
            || !await _sessionWorkPort.CanStartAgentCleanupAsync(originalBinding))
        {
            throw new InvalidOperationException("Workflow cleanup does not match the active frozen execution binding.");
        }

        var events = session.RecordFollowupTurn(
            inputId,
            turnId,
            command.Prompt,
            "workflow-cleanup",
            Now());
        session.MarkTurnExecuting(turnId, Now());
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            _stateDirty = true;
            EnsurePersistenceTimer();
        }
        else
        {
            await CommitAsync(session, events);
        }

        return new WorkflowAgentSessionCleanupReceipt(
            command.CleanupOperationId,
            inputId,
            turnId,
            SessionId);
    }

    private static void ValidateWorkflowCleanupTarget(
        AgentSession session,
        AcceptWorkflowAgentSessionCleanupCommand command)
    {
        if (!string.Equals(
                session.Metadata.Label(AgentSessionQueryMetadataKeys.SourceKind),
                "workflow",
                StringComparison.Ordinal)
            || !string.Equals(
                session.Metadata.Label(AgentSessionQueryMetadataKeys.WorkflowRunId),
                command.WorkflowRunId,
                StringComparison.Ordinal)
            || !string.Equals(session.Id, command.AgentSessionId, StringComparison.Ordinal)
            || !string.Equals(session.Runtime.RunnerId, command.RunnerId, StringComparison.Ordinal)
            || !string.Equals(session.Runtime.Runtime, command.Runtime, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(session.Status.AgentRuntimeSessionId, command.RuntimeSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workflow cleanup does not match the current AgentSession runtime binding.");
        }
    }

    private static AgentTurnRecord? ResolveExistingWorkflowCleanup(
        AgentSession session,
        string inputId,
        string turnId,
        AcceptWorkflowAgentSessionCleanupCommand command)
    {
        var input = (session.Status.Inputs ?? []).SingleOrDefault(candidate =>
            string.Equals(candidate.Id, inputId, StringComparison.Ordinal));
        var turn = (session.Status.Turns ?? []).SingleOrDefault(candidate =>
            string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
        if (input is null && turn is null)
            return null;
        if (input is null || turn is null
            || !string.Equals(input.Text, command.Prompt, StringComparison.Ordinal)
            || !string.Equals(input.Source, "workflow-cleanup", StringComparison.Ordinal)
            || !turn.InputIds.Contains(inputId, StringComparer.Ordinal)
            || turn.WorkflowExecution is not null)
        {
            throw new InvalidOperationException(
                $"Workflow cleanup operation '{command.CleanupOperationId}' conflicts with persisted AgentSession state.");
        }
        return turn;
    }

    private static SessionWorkflowExecutionBinding ResolveWorkflowCleanupBinding(
        AgentSession session,
        AcceptWorkflowAgentSessionCleanupCommand command)
    {
        var matches = (session.Status.Turns ?? [])
            .Where(turn => turn.WorkflowExecution is { } binding
                && string.Equals(binding.WorkflowRunId, command.WorkflowRunId, StringComparison.Ordinal)
                && string.Equals(binding.TaskRunId, command.TaskRunId, StringComparison.Ordinal)
                && string.Equals(binding.WorkId, command.WorkId, StringComparison.Ordinal)
                && string.Equals(binding.RunnerId, command.RunnerId, StringComparison.Ordinal)
                && string.Equals(binding.AgentSessionId, command.AgentSessionId, StringComparison.Ordinal)
                && string.Equals(binding.Runtime, command.Runtime, StringComparison.Ordinal)
                && string.Equals(binding.RuntimeSessionId, command.RuntimeSessionId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 || matches[0].WorkflowExecution is not { } binding)
            throw new InvalidOperationException("Workflow cleanup has no matching frozen execution binding.");
        if (matches[0].Status != AgentTurnStatus.Completed)
            throw new InvalidOperationException("Workflow cleanup requires the original Agent turn to be terminal.");
        return binding;
    }

    private static string CleanupInputId(string operationId) => $"workflow-cleanup-input:{operationId}";

    private static string CleanupTurnId(string operationId) => $"workflow-cleanup-turn:{operationId}";
}
