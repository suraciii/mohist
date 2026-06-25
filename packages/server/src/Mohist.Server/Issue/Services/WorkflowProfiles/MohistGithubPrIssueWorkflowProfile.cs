using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class MohistGithubPrIssueWorkflowProfile : MohistIssueWorkflowProfileBase
{
    public const string GithubPrDescription = """
        Full Mohist pipeline for shipping user-visible changes end-to-end through a GitHub PR.
        Stages: plan (proposal, specs, design, tasks, self-review, open draft PR) → build (load tasks, verify) → check (AI review, push, mark PR ready) → integrate (spec sync, archive, push, merge PR).
        Requires human approval at the plan and check stages. The workflow opens a draft PR as the last plan-stage task once all plan artifacts are ready, marks the PR ready after the check stage approves it, and squash-merges it into the repository base branch on integrate completion — every merge becomes an atomic, traceable PR with full commit history.
        Prerequisite: the runner host must have the `gh` CLI installed and authenticated (`gh auth login`); missing or unauthenticated `gh` fails fast as a non-retryable configuration error.
        Typical duration: 20-60 minutes for a focused change.
        Best suited for: new features, user-visible behavior changes, and changes where auditability on GitHub matters.
        Not suited for: simple bug fixes, exploration or throwaway prototypes, or pure refactors with no behavior change.
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
        "features needing a public, traceable GitHub PR per issue",
        "teams that want squash merges authored as GitHub PRs instead of local fast-forwards",
        "workflows that benefit from per-merge commit history visible on GitHub",
        "runner hosts with the gh CLI installed and authenticated (gh auth login)"
    ];
}
