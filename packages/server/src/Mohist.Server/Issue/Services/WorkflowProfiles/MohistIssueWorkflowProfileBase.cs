using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public abstract class MohistIssueWorkflowProfileBase : IIssueWorkflowProfile
{
    public abstract WorkflowProfile Profile { get; }
}