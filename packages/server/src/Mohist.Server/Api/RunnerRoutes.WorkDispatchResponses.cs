using Mohist.Server.Issue.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Slack.Services;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    internal static async Task<WorkDispatchResponse> ToWorkDispatchResponseAsync(
        WorkDispatch work,
        Func<string, int, Task<ParentIssueContext?>> resolveParentIssueContext,
        ManagerExecutionCapabilityIssuer? managerCredentials = null)
    {
        ParentIssueContextResponse? parentIssueContext = null;
        var projectId = work.Issue?.ProjectId ?? work.ProjectId;
        var issueNumber = work.Issue?.IssueNumber;
        var isWorkflowTask = string.Equals(work.OwnerKind, WorkDispatchOwnerKinds.Workflow, StringComparison.Ordinal)
            && string.Equals(work.WorkType, WorkItemTypes.Task, StringComparison.Ordinal);
        var isWorkflowAgentJob = string.Equals(work.OwnerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(work.WorkflowRunId)
            && !string.IsNullOrWhiteSpace(work.ActionAttemptId);
        if ((isWorkflowTask || isWorkflowAgentJob)
            && !string.IsNullOrWhiteSpace(projectId)
            && issueNumber is > 0)
        {
            var resolved = await resolveParentIssueContext(projectId, issueNumber.Value);
            if (resolved is not null)
                parentIssueContext = new ParentIssueContextResponse(resolved.Title, resolved.Body);
        }

        return new WorkDispatchResponse(
            work.WorkflowRunId,
            work.WorkId,
            work.Uses,
            work.With,
            work.Variables,
            work.WorkType,
            work.Stage,
            work.Title,
            projectId,
            issueNumber,
            work.EpicNumber,
            work.Artifacts,
            work.SetVars,
            work.OwnerKind,
            work.AgentJobId,
            AgentSessionId: work.AgentSessionId,
            InitialInputId: work.InitialInputId,
            InitialTurnId: work.InitialTurnId,
            Recovery: work.Recovery,
            RecoveryRemaining: work.RecoveryRemaining,
            Expect: work.Expect,
            ParentIssueContext: parentIssueContext,
            AgentDefinition: work.AgentDefinition,
            AgentSessionStartup: work.AgentSessionStartup,
            ActionAttemptId: work.ActionAttemptId,
            ManagerExecutionGrant: managerCredentials?.IssueFor(work),
            OriginMarker: work.OriginMarker);
    }
}
