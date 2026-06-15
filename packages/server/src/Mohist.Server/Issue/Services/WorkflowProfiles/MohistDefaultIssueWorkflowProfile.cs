using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class MohistDefaultIssueWorkflowProfile : MohistIssueWorkflowProfileBase
{
    public MohistDefaultIssueWorkflowProfile(
        IPromptLoader promptLoader,
        IDbContextFactory<MohistDbContext> dbFactory)
        : base(promptLoader, dbFactory)
    {
    }

    public override string Id => IssueWorkflowProfiles.DefaultId;
    public override string DisplayName => "Mohist Default";
    public override string Description => ResolveDescription();
    public override bool IsDefault => true;

    private static string ResolveDescription()
    {
        var description = MohistWorkflow.Definition.Description;
        return string.IsNullOrWhiteSpace(description) ? "No description provided" : description;
    }
}
