using System.Text.Json;
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
            new TaskDefinitionInput("proposal", "Generate proposal", Agent, AgentWith("plan", "proposal", requiredPath: "proposal.md")),
            new TaskDefinitionInput("specs", "Write specs", Agent, AgentWith("plan", "specs", requiredPath: "specs")),
            new TaskDefinitionInput("design", "Create design", Agent, AgentWith("plan", "design", requiredPath: "design.md")),
            new TaskDefinitionInput("tasks", "Generate tasks", Agent, AgentWith("plan", "tasks", requiredPath: "tasks.json")),
            new TaskDefinitionInput("self-review", "Self review", Agent, AgentWith("plan", "self-review", requiredPath: "self-review.md", markerPath: "self-review.md", marker: "PASS")),
        ],
        [
            ArtifactExists("proposal-complete", "Proposal complete", "proposal.md"),
            ArtifactExists("specs-complete", "Specs complete", "specs"),
            ArtifactExists("design-complete", "Design complete", "design.md"),
            ArtifactExists("tasks-valid", "Tasks valid", "tasks.json"),
            Marker("self-review-passed", "Self review passed", "self-review.md", "PASS", 1,
                new TaskDefinitionInput("fix-plan-review", "Fix plan review findings", Agent, AgentWith("plan", "fix-plan-review"))),
            ScriptCheck("health", "Health", "git diff --check"),
        ],
        RequiresApproval: true);

    private static StageDefinitionInput Build => new(
        "build",
        [],
        [
            ScriptCheck("health", "Health", "git diff --check", 1,
                new TaskDefinitionInput("fix-build-health", "Fix build health", Agent, AgentWith("build", "fix-build-health"))),
        ],
        TasksFromUses: "mohist/openspec-tasks",
        TasksFromWith: """
        {
          "path": "${{ openspecChangeDir }}/tasks.json"
        }
        """);

    private static StageDefinitionInput Check => new(
        "check",
        [new TaskDefinitionInput("ai-review", "AI review", "mohist/check/ai-review", ChangeDirWith())],
        [
            ScriptCheck("health", "Health", "git diff --check", 1,
                new TaskDefinitionInput("fix-check-health", "Fix check health", Agent, AgentWith("check", "fix-check-health"))),
            Marker("review-passed", "Review passed", "review.md", "PASS", 2,
                new TaskDefinitionInput("fix-review-findings", "Fix review findings", Agent, AgentWith("check", "fix-review-findings"))),
            new CheckDefinitionInput("merge-ready", "Merge ready", "mohist/merge-ready"),
        ],
        RequiresApproval: true);

    private static StageDefinitionInput Integrate => new(
        "integrate",
        [
            new TaskDefinitionInput("integrate:spec-sync", "Sync specs", "mohist/openspec-sync", ChangeDirWith()),
            new TaskDefinitionInput("integrate:archive-change", "Archive change", "mohist/archive-change", ChangeDirWith()),
            new TaskDefinitionInput("integrate:merge", "Merge branch", "mohist/merge", MergeWith()),
        ],
        [
            ScriptCheck("health", "Health", "git diff --check", 1,
                new TaskDefinitionInput("fix-integrate-health", "Fix integrate health", Agent, AgentWith("integrate", "fix-integrate-health"))),
        ]);

    private static CheckDefinitionInput ArtifactExists(string name, string title, string path) => new(
        name,
        title,
        "core/artifact-exists",
        $$$"""
        {
          "path": "${{ openspecChangeDir }}/{{{path}}}"
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
            "core/marker",
            $$$"""
            {
              "path": "${{ openspecChangeDir }}/{{{path}}}",
              "expect": "{{{expect}}}"
            }
            """,
            retryLimit,
            retryTask);

    private static CheckDefinitionInput ScriptCheck(string name, string title, string run, int retryLimit = 0, TaskDefinitionInput? retryTask = null) => new(
        name,
        title,
        "core/script",
        JsonSerializer.Serialize(new { run, timeout = 300_000 }, WorkflowVariableJson.Options),
        retryLimit,
        retryTask);

    private static string AgentWith(string stage, string task, string? requiredPath = null, string? markerPath = null, string? marker = null)
    {
        var input = new Dictionary<string, object?>
        {
            ["stage"] = stage,
            ["task"] = task
        };

        if (!string.IsNullOrWhiteSpace(requiredPath))
            input["requireFiles"] = new[] { new Dictionary<string, string> { ["path"] = $"${{{{ openspecChangeDir }}}}/{requiredPath}" } };

        if (!string.IsNullOrWhiteSpace(markerPath) && !string.IsNullOrWhiteSpace(marker))
            input["requireMarkers"] = new[] { new Dictionary<string, string> { ["path"] = $"${{{{ openspecChangeDir }}}}/{markerPath}", ["marker"] = marker } };

        return JsonSerializer.Serialize(input, WorkflowVariableJson.Options);
    }

    private static string ChangeDirWith() => """
    {
      "changeDir": "${{ openspecChangeDir }}"
    }
    """;

    private static string MergeWith() => """
    {
      "source": "mo/issue-${{ issue.number }}",
      "target": "${{ project.baseBranch }}",
      "strategy": "squash",
      "message": "Complete issue #${{ issue.number }}"
    }
    """;
}
