using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class MohistGithubPrIssueWorkflowProfile : MohistIssueWorkflowProfileBase
{
    public MohistGithubPrIssueWorkflowProfile(
        IPromptLoader promptLoader,
        IDbContextFactory<MohistDbContext> dbFactory)
        : base(promptLoader, dbFactory)
    {
    }

    public override string Id => IssueWorkflowProfiles.GithubPrId;
    public override string DisplayName => "Mohist GitHub PR";
    public override string Description => WorkflowProfileCatalog.GithubPrProfileAsset.Description;
    public override bool IsDefault => false;
    public override WorkflowDefinition Definition => MohistWorkflow.GithubPrWorkflowDefinition;
}
