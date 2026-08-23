using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    private static void EnsureRuntimeSessionPresent(AgentSession session)
    {
        if (!session.IsRuntimeSessionMissing(IsRuntimeRegistered)) return;
        throw new RuntimeSessionMissingException(session.Id, session.Status.AgentRuntimeSessionId, session.Runtime.Runtime);
    }

    private static bool HasInitialLaunch(AgentSession session) =>
        (session.Status.Turns ?? [])
            .Any(turn => !string.IsNullOrWhiteSpace(turn.JobId));

    private static bool IsRuntimeRegistered(string runtime) =>
        string.Equals(runtime, OpenCodeRuntime, StringComparison.OrdinalIgnoreCase)
        || string.Equals(runtime, PiRuntime, StringComparison.OrdinalIgnoreCase);

    private static bool ValidateRuntimeEventAdmission(
        AgentSession session,
        AppendAgentSessionRuntimeEventsCommand command)
    {
        if (command.WorkflowExecution is not null && command.SessionTurnId is not null)
            throw new InvalidOperationException("Runtime events cannot carry both Workflow execution and Session turn identity.");

        var isUnattributed = command.WorkflowExecution is null && command.SessionTurnId is null;
        var isWorkflowIntroduced = string.Equals(
            session.Metadata.Label(AgentSessionQueryMetadataKeys.SourceKind),
            "workflow",
            StringComparison.Ordinal);
        var isPureActivityBatch = command.RuntimeEvents.Count > 0
            && command.RuntimeEvents.All(runtimeEvent =>
                string.Equals(runtimeEvent.Type, RuntimeEventTypes.SessionActivity, StringComparison.Ordinal));
        var isCurrentRuntimeBinding = !string.IsNullOrWhiteSpace(command.RuntimeSessionId)
            && string.Equals(command.RuntimeSessionId, session.Status.AgentRuntimeSessionId, StringComparison.Ordinal);
        var hasPendingFollowupEvent = command.RuntimeEvents.Count > 0
            && command.RuntimeEvents.All(runtimeEvent =>
            {
                var payload = SafeDeserialize(runtimeEvent.PayloadJson);
                var operationId = AgentSessionJsonHelper.GetStringProp(payload, "operationId");
                return !string.IsNullOrWhiteSpace(operationId)
                    && GetPendingFollowups(session).Any(lease =>
                        string.Equals(lease.OperationId, operationId, StringComparison.Ordinal));
            });
        if (isWorkflowIntroduced
            && isUnattributed
            && isCurrentRuntimeBinding
            && !isPureActivityBatch
            && !hasPendingFollowupEvent)
        {
            throw new InvalidOperationException("Workflow runtime events require the acknowledged Agent turn binding.");
        }

        if (command.SessionTurnId is not null)
            ValidateSessionRuntimeEventTurnIds(session, command.RuntimeEvents, command.SessionTurnId);

        var hasWorkflowTurnForRuntime = (session.Status.Turns ?? []).Any(turn =>
            turn.WorkflowExecution is { } binding
            && string.Equals(binding.RuntimeSessionId, command.RuntimeSessionId, StringComparison.Ordinal));
        if (hasWorkflowTurnForRuntime
            && command.SessionTurnId is null
            && !isPureActivityBatch
            && !hasPendingFollowupEvent)
        {
            if (command.WorkflowExecution is null)
                throw new InvalidOperationException("Workflow runtime events require the acknowledged Agent turn binding.");
            ValidateWorkflowRuntimeEventBinding(session, command.WorkflowExecution);
        }
        else if (command.WorkflowExecution is not null)
        {
            ValidateWorkflowRuntimeEventBinding(session, command.WorkflowExecution);
        }
        if (command.WorkflowExecution is { } workflowBinding)
            ValidateWorkflowRuntimeEventTurnIds(command.RuntimeEvents, workflowBinding);

        return isWorkflowIntroduced
            && isUnattributed
            && isCurrentRuntimeBinding
            && isPureActivityBatch
            && !hasPendingFollowupEvent;
    }
}
