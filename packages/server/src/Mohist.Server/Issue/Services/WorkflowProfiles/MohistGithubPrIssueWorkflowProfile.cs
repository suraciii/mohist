using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class MohistGithubPrIssueWorkflowProfile : MohistIssueWorkflowProfileBase
{
    public MohistGithubPrIssueWorkflowProfile(ProjectPromptStore promptStore)
        : base(promptStore)
    {
    }

    public override string Id => IssueWorkflowProfiles.GithubPrId;
    public override string DisplayName => "Mohist GitHub PR";
    public override string Description => WorkflowProfileCatalog.GithubPrProfileAsset.Description;
    public override bool IsDefault => false;
    public override WorkflowDefinition Definition => WorkflowProfileCatalog.GithubPrWorkflowDefinition;
}
