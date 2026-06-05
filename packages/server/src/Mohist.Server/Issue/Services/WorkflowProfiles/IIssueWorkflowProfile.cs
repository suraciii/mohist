using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public interface IIssueWorkflowProfile
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    bool IsDefault { get; }
    WorkflowDefinition Definition { get; }
    string BuildVariables(string workflowRunId, Domain.Issue issue, WorkflowProjectContext project, Dictionary<string, object?>? globalAgentConfig = null);
    Dictionary<string, Dictionary<string, string>>? BuildStageVariables(Domain.Issue issue, Dictionary<string, Dictionary<string, object?>>? globalStageAgentConfigs = null);
    MohistDefaultWorkflowState ProjectWorkflowState(Domain.Issue issue, WorkflowStatusView? workflow);
    MohistDefaultWorkflowState ProjectWorkflowState(IssueReadModel issue, WorkflowStatusView? workflow);
}
