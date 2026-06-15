using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class MohistQuickFixIssueWorkflowProfile : MohistIssueWorkflowProfileBase
{
    public const string HardcodedDescription = """
        Lightweight workflow for small, low-risk, fast-turnaround changes.
        Suited for: simple bug fixes, single-file or few-line corrections, trivial test updates, and obvious defects with a known fix.
        Goal is a fast, low-friction path: minimal planning artifacts, no design document, no spec delta, and lighter review.
        Typical duration: 5-15 minutes for a focused fix.
        Not suited for: new user-visible features, exploration or throwaway prototypes (use experiment), or changes that need a design or spec delta — fall back to mohist/default for any of those.
        """;

    public MohistQuickFixIssueWorkflowProfile(
        IPromptLoader promptLoader,
        IDbContextFactory<MohistDbContext> dbFactory)
        : base(promptLoader, dbFactory)
    {
    }

    public override string Id => IssueWorkflowProfiles.QuickFixId;
    public override string DisplayName => "Mohist Quick Fix";
    public override string Description => HardcodedDescription;
    public override bool IsDefault => false;
}
