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
            new TaskDefinitionInput("proposal", "Generate proposal", Agent, AgentWith("plan", "proposal")),
            new TaskDefinitionInput("specs", "Write specs", Agent, AgentWith("plan", "specs")),
            new TaskDefinitionInput("design", "Create design", Agent, AgentWith("plan", "design")),
            new TaskDefinitionInput("tasks", "Generate tasks", Agent, AgentWith("plan", "tasks")),
            new TaskDefinitionInput("self-review", "Self review", Agent, AgentWith("plan", "self-review")),
        ],
        [
            ArtifactExists("proposal-complete", "Proposal complete", "proposal.md"),
            ArtifactExists("specs-complete", "Specs complete", "specs"),
            ArtifactExists("design-complete", "Design complete", "design.md"),
            ArtifactExists("tasks-valid", "Tasks valid", "tasks.json"),
            Marker("self-review-passed", "Self review passed", "self-review.md", "PASS", 1,
                new TaskDefinitionInput("fix-plan-review", "Fix plan review findings", Agent, AgentWith("plan", "fix-plan-review"))),
            HealthGate("health:plan", "Plan health gate"),
        ],
        RequiresApproval: true);

    private static StageDefinitionInput Build => new(
        "build",
        [],
        [
            HealthGate("health:build", "Build health gate", 1,
                new TaskDefinitionInput("fix-build-health", "Fix build health", Agent, AgentWith("build", "fix-build-health"))),
        ],
        TasksFromUses: "mohist/openspec-tasks",
        TasksFromWith: """
        {
          "path": "${{ artifacts.changeDir }}/tasks.json"
        }
        """);

    private static StageDefinitionInput Check => new(
        "check",
        [new TaskDefinitionInput("ai-review", "AI review", "mohist/check/ai-review", AgentWith("check", "ai-review"))],
        [
            HealthGate("health:check", "Check health gate", 1,
                new TaskDefinitionInput("fix-check-health", "Fix check health", Agent, AgentWith("check", "fix-check-health"))),
            Marker("review-passed", "Review passed", "review.md", "PASS", 2,
                new TaskDefinitionInput("fix-review-findings", "Fix review findings", Agent, AgentWith("check", "fix-review-findings"))),
            new CheckDefinitionInput("merge-ready", "Merge ready", "mohist/merge-ready", RetryLimit: 1,
                RetryTask: new TaskDefinitionInput("fix-merge-readiness", "Fix merge readiness", "mohist/rebase")),
        ],
        RequiresApproval: true);

    private static StageDefinitionInput Integrate => new(
        "integrate",
        [
            new TaskDefinitionInput("integrate:spec-sync", "Sync specs", "mohist/openspec-sync", ChangeDirWith()),
            new TaskDefinitionInput("integrate:archive-change", "Archive change", "mohist/archive-change", ChangeDirWith()),
            new TaskDefinitionInput("integrate:merge", "Merge branch", "mohist/merge"),
        ],
        [
            HealthGate("health:integrate", "Post-delivery health check", 1,
                new TaskDefinitionInput("fix-integrate-health", "Fix integrate health", Agent, AgentWith("integrate", "fix-integrate-health"))),
        ]);

    private static CheckDefinitionInput ArtifactExists(string name, string title, string path) => new(
        name,
        title,
        "mohist/artifact-exists",
        $$$"""
        {
          "path": "${{ artifacts.changeDir }}/{{{path}}}"
        }
        """);

    private static CheckDefinitionInput Marker(
        string name,
        string title,
        string path,
        string expect,
        int retryLimit,
        TaskDefinitionInput retryTask) => new(
            name,
            title,
            "mohist/marker",
            $$$"""
            {
              "path": "${{ artifacts.changeDir }}/{{{path}}}",
              "expect": "{{{expect}}}"
            }
            """,
            retryLimit,
            retryTask);

    private static CheckDefinitionInput HealthGate(string name, string title, int retryLimit = 0, TaskDefinitionInput? retryTask = null) => new(
        name,
        title,
        "mohist/health-gate",
        name switch
        {
            "health:plan" => """{"command":"npm ci && npm run typecheck","timeout":300000}""",
            "health:build" => """{"command":"npm ci && npm run build","timeout":300000}""",
            "health:check" => """{"command":"npm ci && npm run build && npm test","timeout":300000}""",
            "health:integrate" => """{"command":"npm ci && npm run build && npm test","timeout":300000}""",
            _ => """{"command":"npm ci && npm run build && npm test","timeout":300000}"""
        },
        retryLimit,
        retryTask);

    private static string AgentWith(string stage, string task) => $$$"""
    {
      "stage": "{{{stage}}}",
      "task": "{{{task}}}",
      "changeDir": "${{ artifacts.changeDir }}"
    }
    """;

    private static string ChangeDirWith() => """
    {
      "changeDir": "${{ artifacts.changeDir }}"
    }
    """;
}
