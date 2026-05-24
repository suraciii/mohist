using System.Text.Json;

namespace Mohist.Runner.Actions;

public static class AgentPromptRenderer
{
    public static string Render(AgentPromptContext context)
    {
        var variables = new VariableBag(context.Variables);
        var issueTitle = variables.String("issue.title") ?? "Untitled issue";
        var issueBody = variables.String("issue.body") ?? "";
        var issueNumber = variables.String("issue.number") ?? "";
        var projectName = variables.String("project.name") ?? variables.String("project.id") ?? "project";
        var projectPath = variables.String("project.path") ?? context.WorkDir;
        var model = ResolveModel(context.Variables, context.Stage);

        var changeDir = context.ChangeDir is null
            ? "No change artifact directory was provided."
            : $"Change artifact directory: {context.ChangeDir}";
        var modelLine = string.IsNullOrWhiteSpace(model) ? "Model: default runner model" : $"Model: {model}";

        return $$"""
        You are running a Mohist workflow task.

        ## Project
        - Project: {{projectName}}
        - Workspace: {{projectPath}}
        - {{modelLine}}

        ## Issue
        - Issue: #{{issueNumber}} {{issueTitle}}

        {{issueBody}}

        ## Current Work
        - Stage: {{context.Stage}}
        - Task: {{context.Task}}
        - Work directory: {{context.WorkDir}}
        - {{changeDir}}

        {{StageContract(context.Stage, context.Task, context.ChangeDir)}}

        ## Rules
        - Complete only this workflow task.
        - Keep changes scoped to the current workspace.
        - Follow existing project conventions.
        - Do not mark work complete unless the required output exists and is internally consistent.
        """;
    }

    private static string StageContract(string stage, string task, string? changeDir) => (stage, task) switch
    {
        ("plan", "proposal") => $$"""
        ## Output Contract: Proposal
        Create {{PathFor(changeDir, "proposal.md")}}.
        The proposal explains why the change is needed, what will change, capabilities affected, and implementation impact.
        Keep it concise and avoid copying prompt instructions into the file.
        """,
        ("plan", "specs") => $$"""
        ## Output Contract: Specs
        Read {{PathFor(changeDir, "proposal.md")}} and create spec delta files under {{PathFor(changeDir, "specs/<capability>/spec.md")}}.
        Use ADDED/MODIFIED/REMOVED/RENAMED Requirements sections.
        Every requirement must include at least one #### Scenario with WHEN/THEN behavior.
        """,
        ("plan", "design") => $$"""
        ## Output Contract: Design
        Create {{PathFor(changeDir, "design.md")}}.
        Include Context, Goals/Non-Goals, Decisions with rationale, Risks/Trade-offs, Migration Plan, and Open Questions.
        Focus on how to implement the proposal and specs.
        """,
        ("plan", "tasks") => $$"""
        ## Output Contract: Tasks
        Create {{PathFor(changeDir, "tasks.json")}}.
        The file must contain a JSON object with a tasks array.
        Each task must have id, title, description, acceptanceCriteria, priority, mode, type, output, dependsOn, passes=false, and notes.
        Tasks must be ordered by dependency and every non-first task should depend on earlier task IDs.
        """,
        ("plan", "self-review") => $$"""
        ## Output Contract: Self Review
        Review proposal.md, specs, design.md, and tasks.json in {{changeDir ?? "the change artifact directory"}}.
        Fix any issues directly when possible.
        Create {{PathFor(changeDir, "self-review.md")}} with Result, repaired/blocking/follow-up items, and exactly one final marker:
        <promise>PASS</promise> or <promise>FAIL</promise>.
        """,
        ("build", _) => $$"""
        ## Output Contract: Build Task
        Implement this single task from {{PathFor(changeDir, "tasks.json")}}.
        Read proposal.md, specs, design.md, and tasks.json before editing code.
        Make focused code/test changes and verify the acceptance criteria for this task.
        """,
        ("check", "fix-review-findings") => $$"""
        ## Output Contract: Fix Review Findings
        Read {{PathFor(changeDir, "review.md")}} and fix the blocking findings.
        Keep changes focused on review failures and preserve existing passing behavior.
        """,
        ("check", _) => $$"""
        ## Output Contract: Check Task
        Verify and repair the current implementation as requested by this task.
        If producing a review result, write it to {{PathFor(changeDir, "review.md")}} with a final <promise>PASS</promise> or <promise>FAIL</promise> marker.
        """,
        _ => "## Output Contract\nComplete the requested workflow task and write any required artifacts to the change directory when one is provided."
    };

    private static string PathFor(string? changeDir, string path) =>
        string.IsNullOrWhiteSpace(changeDir) ? path : Path.Combine(changeDir, path);

    public static string? ResolveModel(Dictionary<string, JsonElement?>? variables, string stage) =>
        ResolveModel(new VariableBag(variables), stage);

    private static string? ResolveModel(VariableBag variables, string stage) =>
        variables.String($"model.stage.{stage}")
        ?? variables.String("model.default");
}

public sealed record AgentPromptContext(
    string Stage,
    string Task,
    string? ChangeDir,
    string WorkDir,
    Dictionary<string, JsonElement?>? Variables);
