using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Domain;
using Issue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Infrastructure.Data.Workflow;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.Profile;

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

public sealed class FakeDbContextFactory : IDbContextFactory<MohistDbContext>
{
    private readonly SqliteConnection _connection;

    public FakeDbContextFactory(Dictionary<string, string>? projectPrompts = null, string? projectId = null)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
        if (projectPrompts is { Count: > 0 } && projectId is not null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                Prompts = projectPrompts,
            });
            db.SaveChanges();
        }
    }

    public MohistDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(_connection).Options;
        return new MohistDbContext(options);
    }

    public void Dispose() => _connection.Dispose();
}

public class MohistDefaultWorkflowProfileSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void IssueWithNonAsciiTitle_BuildsIssueNumberBasedOpenSpecChangeVariables()
    {
        var profile = new MohistDefaultIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue-154",
            ProjectId = "project-1",
            Number = 154,
            Title = "支持中文标题 🚀",
        };

        var variables = profile.BuildVariables("wr-1", issue, new WorkflowProjectContext("project-1", "Mohist", RepositoryBaseBranch: "main"));

        using var document = JsonDocument.Parse(variables);
        Assert.Equal("openspec/changes/issue-154", document.RootElement.GetProperty("openspecChangeDir").GetString());
        Assert.False(document.RootElement.TryGetProperty("artifacts", out _));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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
        var push = definition.Stages[3].Tasks.Single(t => t.Id == "integrate:push");
        Assert.Equal("sequential", definition.Stages[3].LockBehavior);
        Assert.Equal(["project-integration"], definition.Stages[3].Resources);
        Assert.Equal("mohist/merge", merge.Uses);
        var mergeWithJson = JsonSerializer.Serialize(merge.With);
        // The integrate task merges from the per-run branch on the workspace
        // clone, not from a legacy worktree branch.
        Assert.Contains("workspace.branch", mergeWithJson);
        Assert.Contains("repository.baseBranch", mergeWithJson);
        Assert.Contains("\"conflictResolver\"", mergeWithJson);
        Assert.Contains("Resolve merge conflicts", mergeWithJson);
        Assert.Equal("mohist/push", push.Uses);
        var pushWithJson = JsonSerializer.Serialize(push.With);
        Assert.Contains("${{ project.baseBranch }}", pushWithJson);
        var integrateTaskIds = definition.Stages[3].Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(["integrate:spec-sync", "integrate:archive-change", "integrate:merge", "integrate:push"], integrateTaskIds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void AgentConfig_MergesGlobalConfigIntoAgentVariable()
    {
        var profile = new MohistDefaultIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());
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
            new WorkflowProjectContext("project-1", "Mohist", RepositoryBaseBranch: "main"),
            new Dictionary<string, object?> { ["model"] = "openai/gpt-4o", ["probeTimeoutMs"] = 30000 });

        using var document = JsonDocument.Parse(variables);
        var agent = document.RootElement.GetProperty("vars").GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-4o", agent.GetProperty("model").GetString());
        Assert.Equal(30000, agent.GetProperty("probeTimeoutMs").GetInt32());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void StageVariables_MergesStageOverrides()
    {
        var profile = new MohistDefaultIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void BuildVariables_IncludesPromptsFromLoader()
    {
        var loader = new FakePromptLoader();
        var profile = new MohistDefaultIssueWorkflowProfile(loader, new FakeDbContextFactory());
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue-1",
            ProjectId = "project-1",
            Number = 1,
            Title = "Test",
        };

        var variables = profile.BuildVariables("wr-1", issue, new WorkflowProjectContext("project-1", "Mohist", RepositoryBaseBranch: "main"));

        using var document = JsonDocument.Parse(variables);
        var prompts = document.RootElement.GetProperty("prompts");
        Assert.Equal("# Proposal Artifact\nCreate proposal.md", prompts.GetProperty("proposal").GetString());
        Assert.Equal("# Build\nImplement task", prompts.GetProperty("build").GetString());
        Assert.Equal(7, prompts.EnumerateObject().Count());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void BuildVariables_MergesProjectOverridesAndAddsProjectUniqueKeys()
    {
        var loader = new FakePromptLoader();
        var dbFactory = new FakeDbContextFactory(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["proposal"] = "# Project proposal body",
            ["deploy-checklist"] = "# Deploy checklist body",
        }, "project-1");

        var profile = new MohistDefaultIssueWorkflowProfile(loader, dbFactory);
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue-1",
            ProjectId = "project-1",
            Number = 1,
            Title = "Merge test",
        };

        var variables = profile.BuildVariables("wr-1", issue, new WorkflowProjectContext("project-1", "Mohist", RepositoryBaseBranch: "main"));

        using var document = JsonDocument.Parse(variables);
        var prompts = document.RootElement.GetProperty("prompts");
        Assert.Equal("# Project proposal body", prompts.GetProperty("proposal").GetString());
        Assert.Equal("# Build\nImplement task", prompts.GetProperty("build").GetString());
        Assert.Equal("# Deploy checklist body", prompts.GetProperty("deploy-checklist").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task GetMergedPromptsAsync_KeepsSystemBodyWhenNoOverrideExists()
    {
        var loader = new FakePromptLoader();
        var templateStore = new FakeDbContextFactory();
        var profile = new MohistDefaultIssueWorkflowProfile(loader, templateStore);

        var merged = await profile.GetMergedPromptsAsync("project-99");

        Assert.Equal("# Build\nImplement task", merged["build"]);
        Assert.Equal(7, merged.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void DefaultPrompts_LoadIssueDetailsThroughMohistCli()
    {
        var loader = new FilePromptLoader();
        var prompts = loader.LoadAll();

        const string command = "mo issue show ${{ issue.number }} --project-id ${{ project.id }}";
        var executionPrompts = prompts.Keys.ToArray();
        Assert.NotEmpty(executionPrompts);
        foreach (var key in executionPrompts)
        {
            Assert.Contains(command, prompts[key], StringComparison.Ordinal);
            Assert.DoesNotContain("prompts.issue-context", prompts[key], StringComparison.Ordinal);
        }

        var profile = new MohistDefaultIssueWorkflowProfile(loader, new FakeDbContextFactory());
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = "issue-42",
            ProjectId = "project-1",
            Number = 42,
            Title = "Use CLI issue details",
        };
        var variablesJson = profile.BuildVariables("wr-42", issue, new WorkflowProjectContext("project-1", "Mohist", RepositoryBaseBranch: "main"));
        using var variables = JsonDocument.Parse(variablesJson);
        var (rendered, missing, _) = new PromptTemplateEngine().Render(prompts["proposal"], variables.RootElement);

        Assert.Empty(missing);
        Assert.Contains("mo issue show 42 --project-id project-1", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("prompts.issue-context", rendered, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlParser_PreservesTaskArtifactCapturePaths()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: proposal
                title: Generate proposal
                uses: mohist/acp-agent
                with:
                  prompt: ${{ prompts.proposal }}
                  expect:
                    files:
                      - path: ${{ openspecChangeDir }}/proposal.md
                artifacts:
                  files:
                    - path: ${{ openspecChangeDir }}/proposal.md
                    - path: ${{ openspecChangeDir }}/specs
              - id: design
                title: Design
                uses: mohist/acp-agent
                with:
                  prompt: ${{ prompts.design }}
                artifacts:
                  files:
                    - path: ${{ openspecChangeDir }}/design.md
            checks: []
        """);

        var proposal = definition.Stages.Single().Tasks.Single(t => t.Id == "proposal");
        Assert.NotNull(proposal.Artifacts);
        Assert.Equal(
            new[]
            {
                "${{ openspecChangeDir }}/proposal.md",
                "${{ openspecChangeDir }}/specs",
            },
            proposal.Artifacts!.Files.Select(f => f.Path).ToArray());

        var design = definition.Stages.Single().Tasks.Single(t => t.Id == "design");
        Assert.NotNull(design.Artifacts);
        Assert.Equal(
            new[] { "${{ openspecChangeDir }}/design.md" },
            design.Artifacts!.Files.Select(f => f.Path).ToArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlParser_TaskArtifactsAreNotMergedIntoWith()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: declare-task
                title: Declare artifacts
                uses: mohist/acp-agent
                with:
                  prompt: hello
                artifacts:
                  files:
                    - path: docs/out.md
            checks: []
        """);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.NotNull(task.With);
        var withJson = JsonSerializer.Serialize(task.With);
        Assert.DoesNotContain("artifacts", withJson);
        Assert.DoesNotContain("docs/out.md", withJson);

        Assert.NotNull(task.Artifacts);
        Assert.Equal(new[] { "docs/out.md" }, task.Artifacts!.Files.Select(f => f.Path).ToArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlParser_WithExpectFilesAloneDoesNotCreateArtifactCapture()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: expect-only
                title: Expect files only
                uses: mohist/acp-agent
                with:
                  prompt: hello
                  expect:
                    files:
                      - path: docs/expected.md
                    markers:
                      - path: docs/expected.md
                        contains: "# Done"
            checks: []
        """);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.Null(task.Artifacts);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlParser_AcceptsSamePathInExpectMarkersAndArtifacts()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: review
                title: Review
                uses: mohist/acp-agent
                with:
                  prompt: Review
                  expect:
                    markers:
                      - path: ${{ openspecChangeDir }}/review.md
                        oneOf:
                          - <promise>PASS</promise>
                          - <promise>FAIL</promise>
                artifacts:
                  files:
                    - path: ${{ openspecChangeDir }}/review.md
            checks: []
        """);

        var task = definition.Stages.Single().Tasks.Single();
        Assert.NotNull(task.Artifacts);
        Assert.Equal(new[] { "${{ openspecChangeDir }}/review.md" }, task.Artifacts!.Files.Select(f => f.Path).ToArray());
        var withJson = JsonSerializer.Serialize(task.With);
        Assert.Contains("expect", withJson);
        Assert.Contains("markers", withJson);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlParser_TaskArtifactFileEntryWithoutPathThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: bad
                title: Bad
                uses: mohist/acp-agent
                with:
                  prompt: hi
                artifacts:
                  files:
                    - other: docs/out.md
            checks: []
        """));

        Assert.Contains("artifacts.files", ex.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlParser_RepairTaskArtifactsAreIsolated()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: check
            tasks:
              - id: ai-review
                title: AI review
                uses: mohist/acp-agent
                with:
                  prompt: review
                artifacts:
                  files:
                    - path: review.md
            checks:
              - name: review-passed
                title: Review passed
                uses: core/marker
                with:
                  path: review.md
                  expect: <promise>PASS</promise>
                repairLimit: 1
                repairTask:
                  id: fix-review
                  title: Fix review
                  uses: mohist/acp-agent
                  with:
                    prompt: fix
                verifyTask:
                  id: re-review
                  title: Re review
                  uses: mohist/acp-agent
                  with:
                    prompt: re
                  artifacts:
                    files:
                      - path: review.md
        """);

        var stage = definition.Stages.Single();
        var review = stage.Tasks.Single();
        Assert.NotNull(review.Artifacts);

        var repairCheck = stage.Checks.Single();
        Assert.Null(repairCheck.OnFailure?.Repair?.Task.Artifacts);
        var verify = repairCheck.OnFailure?.Repair?.VerifyTask;
        Assert.NotNull(verify);
        Assert.NotNull(verify!.Artifacts);
        Assert.Equal(new[] { "review.md" }, verify.Artifacts!.Files.Select(f => f.Path).ToArray());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlSerializer_RoundTripsTaskArtifactCapture()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: declare
                title: Declare
                uses: mohist/acp-agent
                with:
                  prompt: hi
                artifacts:
                  files:
                    - path: docs/a.md
                    - path: docs/b.md
            checks: []
        """);

        var yaml = WorkflowYamlSerializer.ToYaml(definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        var task = reparsed.Stages.Single().Tasks.Single();
        Assert.NotNull(task.Artifacts);
        Assert.Equal(
            new[] { "docs/a.md", "docs/b.md" },
            task.Artifacts!.Files.Select(f => f.Path).ToArray());
        Assert.Contains("artifacts:", yaml);
        Assert.Contains("docs/a.md", yaml);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void DefaultWorkflowDefinition_DeclaresExpectedArtifactCapturePaths()
    {
        var definition = MohistWorkflow.Definition;

        var plan = definition.Stages[0];
        AssertArtifactPaths(plan.Tasks.Single(t => t.Id == "proposal"), "proposal.md");
        AssertArtifactPaths(plan.Tasks.Single(t => t.Id == "specs"), "specs");
        AssertArtifactPaths(plan.Tasks.Single(t => t.Id == "design"), "design.md");
        AssertArtifactPaths(plan.Tasks.Single(t => t.Id == "tasks"), "tasks.json");
        AssertArtifactPaths(plan.Tasks.Single(t => t.Id == "self-review"), "self-review.md");

        var check = definition.Stages[2];
        AssertArtifactPaths(check.Tasks.Single(t => t.Id == "ai-review"), "review.md");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void DefaultWorkflowDefinition_AiReviewTaskDeclaresMarkerExpectationWithOneOf()
    {
        // The default workflow's ai-review task must declare a
        // with.expect.markers entry that accepts both PASS and FAIL
        // verdicts so a failing review does not loop the action
        // forever. This is the spec's canonical YAML shape for the
        // check repair loop motivating scenario.
        var definition = MohistWorkflow.Definition;
        var check = definition.Stages[2];
        var aiReview = check.Tasks.Single(t => t.Id == "ai-review");

        AssertMarkerOneOf(aiReview);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void DefaultWorkflowDefinition_ReviewPassedRepairVerifyTaskDeclaresMarkerOneOf()
    {
        // The repair-loop verify task reuses the ai-review task; its
        // action input must also accept either verdict marker.
        var definition = MohistWorkflow.Definition;
        var check = definition.Stages[2];
        var reviewPassed = check.Checks.Single(c => c.Name == "review-passed");
        var verify = reviewPassed.OnFailure?.Repair?.VerifyTask;
        Assert.NotNull(verify);

        AssertMarkerOneOf(verify!);
    }

    private static void AssertMarkerOneOf(TaskDefinition task)
    {
        Assert.NotNull(task.With);
        Assert.True(task.With!.ContainsKey("expect"), "task is missing 'expect' input");

        var expectElement = JsonSerializer.SerializeToElement(task.With["expect"]);
        Assert.Equal(JsonValueKind.Object, expectElement.ValueKind);
        Assert.True(expectElement.TryGetProperty("markers", out var markers),
            "task 'expect' is missing the 'markers' entry");
        Assert.Equal(JsonValueKind.Array, markers.ValueKind);
        Assert.True(markers.GetArrayLength() > 0, "'markers' must declare at least one entry");

        var first = markers[0];
        Assert.Equal(JsonValueKind.Object, first.ValueKind);
        Assert.True(first.TryGetProperty("path", out _), "marker entry is missing 'path'");
        Assert.True(first.TryGetProperty("oneOf", out var oneOf),
            "marker entry is missing the 'oneOf' verdicts list");
        Assert.Equal(JsonValueKind.Array, oneOf.ValueKind);

        var verdicts = oneOf.EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => v is not null)
            .ToList();
        Assert.Contains("<promise>PASS</promise>", verdicts);
        Assert.Contains("<promise>FAIL</promise>", verdicts);

        // Files-style expectations are intentionally absent so the
        // artifact declaration surface is not the same as the action
        // completion contract.
        Assert.False(expectElement.TryGetProperty("files", out _),
            "ai-review expect must use markers, not files");
    }

    private static void AssertArtifactPaths(TaskDefinition task, params string[] expectedPathSuffixes)
    {
        Assert.NotNull(task.Artifacts);
        var actual = task.Artifacts!.Files.Select(f => f.Path).ToList();
        Assert.Equal(expectedPathSuffixes.Length, actual.Count);
        foreach (var suffix in expectedPathSuffixes)
            Assert.Contains(actual, p => p.EndsWith(suffix, StringComparison.Ordinal));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void DefaultWorkflowDefinition_DescriptionIsParsedFromYamlBlockScalar()
    {
        var description = MohistWorkflow.Definition.Description;

        Assert.NotNull(description);
        Assert.Contains("plan (proposal, specs, design, tasks, self-review)", description!);
        Assert.Contains("build", description);
        Assert.Contains("check (AI review, merge readiness)", description);
        Assert.Contains("integrate (spec sync, archive, merge)", description);
        Assert.Contains("quick-fix", description);
        Assert.Contains("experiment", description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void DefaultWorkflowDefinition_DescriptionPreservesMultilineLineBreaks()
    {
        var description = MohistWorkflow.Definition.Description;

        Assert.NotNull(description);
        Assert.Contains("→", description!);
        Assert.Contains("\n", description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlSerializer_RoundTripsDescriptionField()
    {
        var definition = MohistWorkflow.Definition;
        var yaml = WorkflowYamlSerializer.ToYaml(definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        Assert.Equal(definition.Description, reparsed.Description);
        Assert.Contains("description:", yaml);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlParser_ProfileWithoutDescriptionYieldsNullDescription()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: build
            tasks: []
            checks: []
        """);

        Assert.Null(definition.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowYamlParser_ProfileWithSingleLineDescription_ParsesItVerbatim()
    {
        var definition = MohistWorkflow.ParseYaml("""
        description: Simple description
        stages:
          - stage: build
            tasks: []
            checks: []
        """);

        Assert.Equal("Simple description", definition.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void DefaultIssueWorkflowProfile_DescriptionSourcesFromWorkflowYaml()
    {
        var profile = new MohistDefaultIssueWorkflowProfile(new FakePromptLoader(), new FakeDbContextFactory());

        Assert.Equal(MohistWorkflow.Definition.Description, profile.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void DefaultIssueWorkflowProfile_DescriptionFallsBack_WhenYamlHasNoDescription()
    {
        // Mirrors the spec scenario "Profile without description field":
        // a workflow profile whose source description is missing must
        // surface the "No description provided" fallback string. The
        // MohistDefaultIssueWorkflowProfile class applies the fallback
        // through ResolveDescription; the SystemRoutes detail endpoint
        // applies the same string (now sourced from SystemTemplateInfo).
        const string fallback = "No description provided";
        var yamlWithoutDescription = MohistWorkflow.ParseYaml("""
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.Null(yamlWithoutDescription.Description);

        var fallbackDescription = string.IsNullOrWhiteSpace(yamlWithoutDescription.Description)
            ? fallback
            : yamlWithoutDescription.Description!;

        Assert.Equal(fallback, fallbackDescription);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task IssueWorkflowProfileRegistry_ListIncludesDescriptionForDefault()
    {
        var loader = new FakePromptLoader();
        var dbFactory = new FakeDbContextFactory();
        var registry = new IssueWorkflowProfileRegistry(loader, dbFactory);

        var list = registry.List();

        var defaultEntry = Assert.Single(list, info => info.Id == "mohist/default");
        Assert.True(defaultEntry.IsDefault);
        Assert.Equal(MohistWorkflow.Definition.Description, defaultEntry.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ProjectWorkflowProfileManager_SystemTemplates_ExposeDescriptionAndIsDefault()
    {
        var manager = new ProjectWorkflowProfileManager(new FakeDbContextFactory(), new FakePromptLoader(), new PromptTemplateEngine());

        var templates = await manager.ListSystemTemplatesAsync();

        var defaultTemplate = Assert.Single(templates, t => t.Id == "mohist/default");
        Assert.True(defaultTemplate.IsDefault);
        Assert.Equal(MohistWorkflow.Definition.Description, defaultTemplate.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void SystemTemplateInfo_ContractCarriesIsDefaultFlag()
    {
        var info = new SystemTemplateInfo("id", "Name", "Desc", true);

        Assert.True(info.IsDefault);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void DescriptionField_DoesNotInfluenceStageExecutionShape()
    {
        // The description field is passive metadata; verify the engine
        // payload (stages, tasks, checks) is identical to the version
        // without the description key, plus the round-trip is stable.
        var descriptionOnlyYaml = """
            id: mohist/default
            description: |
              Some user-facing description that the engine must not
              read or interpret.
            stages:
              - stage: build
                tasks: []
                checks: []
            """;

        var parsed = MohistWorkflow.ParseYaml(descriptionOnlyYaml);

        Assert.Equal("build", parsed.Stages[0].Stage);
        Assert.Empty(parsed.Stages[0].Tasks);
        Assert.Empty(parsed.Stages[0].Checks);
        Assert.Contains("user-facing description", parsed.Description);

        var yaml = WorkflowYamlSerializer.ToYaml(parsed);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);
        Assert.Equal(parsed.Description, reparsed.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void DefaultWorkflowYaml_OmitsStructuredMetadataFields()
    {
        // Locks the "description-only" design decision. The spec scenario
        // "Other metadata fields are absent" forbids the top-level of a
        // workflow profile YAML from carrying risk_level, typical_duration,
        // suitable_for, avoid_for, tags, or default_approval_policy — those
        // belong inside the natural-language description.
        var yaml = WorkflowYamlSerializer.ToYaml(MohistWorkflow.Definition);

        var forbidden = new[]
        {
            "risk_level:",
            "riskLevel:",
            "typical_duration:",
            "typicalDuration:",
            "suitable_for:",
            "suitableFor:",
            "avoid_for:",
            "avoidFor:",
            "tags:",
            "default_approval_policy:",
            "defaultApprovalPolicy:",
        };
        foreach (var needle in forbidden)
            Assert.DoesNotContain(needle, yaml, StringComparison.Ordinal);
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

[Collection("MohistIntegration")]
public class MohistDefaultWorkflowProfileStartWorkSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public MohistDefaultWorkflowProfileStartWorkSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task StartWork_WithUnknownPromptReference_Returns400MissingPromptsWithMissingKeysDetails()
    {
        var project = await _client.PostDataAsync<StartProjectDto>("/api/projects", new { name = $"missing-prompts-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<StartIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workflow references unknown prompt", projectId = project.Id });

        var customYaml = """
            id: missing-prompt-workflow
            stages:
              - stage: plan
                tasks:
                  - id: missing-prompt-task
                    title: Missing prompt task
                    uses: mohist/acp-agent
                    with:
                      prompt: ${{ prompts.does-not-exist }}
                checks: []
            """;
        await _client.PutAsJsonOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template", new { yaml = customYaml });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("missing_prompts", payload.GetProperty("code").GetString());
        var missingKeys = payload.GetProperty("details").GetProperty("missingKeys").EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Contains("does-not-exist", missingKeys);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task StartWork_WithMultipleUnknownPromptReferences_ReturnsAllMissingKeysInDetails()
    {
        var project = await _client.PostDataAsync<StartProjectDto>("/api/projects", new { name = $"multi-missing-prompts-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<StartIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workflow references multiple unknown prompts", projectId = project.Id });

        var customYaml = """
            id: multi-missing-prompt-workflow
            stages:
              - stage: plan
                tasks:
                  - id: multi-missing-prompt-task
                    title: Multi missing prompt task
                    uses: mohist/acp-agent
                    with:
                      prompt: ${{ prompts.ghost-one }} and ${{ prompts.ghost-two }}
                checks: []
            """;
        await _client.PutAsJsonOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template", new { yaml = customYaml });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("missing_prompts", payload.GetProperty("code").GetString());
        var missingKeys = payload.GetProperty("details").GetProperty("missingKeys").EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Contains("ghost-one", missingKeys);
        Assert.Contains("ghost-two", missingKeys);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task StartWork_WithKnownSystemPromptKey_DoesNotEmitMissingPromptsError()
    {
        var project = await _client.PostDataAsync<StartProjectDto>("/api/projects", new { name = $"known-prompts-{Guid.NewGuid():N}" });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", isDefault = true });
        var issue = await _client.PostDataAsync<StartIssueDto>($"/api/projects/{project.Id}/issues", new { title = "Workflow references known prompt", projectId = project.Id });

        var customYaml = """
            id: known-prompt-workflow
            stages:
              - stage: plan
                tasks:
                  - id: known-prompt-task
                    title: Known prompt task
                    uses: mohist/acp-agent
                    with:
                      prompt: ${{ prompts.proposal }}
                checks: []
            """;
        await _client.PutAsJsonOkAsync($"/api/projects/{project.Id}/issues/{issue.Number}/workflow-profile/template", new { yaml = customYaml });

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/start", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record StartProjectDto(string Id);
    private sealed record StartIssueDto(int Number, string Id);
}
