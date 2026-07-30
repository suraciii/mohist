using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class MohistLocalIssueWorkflowProfile : MohistIssueWorkflowProfileBase
{
    public MohistLocalIssueWorkflowProfile(ProjectPromptStore promptStore)
        : base(promptStore)
    {
    }

    public override string Id => IssueWorkflowProfiles.LocalId;
    public override string DisplayName => "Mohist Local";
    public override string Description => WorkflowProfileCatalog.Profile.Description;
    public override bool IsDefault => true;
}
