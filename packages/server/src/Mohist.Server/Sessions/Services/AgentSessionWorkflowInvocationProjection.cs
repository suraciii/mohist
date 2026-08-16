using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

internal static class AgentSessionWorkflowInvocationProjection
{
    internal static WorkflowInvocationLineageDto? Build(AgentSessionRecord record)
    {
        if (!string.Equals(
                record.Label(AgentSessionQueryMetadataKeys.SourceKind),
                "workflow",
                StringComparison.Ordinal))
        {
            return null;
        }

        var workflowRunId = record.Label(AgentSessionQueryMetadataKeys.WorkflowRunId);
        var taskRunId = record.Label(AgentSessionQueryMetadataKeys.TaskRunId);
        var invocationId = record.Label(AgentSessionQueryMetadataKeys.InvocationId);
        if (string.IsNullOrWhiteSpace(workflowRunId)
            || string.IsNullOrWhiteSpace(taskRunId)
            || string.IsNullOrWhiteSpace(invocationId))
        {
            return null;
        }

        var input = record.Session.Status.Inputs?.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.JobId));
        var turn = record.Session.Status.Turns?
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.JobId));
        var jobId = input?.JobId ?? turn?.JobId;
        var workId = record.Label(AgentSessionQueryMetadataKeys.WorkId);
        if (string.IsNullOrWhiteSpace(jobId)
            || string.IsNullOrWhiteSpace(workId)
            || input is null
            || turn is null)
        {
            return null;
        }

        return new WorkflowInvocationLineageDto(
            invocationId,
            workflowRunId,
            taskRunId,
            workId,
            jobId,
            record.Session.Id,
            input.Id,
            turn.Id);
    }
}
