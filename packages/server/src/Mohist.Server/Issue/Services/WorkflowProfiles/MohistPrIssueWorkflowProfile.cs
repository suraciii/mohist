using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services.Prompts;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public class MohistPrIssueWorkflowProfile : MohistIssueWorkflowProfileBase
{
    public const string PrDescription = """
        Full Mohist pipeline for shipping user-visible changes end-to-end via a GitHub PR.
        Stages: plan (proposal, specs, design, tasks, self-review) → build → check (AI review, merge readiness) → integrate (spec sync, archive, prepare, publish via PR).
        Requires human approval at the plan and check stages. On integrate completion the workflow force-pushes the working branch to origin, opens or reuses a GitHub PR, and squash-merges it — every merge becomes an atomic, traceable PR with full commit history.
        Prerequisite: the runner host must have the `gh` CLI installed and authenticated (`gh auth login`); missing or unauthenticated `gh` fails fast as a non-retryable configuration error.
        Typical duration: 20-60 minutes for a focused change.
        Best suited for: new features, user-visible behavior changes, and changes where auditability on GitHub matters.
        Not suited for: simple bug fixes, exploration or throwaway prototypes, or pure refactors with no behavior change.
        """;

    public MohistPrIssueWorkflowProfile(
        IPromptLoader promptLoader,
        IDbContextFactory<MohistDbContext> dbFactory)
        : base(promptLoader, dbFactory)
    {
    }

    public override string Id => IssueWorkflowProfiles.PrId;
    public override string DisplayName => "Mohist PR";
    public override string Description => PrDescription.TrimEnd();
    public override bool IsDefault => false;
    public override WorkflowDefinition Definition => MohistWorkflow.PrWorkflowDefinition;
    public override IReadOnlyList<string> SuitableFor { get; } = [
        "features needing a public, traceable GitHub PR per issue",
        "teams that want squash merges authored as GitHub PRs instead of local fast-forwards",
        "workflows that benefit from per-merge commit history visible on GitHub",
        "runner hosts with the gh CLI installed and authenticated (gh auth login)"
    ];
}
