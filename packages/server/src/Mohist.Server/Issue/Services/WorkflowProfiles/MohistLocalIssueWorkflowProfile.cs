using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class MohistLocalIssueWorkflowProfile : MohistIssueWorkflowProfileBase
{
    public MohistLocalIssueWorkflowProfile(
        IPromptLoader promptLoader,
        IDbContextFactory<MohistDbContext> dbFactory)
        : base(promptLoader, dbFactory)
    {
    }

    public override string Id => IssueWorkflowProfiles.LocalId;
    public override string DisplayName => "Mohist Local";
    public override string Description => MohistWorkflow.ResolveDescription(MohistWorkflow.Definition);
    public override bool IsDefault => true;
}
