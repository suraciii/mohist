using Mohist.Server.Issue.Grains;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public interface IIssueWorkflowProfile
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    bool IsDefault { get; }
    WorkflowDefinition Definition { get; }
    MohistDefaultWorkflowState ProjectWorkflowState(Domain.Issue issue, WorkflowStatusView? workflow);
    MohistDefaultWorkflowState ProjectWorkflowState(IssueReadModel issue, WorkflowStatusView? workflow);
}
