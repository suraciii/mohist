using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Domain;

public static class MohistPipeline
{
    private const string Agent = "mohist/agent";

    public static WorkflowDefinitionInput Definition => new(
    [
        Plan,
        Build,
        Check,
        Integrate
    ]);

    private static StageDefinitionInput Plan => new(
        "plan",
        [
            new TaskDefinitionInput("proposal", "Generate proposal", Agent),
            new TaskDefinitionInput("specs", "Write specs", Agent),
            new TaskDefinitionInput("design", "Create design", Agent),
            new TaskDefinitionInput("tasks", "Generate tasks", Agent),
            new TaskDefinitionInput("self-review", "Self review", Agent),
        ],
        [
            new CheckDefinitionInput("proposal-complete", "Proposal complete", "mohist/artifact-exists"),
            new CheckDefinitionInput("specs-complete", "Specs complete", "mohist/artifact-exists"),
            new CheckDefinitionInput("design-complete", "Design complete", "mohist/artifact-exists"),
            new CheckDefinitionInput("tasks-valid", "Tasks valid", "mohist/artifact-exists"),
            new CheckDefinitionInput("self-review-passed", "Self review passed", "mohist/marker", RetryLimit: 1,
                RetryTask: new TaskDefinitionInput("fix-plan-review", "Fix plan review findings", Agent)),
            new CheckDefinitionInput("health:plan", "Plan health gate", "mohist/health-gate"),
        ],
        RequiresApproval: true);

    private static StageDefinitionInput Build => new(
        "build",
        [],
        [
            new CheckDefinitionInput("health:build", "Build health gate", "mohist/health-gate", RetryLimit: 1,
                RetryTask: new TaskDefinitionInput("fix-build-health", "Fix build health", Agent)),
        ],
        TasksFromUses: "mohist/openspec-tasks");

    private static StageDefinitionInput Check => new(
        "check",
        [new TaskDefinitionInput("ai-review", "AI review", "mohist/check/ai-review")],
        [
            new CheckDefinitionInput("health:check", "Check health gate", "mohist/health-gate", RetryLimit: 1,
                RetryTask: new TaskDefinitionInput("fix-check-health", "Fix check health", Agent)),
            new CheckDefinitionInput("review-passed", "Review passed", "mohist/marker", RetryLimit: 2,
                RetryTask: new TaskDefinitionInput("fix-review-findings", "Fix review findings", Agent)),
            new CheckDefinitionInput("merge-ready", "Merge ready", "mohist/merge-ready", RetryLimit: 1,
                RetryTask: new TaskDefinitionInput("fix-merge-readiness", "Fix merge readiness", "mohist/rebase")),
        ],
        RequiresApproval: true);

    private static StageDefinitionInput Integrate => new(
        "integrate",
        [
            new TaskDefinitionInput("integrate:spec-sync", "Sync specs", "mohist/openspec-sync"),
            new TaskDefinitionInput("integrate:archive-change", "Archive change", "mohist/archive-change"),
            new TaskDefinitionInput("integrate:merge", "Merge branch", "mohist/merge"),
        ],
        [
            new CheckDefinitionInput("health:integrate", "Post-delivery health check", "mohist/health-gate", RetryLimit: 1,
                RetryTask: new TaskDefinitionInput("fix-integrate-health", "Fix integrate health", Agent)),
        ]);
}
