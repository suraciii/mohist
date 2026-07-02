using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class MohistGithubPrIssueWorkflowProfile : MohistIssueWorkflowProfileBase
{
    public const string GithubPrDescription = """
        Default general-purpose Mohist pipeline that delivers through a GitHub PR: plan (proposal, specs, design, tasks, self-review, open draft PR) → build (load tasks, verify) → check (AI review, push, mark PR ready) → integrate (archive, push, merge PR).
        Requires human approval at the plan and check stages. The workflow opens a draft PR as the last plan task, marks it ready after the check stage approves it, and squash-merges it into the repository base branch on integrate completion.
        Typical duration: 20-60 minutes for a focused change.
        Choose this over mohist/local when you want each issue to ship as a reviewable, traceable GitHub PR.
        Not suited for: trivial one-line fixes, throwaway spikes, or quick experiments — these don't warrant a full plan-check-integrate cycle.
        Requires the `gh` CLI on the runner host and `gh auth login` against the target repository.
        """;

    public MohistGithubPrIssueWorkflowProfile(
        IPromptLoader promptLoader,
        IDbContextFactory<MohistDbContext> dbFactory)
        : base(promptLoader, dbFactory)
    {
    }

    public override string Id => IssueWorkflowProfiles.GithubPrId;
    public override string DisplayName => "Mohist GitHub PR";
    public override string Description => GithubPrDescription.TrimEnd();
    public override bool IsDefault => false;
    public override WorkflowDefinition Definition => MohistWorkflow.GithubPrWorkflowDefinition;
    public override IReadOnlyList<string> SuitableFor { get; } = [
        "default general-purpose workflow",
        "changes that warrant a full plan-build-check-integrate lifecycle",
        "work that should ship as a reviewable, traceable GitHub PR per issue"
    ];
}
