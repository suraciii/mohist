using System.Text.Json;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.WorkflowProfiles;
using Mohist.Server.Workflow.Infrastructure;
using Mohist.Server.Workflow.Prompts;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class FakePromptLoader : IPromptLoader
{
    public Dictionary<string, string> Prompts { get; set; } = new(StringComparer.Ordinal)
    {
        ["proposal"] = "# Proposal Artifact\nCreate proposal.md",
        ["specs"] = "# Specs Artifact\nCreate specs",
        ["design"] = "# Design Artifact\nCreate design.md",
        ["tasks"] = "# Tasks Artifact\nCreate tasks.json",
        ["self-review"] = "# Self Review\nReview artifacts",
        ["review"] = "# Review\nReview implementation",
        ["build"] = "# Build\nImplement task",
    };

    public string Load(string name) => Prompts.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException($"Prompt '{name}' not found");
    public Dictionary<string, string> LoadAll() => new(Prompts, StringComparer.Ordinal);
}

public class MohistDefaultWorkflowProfileSpecs
{
    [Fact]
    public void IssueWithNonAsciiTitle_BuildsIssueNumberBasedOpenSpecChangeVariables()
    {
        var profile = new MohistDefaultIssueWorkflowProfile(new FakePromptLoader());
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue-154",
            ProjectId = "project-1",
            Number = 154,
            Title = "支持中文标题 🚀",
        };

        var variables = profile.BuildVariables("wr-1", issue, new WorkflowProjectContext("project-1", "Mohist", "/repo", "main"));

        using var document = JsonDocument.Parse(variables);
        Assert.Equal("issue-154", document.RootElement.GetProperty("openspecChangeName").GetString());
        Assert.Equal("openspec/changes/issue-154", document.RootElement.GetProperty("openspecChangeDir").GetString());
        Assert.False(document.RootElement.TryGetProperty("artifacts", out _));
    }

    [Fact]
    public void IssueWithNonAsciiTitle_ProjectsIssueNumberBasedChangeDir()
    {
        var state = MohistDefaultWorkflowProjection.ProjectWorkflowState(
            154,
            "支持中文标题 🚀",
            "todo",
            null);

        Assert.Equal("openspec/changes/issue-154", state.ChangeDir);
    }

    [Fact]
    public void DefaultWorkflowDefinition_LoadsFromYaml()
    {
        var definition = MohistWorkflow.Definition;

        Assert.Equal(["plan", "build", "check", "integrate"], definition.Stages.Select(s => s.Stage).ToArray());
        Assert.True(definition.Stages[0].RequiresApproval);
        Assert.True(definition.Stages[2].RequiresApproval);

        var proposal = definition.Stages[0].Tasks[0];
        Assert.Equal("proposal", proposal.Id);
        Assert.Equal("mohist/acp-agent", proposal.Uses);
        Assert.Contains("proposal.md", JsonSerializer.Serialize(proposal.With));

        var build = definition.Stages[1];
        var loadTask = build.Tasks[0];
        Assert.Equal("load-tasks", loadTask.Id);
        Assert.Equal("mohist/openspec-tasks", loadTask.Uses);
        Assert.Contains("tasks.json", JsonSerializer.Serialize(loadTask.With));

        var merge = definition.Stages[3].Tasks.Single(t => t.Id == "integrate:merge");
        Assert.Equal("sequential", definition.Stages[3].LockBehavior);
        Assert.Equal(["project-integration"], definition.Stages[3].Resources);
        Assert.Equal("mohist/merge", merge.Uses);
        Assert.Contains("mo/issue-${{ issue.number }}", JsonSerializer.Serialize(merge.With));
    }

    [Fact]
    public void DefaultWorkflowDefinition_BuildStageTaskTemplateUsesAcpAgentWithPromptLoaderSpec()
    {
        var loadTask = MohistWorkflow.Definition.Stages[1].Tasks[0];
        var withJson = JsonSerializer.Serialize(loadTask.With);

        Assert.Equal("mohist/openspec-tasks", loadTask.Uses);
        Assert.Contains("\"uses\":\"mohist/acp-agent\"", withJson);
        Assert.Contains("\"prompt\":", withJson);
        Assert.Contains("\"uses\":\"mohist/openspec-task-prompt\"", withJson);
        Assert.Contains("${{ openspecChangeDir }}/tasks.json", withJson);
        Assert.Contains("\"items\":\"tasks\"", withJson);
        Assert.Contains("\"base\":\"${{ prompts.build }}\"", withJson);
    }

    [Fact]
    public void DefaultWorkflowDefinition_BuildStagePromptLoaderConfigExposesFileItemsAndBase()
    {
        var loadTask = MohistWorkflow.Definition.Stages[1].Tasks[0];
        var with = loadTask.With ?? throw new InvalidOperationException("load-tasks must have a with map");
        var taskElement = with["task"] ?? throw new InvalidOperationException("load-tasks with must contain 'task'");
        var taskTemplate = taskElement.GetProperty("with");
        var promptSpec = taskTemplate.GetProperty("prompt");
        var promptWith = promptSpec.GetProperty("with");

        Assert.Equal("mohist/openspec-task-prompt", promptSpec.GetProperty("uses").GetString());
        Assert.Contains("tasks.json", promptWith.GetProperty("file").GetString());
        Assert.Equal("tasks", promptWith.GetProperty("items").GetString());
        Assert.Equal("${{ prompts.build }}", promptWith.GetProperty("base").GetString());
    }

    [Fact]
    public void DefaultWorkflowDefinition_BuildStageRetainsExistingLoaderKeys()
    {
        var loadTask = MohistWorkflow.Definition.Stages[1].Tasks[0];
        var with = loadTask.With ?? throw new InvalidOperationException("load-tasks must have a with map");
        var pathElement = with["path"] ?? throw new InvalidOperationException("load-tasks with must contain 'path'");

        Assert.Equal("mohist/openspec-tasks", loadTask.Uses);
        Assert.Equal("load-tasks", loadTask.Id);
        Assert.Equal("${{ openspecChangeDir }}/tasks.json", pathElement.GetString());
    }

    [Fact]
    public void DefaultWorkflowDefinition_PlanCheckIntegrateStagesAreUnchanged()
    {
        var yaml = WorkflowYamlSerializer.ToYaml(MohistWorkflow.Definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        Assert.Equal(MohistWorkflow.Definition.Stages[0].Tasks.Select(t => t.Id), reparsed.Stages[0].Tasks.Select(t => t.Id));
        Assert.Equal(MohistWorkflow.Definition.Stages[0].Tasks.Select(t => t.Uses), reparsed.Stages[0].Tasks.Select(t => t.Uses));
        Assert.Equal(MohistWorkflow.Definition.Stages[2].Tasks.Select(t => t.Id), reparsed.Stages[2].Tasks.Select(t => t.Id));
        Assert.Equal(MohistWorkflow.Definition.Stages[3].Tasks.Select(t => t.Id), reparsed.Stages[3].Tasks.Select(t => t.Id));
        Assert.True(reparsed.Stages[0].RequiresApproval);
        Assert.True(reparsed.Stages[2].RequiresApproval);
    }

    [Fact]
    public void AgentConfig_MergesGlobalConfigIntoAgentVariable()
    {
        var profile = new MohistDefaultIssueWorkflowProfile(new FakePromptLoader());
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue-1",
            ProjectId = "project-1",
            Number = 1,
            Title = "Agent config",
        };

        var variables = profile.BuildVariables(
            "wr-1",
            issue,
            new WorkflowProjectContext("project-1", "Mohist", "/repo", "main"),
            new Dictionary<string, object?> { ["model"] = "openai/gpt-4o", ["probeTimeoutMs"] = 30000 });

        using var document = JsonDocument.Parse(variables);
        var agent = document.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-4o", agent.GetProperty("model").GetString());
        Assert.Equal(30000, agent.GetProperty("probeTimeoutMs").GetInt32());
    }

    [Fact]
    public void StageVariables_MergesStageOverrides()
    {
        var profile = new MohistDefaultIssueWorkflowProfile(new FakePromptLoader());
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue-1",
            ProjectId = "project-1",
            Number = 1,
            Title = "Stage vars",
        };

        var stageVariables = profile.BuildStageVariables(
            issue,
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["check"] = new() { ["model"] = "openai/o3" },
            });

        Assert.NotNull(stageVariables);
        Assert.True(stageVariables.ContainsKey("check"));
    }

    [Fact]
    public void BuildVariables_IncludesPromptsFromLoader()
    {
        var loader = new FakePromptLoader();
        var profile = new MohistDefaultIssueWorkflowProfile(loader);
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue-1",
            ProjectId = "project-1",
            Number = 1,
            Title = "Test",
        };

        var variables = profile.BuildVariables("wr-1", issue, new WorkflowProjectContext("project-1", "Mohist", "/repo", "main"));

        using var document = JsonDocument.Parse(variables);
        var prompts = document.RootElement.GetProperty("prompts");
        Assert.Equal("# Proposal Artifact\nCreate proposal.md", prompts.GetProperty("proposal").GetString());
        Assert.Equal("# Build\nImplement task", prompts.GetProperty("build").GetString());
        Assert.Equal(7, prompts.EnumerateObject().Count());
    }

    [Fact]
    public void DefaultPrompts_DefineWorkflowArtifactBoundaryForReviewAndAutoFix()
    {
        var files = new FakePromptFileStore("/prompts");
        files.Add("review.prompt", """
            Mohist workflow artifacts under `${{ openspecChangeDir }}/` are review context and evidence, not product deliverables by themselves.
            Do not fail solely because `${{ openspecChangeDir }}/proposal.md`, `design.md`, `tasks.json`, `self-review.md`, `review.md`, or delta specs exist.
            """);
        files.Add("auto-fix.prompt", """
            Do NOT remove Mohist workflow artifacts under `${{ openspecChangeDir }}/`.
            Workflow artifacts under `${{ openspecChangeDir }}/` are planning/review context, not product deliverables to delete during auto-fix.
            """);

        var loader = new FilePromptLoader("/prompts", files);
        var prompts = loader.LoadAll();

        Assert.Contains("workflow artifacts", prompts["review"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not product deliverables", prompts["review"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("${{ openspecChangeDir }}/proposal.md", prompts["review"], StringComparison.Ordinal);
        Assert.Contains("do not remove mohist workflow artifacts", prompts["auto-fix"].ToLowerInvariant());
        Assert.Contains("${{ openspecChangeDir }}/", prompts["auto-fix"], StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowYamlParser_ParsesRepairTasksAndWithObjects()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: build
            tasks: []
            checks:
              - name: health
                title: Health
                uses: core/script
                with:
                  run: git diff --check
                  timeout: 300000
                repairLimit: 1
                repairTask:
                  id: fix-health
                  title: Fix health
                  uses: mohist/acp-agent
                  with:
                    prompt: Fix it
                verifyTask:
                  id: verify-health
                  title: Verify health
                  uses: core/script
                  with:
                    run: git diff --check
        """);

        var check = definition.Stages.Single().Checks.Single();
        Assert.Equal("core/script", check.Uses);
        Assert.Equal(1, check.OnFailure?.Repair?.Limit);
        Assert.Equal("fix-health", check.OnFailure?.Repair?.Task.Id);
        Assert.Equal("verify-health", check.OnFailure?.Repair?.VerifyTask?.Id);
        Assert.Contains("\"timeout\":300000", JsonSerializer.Serialize(check.With));
        Assert.Contains("\"prompt\":\"Fix it\"", JsonSerializer.Serialize(check.OnFailure?.Repair?.Task.With));
        Assert.Contains("\"run\":\"git diff --check\"", JsonSerializer.Serialize(check.OnFailure?.Repair?.VerifyTask?.With));
    }

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsDomainDefinition()
    {
        var yaml = WorkflowYamlSerializer.ToYaml(MohistWorkflow.Definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        Assert.Equal(MohistWorkflow.Definition.Stages.Select(s => s.Stage), reparsed.Stages.Select(s => s.Stage));
        Assert.Contains("agent: ${{ vars.agent }}", yaml);
        Assert.Contains("prompt: ${{ prompts.proposal }}", yaml);
        Assert.Contains("repairTask:", yaml);
        Assert.Contains("verifyTask:", yaml);
        Assert.Contains("id: fix-review-findings", yaml);
        Assert.Contains("prompt: ${{ prompts.auto-fix }}", yaml);
        Assert.Equal("mohist/openspec-tasks", reparsed.Stages[1].Tasks[0].Uses);
        var reviewRepair = reparsed.Stages[2].Checks.Single(c => c.Name == "review-passed").OnFailure?.Repair;
        Assert.Equal(2, reviewRepair?.Limit);
        Assert.Equal("fix-review-findings", reviewRepair?.Task.Id);
        Assert.Equal("ai-review", reviewRepair?.VerifyTask?.Id);
        Assert.Contains("\"expect\"", JsonSerializer.Serialize(reviewRepair?.VerifyTask?.With));
        Assert.DoesNotContain("\"expect\"", JsonSerializer.Serialize(reviewRepair?.Task.With));
    }

    [Fact]
    public void WorkflowYamlParser_TaskWithNeutralArtifactMarker_ParsesSuccessfully()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: build
            tasks:
              - id: doc-task
                title: Document task
                uses: mohist/acp-agent
                with:
                  prompt: Write docs
                  expect:
                    files:
                      - path: docs/readme.md
                    markers:
                      - path: docs/readme.md
                        contains: "## Getting Started"
            checks: []
        """);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.Equal("doc-task", task.Id);
    }

    [Theory]
    [InlineData("PASS")]
    [InlineData("FAIL")]
    [InlineData("<promise>PASS</promise>")]
    [InlineData("<promise>FAIL</promise>")]
    public void WorkflowYamlParser_TaskWithVerdictMarkerInExpect_ThrowsSchemaDiagnostic(string marker)
    {
        var yaml = $"""
        stages:
          - stage: build
            tasks:
              - id: bad-task
                title: Bad task
                uses: mohist/acp-agent
                with:
                  prompt: Do work
                  expect:
                    files:
                      - path: result.md
                    markers:
                      - path: result.md
                        contains: {marker}
            checks: []
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => MohistWorkflow.ParseYaml(yaml));
        Assert.Contains("verdict marker", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("check definition", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bad-task", ex.Message);
    }

    [Fact]
    public void DefaultWorkflowDefinition_HasNoTaskVerdictMarkers()
    {
        var definition = MohistWorkflow.Definition;

        foreach (var stage in definition.Stages)
        {
            foreach (var task in stage.Tasks)
            {
                var withJson = JsonSerializer.Serialize(task.With);
                Assert.DoesNotContain("\"PASS\"", withJson);
                Assert.DoesNotContain("\"FAIL\"", withJson);
            }
        }
    }
}

internal sealed class FakePromptFileStore : IPromptFileStore
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public FakePromptFileStore(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public void Add(string name, string content) => _files[Path.Combine(Root, name)] = content;

    public bool DirectoryExists(string path) => path == Root;

    public IEnumerable<string> EnumeratePromptFiles(string path) =>
        path == Root ? _files.Keys.Where(k => k.EndsWith(".prompt", StringComparison.Ordinal)).Order() : [];

    public string ReadAllText(string path) => _files[path];
}
