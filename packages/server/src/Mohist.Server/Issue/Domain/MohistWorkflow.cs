using System.Text.Json;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Issue.Domain;

public static class MohistWorkflow
{
    private const string Agent = "mohist/coder-agent";

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
            new TaskDefinitionInput("proposal", "Generate proposal", Agent, AgentWith(ProposalPrompt, requiredPath: "proposal.md", model: "${{ model.stage.plan }}")),
            new TaskDefinitionInput("specs", "Write specs", Agent, AgentWith(SpecsPrompt, requiredPath: "specs", model: "${{ model.stage.plan }}")),
            new TaskDefinitionInput("design", "Create design", Agent, AgentWith(DesignPrompt, requiredPath: "design.md", model: "${{ model.stage.plan }}")),
            new TaskDefinitionInput("tasks", "Generate tasks", Agent, AgentWith(TasksPrompt, requiredPath: "tasks.json", model: "${{ model.stage.plan }}")),
            new TaskDefinitionInput("self-review", "Self review", Agent, AgentWith(SelfReviewPrompt, requiredPath: "self-review.md", markerPath: "self-review.md", marker: "PASS", model: "${{ model.stage.plan }}")),
        ],
        [
            ArtifactExists("proposal-complete", "Proposal complete", "proposal.md"),
            ArtifactExists("specs-complete", "Specs complete", "specs"),
            ArtifactExists("design-complete", "Design complete", "design.md"),
            ArtifactExists("tasks-valid", "Tasks valid", "tasks.json"),
            Marker("self-review-passed", "Self review passed", "self-review.md", "PASS", 1,
                new TaskDefinitionInput("fix-plan-review", "Fix plan review findings", Agent, AgentWith(FixPlanReviewPrompt, model: "${{ model.stage.plan }}"))),
            ScriptCheck("health", "Health", "git diff --check"),
        ],
        RequiresApproval: true);

    private static StageDefinitionInput Build => new(
        "build",
        [],
        [
            ScriptCheck("health", "Health", "git diff --check", 1,
                new TaskDefinitionInput("fix-build-health", "Fix build health", Agent, AgentWith(FixBuildHealthPrompt, model: "${{ model.stage.build }}"))),
        ],
        TasksFromUses: "mohist/openspec-tasks",
        TasksFromWith: """
        {
          "path": "${{ openspecChangeDir }}/tasks.json"
        }
        """);

    private static StageDefinitionInput Check => new(
        "check",
        [new TaskDefinitionInput("ai-review", "AI review", Agent, AgentWith(AiReviewPrompt, requiredPath: "review.md", model: "${{ model.stage.check }}"))],
        [
            ScriptCheck("health", "Health", "git diff --check", 1,
                new TaskDefinitionInput("fix-check-health", "Fix check health", Agent, AgentWith(FixCheckHealthPrompt, model: "${{ model.stage.check }}"))),
            Marker("review-passed", "Review passed", "review.md", "PASS", 2,
                new TaskDefinitionInput("fix-review-findings", "Fix review findings", Agent, AgentWith(FixReviewFindingsPrompt, model: "${{ model.stage.check }}"))),
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
                new TaskDefinitionInput("fix-integrate-health", "Fix integrate health", Agent, AgentWith(FixIntegrateHealthPrompt, model: "${{ model.stage.integrate }}"))),
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

    private static string AgentWith(string prompt, string? requiredPath = null, string? markerPath = null, string? marker = null, string? model = null)
    {
        var input = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
        };

        if (!string.IsNullOrWhiteSpace(model))
            input["model"] = model;

        var expect = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(requiredPath))
            expect["files"] = new[] { new Dictionary<string, string> { ["path"] = $"${{{{ openspecChangeDir }}}}/{requiredPath}" } };

        if (!string.IsNullOrWhiteSpace(markerPath) && !string.IsNullOrWhiteSpace(marker))
            expect["markers"] = new[] { new Dictionary<string, string> { ["path"] = $"${{{{ openspecChangeDir }}}}/{markerPath}", ["contains"] = marker } };

        if (expect.Count > 0)
            input["expect"] = expect;

        return JsonSerializer.Serialize(input, WorkflowVariableJson.Options);
    }

    private const string ProposalPrompt = """
    Create ${{ openspecChangeDir }}/proposal.md.
    The proposal explains why the change is needed, what will change, capabilities affected, and implementation impact.
    Keep it concise and avoid copying prompt instructions into the file.
    """;

    private const string SpecsPrompt = """
    Read ${{ openspecChangeDir }}/proposal.md and create spec delta files under ${{ openspecChangeDir }}/specs/<capability>/spec.md.
    Use ADDED/MODIFIED/REMOVED/RENAMED Requirements sections.
    Every requirement must include at least one #### Scenario with WHEN/THEN behavior.
    """;

    private const string DesignPrompt = """
    Create ${{ openspecChangeDir }}/design.md.
    Include Context, Goals/Non-Goals, Decisions with rationale, Risks/Trade-offs, Migration Plan, and Open Questions.
    Focus on how to implement the proposal and specs.
    """;

    private const string TasksPrompt = """
    Create ${{ openspecChangeDir }}/tasks.json.
    The file must contain a JSON object with a tasks array.
    Each task must have id, title, description, acceptanceCriteria, priority, mode, type, output, dependsOn, passes=false, and notes.
    Tasks must be ordered by dependency and every non-first task should depend on earlier task IDs.
    """;

    private const string SelfReviewPrompt = """
    Review proposal.md, specs, design.md, and tasks.json in ${{ openspecChangeDir }}.
    Fix any issues directly when possible.
    Create ${{ openspecChangeDir }}/self-review.md with Result, repaired/blocking/follow-up items, and exactly one final marker:
    <promise>PASS</promise> or <promise>FAIL</promise>.
    """;

    private const string FixPlanReviewPrompt = """
    Fix the plan review findings in ${{ openspecChangeDir }}.
    Update proposal.md, specs, design.md, tasks.json, or self-review.md as needed.
    """;

    private const string FixBuildHealthPrompt = """
    Fix the build-stage health failure reported by `git diff --check`.
    Keep changes focused on whitespace and patch formatting issues.
    """;

    private const string FixCheckHealthPrompt = """
    Fix the check-stage health failure reported by `git diff --check`.
    Keep changes focused on whitespace and patch formatting issues.
    """;

    private const string FixReviewFindingsPrompt = """
    Read ${{ openspecChangeDir }}/review.md and fix the blocking findings.
    Keep changes focused on review failures and preserve existing passing behavior.
    """;

    private const string AiReviewPrompt = """
    Review the current workspace implementation and the change artifacts in ${{ openspecChangeDir }}.
    Write the review result to ${{ openspecChangeDir }}/review.md.
    The review must identify blocking correctness, test, integration, or product contract issues.
    End the file with exactly one marker:
    <promise>PASS</promise>
    or
    <promise>FAIL</promise>.
    """;

    private const string FixIntegrateHealthPrompt = """
    Fix the integrate-stage health failure reported by `git diff --check`.
    Keep changes focused on whitespace and patch formatting issues.
    """;

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
