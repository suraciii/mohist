using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class MohistLocalIssueWorkflowProfile : MohistIssueWorkflowProfileBase
{
    public override WorkflowProfile Profile => WorkflowProfileCatalog.Profile;
}