using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.WorkflowProfiles;

public interface IIssueWorkflowProfile
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    bool IsDefault { get; }
    WorkflowDefinitionInput Definition { get; }
    string BuildVariables(string workflowRunId, Domain.Issue issue, WorkflowProjectContext project);
    MohistDefaultWorkflowState Project(Domain.Issue issue, WorkflowStatusSnapshot? workflow);
    MohistDefaultWorkflowState Project(Queries.IssueReadModel issue, WorkflowStatusSnapshot? workflow);
}
