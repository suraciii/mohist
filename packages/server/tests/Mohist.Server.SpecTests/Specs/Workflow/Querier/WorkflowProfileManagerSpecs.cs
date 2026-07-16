using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Infrastructure.Data.Workflow;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.SpecTests.Specs.Workflow.Querier;

public class WorkflowProfileManagerSpecs : IAsyncLifetime
{
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly WorkflowProfileManager _manager;
    private readonly SqliteConnection _keeper;

    public WorkflowProfileManagerSpecs()
    {
        var connectionString = $"Data Source=profile-specs-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;

        var factory = new TestDbContextFactory(_options);
        var runProfileManager = new WorkflowRunProfileManager(factory);
        var promptLoader = new Mohist.Server.Workflow.Services.Prompts.FilePromptLoader();
        _manager = new WorkflowProfileManager(
            factory,
            promptLoader,
            new PromptTemplateEngine(),
            WorkflowGrainTestHelpers.CreateEmptyConfigService(),
            runProfileManager);

        MigratedSqliteTemplate.CopyTo(_keeper);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LoadTemplate_FallsBackToSystemDefault_WhenRunContextMissing()
    {
        var result = await _manager.LoadTemplateAsync("unknown-run-id");

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/local", result.Id ?? "");
    }

    [Fact]
    public async Task LoadTemplate_UsesIssueCustomWithoutRunProfileBinding()
    {
        var runId = "wr_snap01";
        await SeedAsync(projectId: "proj1", issueNumber: 1, runId: runId,
            issueTemplateJson: SerializeDefinition("issue-custom", stageCount: 2),
            projectTemplateJson: SerializeDefinition("project-tmpl", stageCount: 3));

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("issue-custom", result.Id ?? "");
        Assert.Equal(2, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_Priority2_ReturnsIssueCustomTemplate()
    {
        var runId = "wr_issue01";
        await SeedAsync(projectId: "proj2", issueNumber: 1, runId: runId,
            issueTemplateJson: SerializeDefinition("issue-custom", stageCount: 2),
            projectTemplateJson: SerializeDefinition("project-tmpl", stageCount: 3));

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("issue-custom", result.Id ?? "");
        Assert.Equal(2, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_Priority3_ReturnsIssueReferencedTemplate()
    {
        var runId = "wr_ref01";
        await SeedAsync(projectId: "proj3", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: "my-tmpl",
            projectTemplateJson: SerializeDefinition("my-tmpl", stageCount: 4));

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("project-template", result.Id ?? "");
        Assert.Equal(4, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_ProjectDefaultCustomTemplate_NoIssueSelection_UsesProjectDefault()
    {
        var runId = "wr_default01";
        await SeedAsync(projectId: "proj4", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "default-tmpl",
            projectTemplateJson: SerializeDefinition("default-tmpl", stageCount: 5));

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("project-template", result.Id ?? "");
        Assert.Equal(5, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_ProjectDefaultSystemTemplate_FallsBackToSystemTemplate()
    {
        var runId = "wr_system_default01";
        await SeedAsync(projectId: "proj-sys", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "mohist/local");

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/local", result.Id ?? "");
        Assert.Contains(result.Structure.Stages, s => s.Stage == "plan");
    }

    [Fact]
    public async Task LoadTemplate_DisabledProjectDefaultSystemTemplate_UsesFirstEnabledProfile()
    {
        var runId = "wr_disabled_default01";
        await SeedWithoutRunAsync(projectId: "proj-disabled-default", issueNumber: 1,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local"]);

        var result = await _manager.LoadTemplateAsync(runId, "proj-disabled-default", 1);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/github-pr", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "merge-pr");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:rebase");
    }

    [Fact]
    public async Task LoadTemplate_WhenAllProfilesDisabled_ThrowsActionableErrorInsteadOfFallingBackToLocal()
    {
        var runId = "wr_all_disabled_template";
        await SeedWithoutRunAsync(projectId: "proj-all-disabled-template", issueNumber: 1,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.LoadTemplateAsync(runId, "proj-all-disabled-template", 1));

        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadTemplate_WhenAllProfilesDisabled_ThrowsBeforeIssueCustomTemplate()
    {
        var runId = "wr_all_disabled_custom_template";
        await SeedWithoutRunAsync(projectId: "proj-all-disabled-custom-template", issueNumber: 1,
            issueTemplateJson: SerializeDefinition("issue-custom-disabled", stageCount: 1),
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.LoadTemplateAsync(runId, "proj-all-disabled-custom-template", 1));

        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadTemplate_ExistingRunIgnoresLaterDisabledProfiles()
    {
        var runId = "wr_existing_disabled_template";
        await SeedAsync(projectId: "proj-existing-disabled-template", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/local", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:rebase");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "merge-pr");
    }

    [Fact]
    public async Task LoadTemplate_IssuePrProfile_NoOverrides_UsesPrSystemTemplate()
    {
        var runId = "wr_issue_pr";
        await SeedAsync(projectId: "proj-issue-pr", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/github-pr");

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/github-pr", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        var mergePr = Assert.Single(integrate.Tasks, t => t.Id == "merge-pr");
        Assert.Equal("mohist/merge-github-pr", mergePr.Uses);
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:rebase");
    }

    [Fact]
    public async Task LoadTemplate_IssueDefaultProfile_NoOverrides_UsesDefaultSystemTemplate()
    {
        var runId = "wr_issue_default";
        await SeedAsync(projectId: "proj-issue-default", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local");

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/local", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:rebase");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:open-pr");
    }

    [Fact]
    public async Task LoadTemplate_IssuePrProfile_ProjectDefaultIsDifferent_UsesIssueProfile()
    {
        var runId = "wr_issue_pr_proj_default";
        await SeedAsync(projectId: "proj-issue-pr-proj-default", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "mohist/local",
            issueWorkflowProfileId: "mohist/github-pr");

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/github-pr", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        var mergePr = Assert.Single(integrate.Tasks, t => t.Id == "merge-pr");
        Assert.Equal("mohist/merge-github-pr", mergePr.Uses);
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:rebase");
    }

    [Fact]
    public async Task LoadTemplate_IssuePrProfile_CustomYamlOverride_TakesPrecedence()
    {
        var runId = "wr_issue_pr_custom";
        await SeedAsync(projectId: "proj-pr-custom", issueNumber: 1, runId: runId,
            issueTemplateJson: SerializeDefinition("custom-override", stageCount: 1),
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/github-pr");

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("issue-custom", result.Id ?? "");
        Assert.Single(result.Structure.Stages);
    }

    [Fact]
    public async Task ResolveLayeredVariables_ReturnsEmpty_WhenNoProfileVariablesExist()
    {
        var runId = "wr_vars01";
        await SeedAsync(
            projectId: "proj5",
            issueNumber: 1,
            runId: runId,
            issueTemplateJson: SerializeDefinition("empty-vars-template"));

        var result = await _manager.ResolveLayeredVariablesAsync(runId);

        Assert.False(result.Vars.HasValue);
        Assert.Null(result.Stages);
    }

    [Fact]
    public async Task ResolveLayeredVariables_MergesProjectDefaultsWithIssueOverrides()
    {
        var runId = "wr_direct01";
        var proj = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { a = 1, b = "proj-b", c = "proj-c" })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { b = "issue-b", c = "issue-c", d = "issue-d" })));

        await SeedAllLayersAsync("proj6", 1, runId, proj, issue);

        var result = await _manager.ResolveLayeredVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        Assert.Equal(1, doc.RootElement.GetProperty("a").GetInt32());
        Assert.Equal("issue-b", doc.RootElement.GetProperty("b").GetString());
        Assert.Equal("issue-c", doc.RootElement.GetProperty("c").GetString());
        Assert.Equal("issue-d", doc.RootElement.GetProperty("d").GetString());
    }

    [Fact]
    public async Task ResolveLayeredVariables_ProjectEditsAffectExistingIssueWhenNotOverridden()
    {
        // Project-level workflow variables are live defaults. Existing issues
        // inherit updated project model settings unless the issue profile
        // explicitly overrides the same leaf.
        var runId = "wr_snapshot01";
        var proj = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { agent = new { model = "minimax-coding-plan/MiniMax-M3" } })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { issueContext = true })));

        await SeedAllLayersAsync("proj_snap", 1, runId, proj, issue);

        await using (var db = new MohistDbContext(_options))
        {
            var row = db.ProjectWorkflowProfiles.Single(x => x.ProjectId == "proj_snap");
            row.Variables = new VariableBundle(
                Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                { agent = new { model = "anthropic/claude-sonnet-4-6" } }))
            ).ToJson();
            await db.SaveChangesAsync();
        }

        var result = await _manager.ResolveLayeredVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        Assert.True(doc.RootElement.GetProperty("issueContext").GetBoolean());
        Assert.Equal("anthropic/claude-sonnet-4-6",
            doc.RootElement.GetProperty("agent").GetProperty("model").GetString());
    }

    [Fact]
    public async Task ResolveLayeredVariables_IssueModelOverrideWinsOverProjectModel()
    {
        var runId = "wr_override01";
        var proj = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { agent = new { type = "opencode", model = "project/default" } })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { agent = new { model = "issue/override" } })));

        await SeedAllLayersAsync("proj_override", 1, runId, proj, issue);

        var result = await _manager.ResolveLayeredVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("issue/override", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ResolveEffectiveVariables_ProjectStageModelAppliesWhenIssueOnlyHasContext()
    {
        var runId = "wr_project_stage01";
        var project = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { type = "opencode", model = "project/default" }
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["check"] = new(JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new
                    {
                        agent = new { type = "opencode", model = "openai/gpt-5.5" }
                    })))
            });
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                issue = new { number = 122 }
            })));

        await SeedAllLayersAsync("proj_project_stage", 1, runId, project, issue);

        var result = await _manager.ResolveEffectiveVariablesAsync(runId, "check");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        var agent = result.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
        Assert.Equal(122, result.GetProperty("issue").GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task ResolveEffectiveVariables_ReadsIssueStageAndFallsBackToTopLevel()
    {
        // Per-stage variables are read directly from the issue's Stages
        // map. Runtime dispatch falls back from
        // Variables.stages[stage].vars.agent to Variables.vars.agent via
        // ordinary variable lookups — no cross-layer resolution.
        var runId = "wr_stage01";
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model = "minimax-coding-plan/MiniMax-M3" }
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(new
                    {
                        agent = new { model = "anthropic/claude-sonnet-4-6" }
                    })))
            });

        await SeedIssueOnlyAsync("proj_stage", 1, runId, issue);

        var result = await _manager.ResolveEffectiveVariablesAsync(runId, "build");

        Assert.Equal("anthropic/claude-sonnet-4-6",
            result.GetProperty("agent").GetProperty("model").GetString());

        var topLevelResult = await _manager.ResolveEffectiveVariablesAsync(runId, null);
        Assert.Equal("minimax-coding-plan/MiniMax-M3",
            topLevelResult.GetProperty("agent").GetProperty("model").GetString());
    }

    [Fact]
    public async Task ResolveLayeredVariables_MergesTemplateProjectIssueAndRuntimeLayers()
    {
        var runId = "wr_effective01";
        var templateJson = SerializeDefinition(
            "effective-template",
            variables: new Dictionary<string, JsonElement?>
            {
                ["source"] = JsonSerializer.SerializeToElement("template"),
                ["agent"] = JsonSerializer.SerializeToElement(new { model = "template-model", type = "template-agent" }),
                ["github"] = JsonSerializer.SerializeToElement(new { pr = new { number = 1 } }),
            });
        var project = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                source = "project",
                agent = new { model = "project-model" },
            })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                source = "issue",
            })));
        var runtime = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                source = "runtime",
                github = new { pr = new { number = 249, url = "https://example.test/pr/249" } },
            })));

        await SeedAllLayersAsync("proj_effective", 1, runId, project, issue,
            issueTemplateJson: templateJson,
            runtime: runtime);

        var result = await _manager.ResolveLayeredVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var root = doc.RootElement;
        Assert.Equal("runtime", root.GetProperty("source").GetString());
        Assert.Equal("project-model", root.GetProperty("agent").GetProperty("model").GetString());
        Assert.Equal("template-agent", root.GetProperty("agent").GetProperty("type").GetString());
        var pr = root.GetProperty("github").GetProperty("pr");
        Assert.Equal(249, pr.GetProperty("number").GetInt32());
        Assert.Equal("https://example.test/pr/249", pr.GetProperty("url").GetString());
    }

    [Fact]
    public async Task ResolveEffectiveVariables_ReturnsRunnerVarsForStage()
    {
        var runId = "wr_effective_stage01";
        var templateJson = SerializeDefinition(
            "effective-stage-template",
            variables: new Dictionary<string, JsonElement?>
            {
                ["agent"] = JsonSerializer.SerializeToElement(new { type = "opencode", model = "template-model" }),
            });
        var project = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model = "project-model" },
            })),
            Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                {
                    agent = new { model = "build-model" },
                    stageOnly = true,
                })))
            });
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                issueOnly = true,
            })));

        await SeedAllLayersAsync("proj_effective_stage", 1, runId, project, issue,
            issueTemplateJson: templateJson);

        var result = await _manager.ResolveEffectiveVariablesAsync(runId, "build");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.False(result.TryGetProperty("vars", out _));
        Assert.False(result.TryGetProperty("stages", out _));
        Assert.True(result.GetProperty("stageOnly").GetBoolean());
        Assert.True(result.GetProperty("issueOnly").GetBoolean());
        var agent = result.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("build-model", agent.GetProperty("model").GetString());
    }

    [Fact]
    public void ExpandTaskWith_NullTaskWith_ReturnsNull()
    {
        var result = WorkflowProfileManager.ExpandTaskWith(VariableBundle.Empty, null);

        Assert.Null(result);
    }

    [Fact]
    public void ExpandTaskWith_ResolvesWholeTemplateStringToJsonValue()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { type = "opencode", model = "sonnet-4" }
            })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["name"] = JsonSerializer.SerializeToElement("task-1"),
            ["agent"] = JsonSerializer.SerializeToElement("${{ agent }}"),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.NotNull(result);
        Assert.Equal(JsonValueKind.Object, result["agent"]!.Value.ValueKind);
        Assert.Equal("opencode", result["agent"]!.Value.GetProperty("type").GetString());
        Assert.Equal("sonnet-4", result["agent"]!.Value.GetProperty("model").GetString());
    }

    [Fact]
    public void ExpandTaskWith_DeepMergesObjectKey()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                agent = new { model = "gpt-4o", timeoutMs = 300000 }
            })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["name"] = JsonSerializer.SerializeToElement("task-1"),
            ["agent"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { type = "opencode", timeoutMs = 600000 })),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result["agent"]));
        Assert.Equal("opencode", doc.RootElement.GetProperty("type").GetString());       // from task
        Assert.Equal("gpt-4o", doc.RootElement.GetProperty("model").GetString());        // from vars
        Assert.Equal(300000, doc.RootElement.GetProperty("timeoutMs").GetInt32());       // vars overrides task
    }

    [Fact]
    public void ExpandTaskWith_PreservesPlainValues()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { other = 1 })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["name"] = JsonSerializer.SerializeToElement("task-1"),
            ["count"] = JsonSerializer.SerializeToElement(42),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.Equal("task-1", result!["name"]!.Value.GetString());
        Assert.Equal(42, result!["count"]!.Value.GetInt32());
    }

    [Fact]
    public void ExpandTaskWith_ResolvesNestedWholeTemplatePath()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                config = new { deep = new { value = "found-it" } }
            })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["x"] = JsonSerializer.SerializeToElement("${{ config.deep.value }}"),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.Equal("found-it", result!["x"]!.Value.GetString());
    }

    [Fact]
    public void ExpandTaskWith_ResolvesVarsPrefixedNestedWholeTemplatePath()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                github = new { pr = new { number = 42, url = "https://github.com/example/repo/pull/42" } }
            })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["prNumber"] = JsonSerializer.SerializeToElement("${{ vars.github.pr.number }}"),
            ["prUrl"] = JsonSerializer.SerializeToElement("${{ vars.github.pr.url }}"),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.Equal(42, result!["prNumber"]!.Value.GetInt32());
        Assert.Equal("https://github.com/example/repo/pull/42", result!["prUrl"]!.Value.GetString());
    }

    [Fact]
    public void ExpandTaskWith_PreservesUnresolvedWholeTemplateString()
    {
        var resolved = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { other = 1 })));

        var taskWith = new Dictionary<string, JsonElement?>
        {
            ["agent"] = JsonSerializer.SerializeToElement("${{ missing.agent }}"),
        };

        var result = WorkflowProfileManager.ExpandTaskWith(resolved, taskWith);

        Assert.Equal("${{ missing.agent }}", result!["agent"]!.Value.GetString());
    }

    // =================================================================
    // Narrow API tests — LoadStageSpecsAsync / LoadStructureAsync /
    // LoadApprovalConfigAsync (design D6 — profileManager encapsulates
    // the template selection cascade so the grain never holds a
    // WorkflowDefinition).
    // =================================================================

    [Fact]
    public async Task LoadStageSpecsAsync_ReturnsTasksAndChecksForStage_FromProjectTemplate()
    {
        var runId = "wr_stage_specs_proj";
        var templateJson = SerializeDefinitionWithStages("specs-template",
            ("plan", new[]
            {
                new TaskDefinition("draft", "Draft", "spec/task"),
            }, new[]
            {
                new CheckDefinition("plan-ok", "Plan OK", "spec/check"),
            }, requiresApproval: false),
            ("build", new[]
            {
                new TaskDefinition("compile", "Compile", "spec/task"),
                new TaskDefinition("test", "Test", "spec/task"),
            }, new[]
            {
                new CheckDefinition("build-ok", "Build OK", "spec/check"),
            }, requiresApproval: false));

        await SeedProjectTemplateAsync("specs_proj", runId, "specs-template", templateJson);

        var build = await _manager.LoadStageSpecsAsync(runId, "build");

        Assert.Equal("build", build.Stage);
        Assert.Equal(new[] { "compile", "test" }, build.Tasks.Select(t => t.Id).ToArray());
        Assert.Equal(new[] { "build-ok" }, build.Checks.Select(c => c.Name).ToArray());
        Assert.Equal("sequential", build.LockBehavior);
        Assert.Equal(new[] { "ci-pool" }, build.Resources);
    }

    [Fact]
    public async Task LoadStageSpecsAsync_HonorsIssueCustomTemplate_PerStage()
    {
        // Issue-level template can replace the project default. The narrow API
        // re-runs the cascade on every call so the choice is honored
        // even when stage-init runs after StartAsync has already loaded
        // a different (e.g. project default) structure.
        var runId = "wr_stage_specs_issue";
        var projectJson = SerializeDefinitionWithStages("project-tmpl",
            ("build", new[]
            {
                new TaskDefinition("compile", "Compile", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));
        var issueJson = SerializeDefinitionWithStages("issue-custom",
            ("build", new[]
            {
                new TaskDefinition("replacement-task", "Replacement", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));

        await SeedIssueOverProjectTemplateAsync(
            "iss_proj", 1, runId,
            issueTemplateJson: issueJson,
            projectDefaultTemplateId: "project-tmpl",
            projectTemplateJson: projectJson);

        var build = await _manager.LoadStageSpecsAsync(runId, "build");

        Assert.Equal(new[] { "replacement-task" }, build.Tasks.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task LoadStageSpecsAsync_RerunsCascadeBetweenCalls_HotReloadsProfileEdits()
    {
        // The hot-reload promise: profile edits between two calls MUST be
        // visible to the second caller (since this API re-runs the cascade).
        var runId = "wr_stage_specs_hot_reload";
        var templateJson = SerializeDefinitionWithStages("hot-template",
            ("build", new[]
            {
                new TaskDefinition("original-task", "Original", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));

        await SeedProjectTemplateAsync("hot_proj", runId, "hot-template", templateJson);

        var before = await _manager.LoadStageSpecsAsync(runId, "build");
        Assert.Equal(new[] { "original-task" }, before.Tasks.Select(t => t.Id).ToArray());

        // Mutate the project template to a new task — next call must see it.
        var updatedJson = SerializeDefinitionWithStages("hot-template",
            ("build", new[]
            {
                new TaskDefinition("replacement-task", "Replacement", "spec/task"),
                new TaskDefinition("follow-up-task", "Follow Up", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));
        await UpdateProjectTemplateAsync("hot_proj", "hot-template", updatedJson);

        var after = await _manager.LoadStageSpecsAsync(runId, "build");
        Assert.Equal(new[] { "replacement-task", "follow-up-task" }, after.Tasks.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task LoadStageSpecsAsync_ThrowsWhenStageMissing()
    {
        var runId = "wr_stage_specs_missing";
        var templateJson = SerializeDefinitionWithStages("missing-template",
            ("build", new[]
            {
                new TaskDefinition("compile", "Compile", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: false));

        await SeedProjectTemplateAsync("missing_proj", runId, "missing-template", templateJson);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.LoadStageSpecsAsync(runId, "no-such-stage"));

        Assert.Contains("no-such-stage", ex.Message);
    }

    [Fact]
    public async Task LoadStageSpecsAsync_WhenAllProfilesDisabled_ThrowsActionableErrorInsteadOfFallingBackToLocal()
    {
        var runId = "wr_all_disabled_stage_specs";
        await SeedWithoutRunAsync(projectId: "proj-all-disabled-stage-specs", issueNumber: 1,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.LoadStageSpecsAsync(runId, "plan", "proj-all-disabled-stage-specs", 1));

        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadStageSpecsAsync_ExistingRunKeepsOriginalProfileAfterItIsDisabled()
    {
        var runId = "wr_existing_disabled_stage_specs";
        await SeedAsync(projectId: "proj-existing-disabled-stage-specs", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var integrate = await _manager.LoadStageSpecsAsync(runId, "integrate");

        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:rebase");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "merge-pr");
    }

    [Fact]
    public async Task LoadStructureAsync_ReturnsStageSequenceAndApprovalFlags_WithoutTasks()
    {
        // The narrow structure projection must NOT carry tasks or checks.
        // That keeps the grain's Create path from touching per-stage detail
        // until a stage actually initializes.
        var runId = "wr_structure_basic";
        var templateJson = SerializeDefinitionWithStages("struct-template",
            ("plan", new[]
            {
                new TaskDefinition("draft", "Draft", "spec/task"),
            }, new[]
            {
                new CheckDefinition("plan-ok", "Plan OK", "spec/check"),
            }, requiresApproval: true),
            ("build", new[]
            {
                new TaskDefinition("compile", "Compile", "spec/task"),
            }, new[]
            {
                new CheckDefinition("build-ok", "Build OK", "spec/check"),
            }, requiresApproval: false));

        await SeedProjectTemplateAsync("struct_proj", runId, "struct-template", templateJson);

        var structure = await _manager.LoadStructureAsync(runId);

        Assert.Equal("struct-template", structure.Id);
        Assert.Equal(new[] { "plan", "build" }, structure.Stages.Select(s => s.Stage).ToArray());
        Assert.True(structure.Stages.Single(s => s.Stage == "plan").RequiresApproval);
        Assert.False(structure.Stages.Single(s => s.Stage == "build").RequiresApproval);
    }

    [Fact]
    public async Task LoadStructureAsync_HonorsExplicitContextAtCreateTime_BeforeRunPersisted()
    {
        // StartAsync passes project/issue context explicitly because the run
        // is not yet persisted when the structure is loaded for Create.
        var runId = "wr_structure_explicit";
        var templateJson = SerializeDefinitionWithStages("explicit-tmpl",
            ("plan", new[]
            {
                new TaskDefinition("draft", "Draft", "spec/task"),
            }, Array.Empty<CheckDefinition>(), requiresApproval: true));

        // Seed only the project profile — no WorkflowRun row exists yet.
        await using (var db = new MohistDbContext(_options))
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = "explicit_proj",
                DefaultTemplateId = "explicit-tmpl",
                Variables = "{}",
            });
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = "explicit_proj",
                TemplateId = "explicit-tmpl",
                Template = templateJson,
            });
            db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
            {
                ProjectId = "explicit_proj",
                IssueNumber = 1,
                Variables = "{}",
            });
            await db.SaveChangesAsync();
        }

        // The run is not in the DB; only the explicit context will find the
        // project template.
        var structure = await _manager.LoadStructureAsync(
            runId, projectId: "explicit_proj", issueNumber: 1);

        Assert.Equal("explicit-tmpl", structure.Id);
        Assert.Equal(new[] { "plan" }, structure.Stages.Select(s => s.Stage).ToArray());
        Assert.True(structure.Stages.Single().RequiresApproval);
    }

    [Fact]
    public async Task LoadStructureAsync_FallsBackToSystemDefault_WhenContextMissing()
    {
        // Sanity: when neither the run nor explicit context carries a
        // project, the cascade ends at the system default template.
        var structure = await _manager.LoadStructureAsync("unknown-run-id");

        Assert.NotEmpty(structure.Stages);
        Assert.Contains(structure.Stages, s => s.Stage == "plan");
    }

    [Fact]
    public async Task LoadStructureAsync_WhenAllProfilesDisabled_ThrowsActionableErrorInsteadOfFallingBackToLocal()
    {
        var runId = "wr_all_disabled_structure";
        await SeedWithoutRunAsync(projectId: "proj-all-disabled-structure", issueNumber: 1,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _manager.LoadStructureAsync(runId, "proj-all-disabled-structure", 1));

        Assert.Contains("Enable a workflow first", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadStructureAsync_ExistingRunKeepsOriginalProfileAfterItIsDisabled()
    {
        var runId = "wr_existing_disabled_structure";
        await SeedAsync(projectId: "proj-existing-disabled-structure", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var structure = await _manager.LoadStructureAsync(runId);

        Assert.Equal("mohist/local", structure.Id);
        Assert.Contains(structure.Stages, s => s.Stage == "integrate");
    }

    [Fact]
    public async Task WorkflowQuerier_ExistingRunYamlAndStatusUseOriginalProfileAfterItIsDisabled()
    {
        var runId = "wr_existing_disabled_query";
        await SeedAsync(projectId: "proj-existing-disabled-query", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);
        await ReplaceRunStateAsync(runId, "proj-existing-disabled-query", 1, "mohist/local");
        var querier = new WorkflowQuerier(
            new TestDbContextFactory(_options),
            _manager,
            new Mohist.Server.Workflow.Services.Artifacts.WorkflowArtifactQuerier(new TestDbContextFactory(_options)));

        var yaml = await querier.GetDefinitionYamlAsync(runId);
        var status = await querier.GetStatusAsync(runId);

        Assert.NotNull(yaml);
        Assert.Contains("integrate:rebase", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("merge-pr", yaml, StringComparison.Ordinal);
        Assert.NotNull(status);
        var integrate = Assert.Single(status!.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:rebase");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "merge-pr");
    }

    [Fact]
    public async Task WorkflowQuerier_StatusRead_MigratesLegacyClaimAssignment()
    {
        var runId = "wr_legacy_claim_status_query";
        await SeedAsync(
            projectId: "proj-legacy-claim-status-query",
            issueNumber: 1,
            runId: runId,
            issueTemplateJson: null,
            issueWorkflowProfileId: "mohist/local");
        await ReplaceRunStateJsonAsync(
            runId,
            """
            {
              "id": "wr_legacy_claim_status_query",
              "metadata": {
                "createdAt": "2026-06-15T10:00:00Z"
              },
              "status": "ready",
              "claim": {
                "runnerId": "runner-legacy-claim",
                "claimedAt": "2026-06-15T10:01:00Z"
              },
              "currentStageId": "build",
              "stages": []
            }
            """);
        var querier = new WorkflowQuerier(
            new TestDbContextFactory(_options),
            _manager,
            new Mohist.Server.Workflow.Services.Artifacts.WorkflowArtifactQuerier(new TestDbContextFactory(_options)));

        var status = await querier.GetStatusAsync(runId);

        Assert.NotNull(status);
        Assert.Equal("ready", status!.Status);
        Assert.Equal("runner-legacy-claim", status.AssignedTo);
    }

    [Fact]
    public async Task LoadApprovalConfigAsync_ExistingRunIgnoresLaterDisabledProfiles()
    {
        var runId = "wr_existing_disabled_approval";
        await SeedAsync(projectId: "proj-existing-disabled-approval", issueNumber: 1, runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/local",
            disabledWorkflowProfileIds: ["mohist/local", "mohist/github-pr"]);

        var approval = await _manager.LoadApprovalConfigAsync(runId);

        Assert.NotNull(approval?.Feedback?.Task);
        Assert.Equal("apply-feedback", approval!.Feedback!.Task!.Id);
    }

    [Fact]
    public async Task LoadApprovalConfigAsync_ReturnsConfiguredFeedbackTask_WhenDefined()
    {
        var runId = "wr_approval_defined";
        var feedbackConfig = new ApprovalFeedbackConfig(
            Task: new FeedbackTaskConfig(
                Id: "apply-feedback",
                Title: "Apply Feedback",
                Uses: "spec/task",
                With: null));
        var approval = new ApprovalConfig(Feedback: feedbackConfig);
        var def = new WorkflowDefinition("approval-template",
            new List<StageDefinition>
            {
                new("plan",
                    new List<TaskDefinition>(),
                    new List<CheckDefinition>(),
                    RequiresApproval: true),
            },
            Approval: approval);
        var templateJson = JsonSerializer.Serialize(def, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        await SeedProjectTemplateAsync("approval_proj", runId, "approval-template", templateJson);

        var loaded = await _manager.LoadApprovalConfigAsync(runId);

        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.Feedback);
        Assert.NotNull(loaded.Feedback!.Task);
        Assert.Equal("apply-feedback", loaded.Feedback.Task!.Id);
        Assert.Equal("spec/task", loaded.Feedback.Task.Uses);
    }

    [Fact]
    public async Task LoadApprovalConfigAsync_ReturnsNull_WhenNoApprovalConfig()
    {
        var runId = "wr_approval_null";
        var templateJson = SerializeDefinitionWithStages("no-approval-template",
            ("plan", Array.Empty<TaskDefinition>(), Array.Empty<CheckDefinition>(), requiresApproval: false));

        await SeedProjectTemplateAsync("no_approval_proj", runId, "no-approval-template", templateJson);

        var loaded = await _manager.LoadApprovalConfigAsync(runId);

        Assert.Null(loaded);
    }

    // --- helpers ---

    private static string SerializeDefinition(
        string id,
        int stageCount = 1,
        Dictionary<string, JsonElement?>? variables = null)
    {
        var stages = new List<StageDefinition>();
        for (var i = 0; i < stageCount; i++)
            stages.Add(new StageDefinition($"stage-{i}", [], []));
        var def = new WorkflowDefinition(id, stages, Variables: variables);
        return JsonSerializer.Serialize(def, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    private static string SerializeDefinitionWithStages(
        string id,
        params (string stage, TaskDefinition[] tasks, CheckDefinition[] checks, bool requiresApproval)[] stageSpecs)
    {
        var stages = new List<StageDefinition>();
        foreach (var (stage, tasks, checks, requiresApproval) in stageSpecs)
        {
            stages.Add(new StageDefinition(
                stage,
                new List<TaskDefinition>(tasks),
                new List<CheckDefinition>(checks),
                RequiresApproval: requiresApproval,
                LockBehavior: stage == "build" ? "sequential" : null,
                Resources: stage == "build" ? new List<string> { "ci-pool" } : null));
        }

        var def = new WorkflowDefinition(id, stages);
        return JsonSerializer.Serialize(def, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    private async Task SeedProjectTemplateAsync(string projectId, string runId, string templateId, string templateJson)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, 1, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = templateId,
            Variables = "{}",
        });
        db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
        {
            ProjectId = projectId,
            TemplateId = templateId,
            Template = templateJson,
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = 1,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    private async Task UpdateProjectTemplateAsync(string projectId, string templateId, string templateJson)
    {
        await using var db = new MohistDbContext(_options);
        var existing = await db.ProjectWorkflowTemplates.FindAsync(projectId, templateId);
        Assert.NotNull(existing);
        existing!.Template = templateJson;
        existing.UpdatedAt = TestTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task SeedIssueOverProjectTemplateAsync(
        string projectId,
        int issueNumber,
        string runId,
        string issueTemplateJson,
        string projectDefaultTemplateId,
        string projectTemplateJson)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueNumber, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = projectDefaultTemplateId,
            Variables = "{}",
        });
        db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
        {
            ProjectId = projectId,
            TemplateId = projectDefaultTemplateId,
            Template = projectTemplateJson,
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            Template = issueTemplateJson,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    private async Task SeedAsync(
        string projectId, int issueNumber, string runId,
        string? issueTemplateJson,
        string? issueSourceTemplateId = null,
        string? projectDefaultTemplateId = null,
        string? projectTemplateJson = null,
        string? issueWorkflowProfileId = null,
        string[]? disabledWorkflowProfileIds = null)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueNumber, runId, issueWorkflowProfileId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = projectDefaultTemplateId,
            Variables = "{}",
            DisabledWorkflowProfileIds = disabledWorkflowProfileIds?.ToList() ?? [],
        });

        if (projectDefaultTemplateId is not null && projectTemplateJson is not null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = projectDefaultTemplateId,
                Template = projectTemplateJson,
            });
        }
        if (issueSourceTemplateId is not null && projectTemplateJson is not null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = issueSourceTemplateId,
                Template = projectTemplateJson,
            });
        }

        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            SourceTemplateId = issueSourceTemplateId,
            Template = issueTemplateJson,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    private async Task SeedWithoutRunAsync(
        string projectId, int issueNumber,
        string? issueTemplateJson,
        string? issueSourceTemplateId = null,
        string? projectDefaultTemplateId = null,
        string? projectTemplateJson = null,
        string[]? disabledWorkflowProfileIds = null)
    {
        await using var db = new MohistDbContext(_options);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = projectDefaultTemplateId,
            Variables = "{}",
            DisabledWorkflowProfileIds = disabledWorkflowProfileIds?.ToList() ?? [],
        });

        if (projectDefaultTemplateId is not null && projectTemplateJson is not null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = projectDefaultTemplateId,
                Template = projectTemplateJson,
            });
        }
        if (issueSourceTemplateId is not null && projectTemplateJson is not null)
        {
            db.ProjectWorkflowTemplates.Add(new ProjectWorkflowTemplateRow
            {
                ProjectId = projectId,
                TemplateId = issueSourceTemplateId,
                Template = projectTemplateJson,
            });
        }

        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            SourceTemplateId = issueSourceTemplateId,
            Template = issueTemplateJson,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    private async Task ReplaceRunStateAsync(string runId, string projectId, int issueNumber, string systemProfileId)
    {
        await using var db = new MohistDbContext(_options);
        var row = await db.WorkflowRuns.FirstAsync(x => x.WorkflowRunId == runId);
        var definition = ProjectWorkflowProfileManager.GetSystemTemplateDefinition(systemProfileId)
            ?? throw new InvalidOperationException($"Unknown system profile '{systemProfileId}'");
        var run = WorkflowRun.Create(
            runId,
            definition,
            DateTimeOffset.UnixEpoch,
            new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UnixEpoch,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = projectId,
                    ["issueNumber"] = issueNumber.ToString(),
                }));
        row.State = JSON.Serialize(run);
        await db.SaveChangesAsync();
    }

    private async Task ReplaceRunStateJsonAsync(string runId, string stateJson)
    {
        await using var db = new MohistDbContext(_options);
        var row = await db.WorkflowRuns.FirstAsync(x => x.WorkflowRunId == runId);
        row.State = stateJson;
        await db.SaveChangesAsync();
    }

    private async Task SeedRunOnlyAsync(
        string projectId, int issueNumber, string runId)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueNumber, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            Variables = "{}",
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    private async Task SeedAllLayersAsync(
        string projectId, int issueNumber, string runId,
        VariableBundle project,
        VariableBundle issue,
        string? issueTemplateJson = null,
        VariableBundle? runtime = null)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueNumber, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            Variables = project.ToJson(),
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            Template = issueTemplateJson,
            Variables = issue.ToJson(),
        });
        if (runtime is not null)
        {
            db.WorkflowRunProfiles.Add(new WorkflowRunProfileRow
            {
                WorkflowRunId = runId,
                Variables = runtime.ToJson(),
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedIssueOnlyAsync(
        string projectId, int issueNumber, string runId, VariableBundle issue)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueNumber, runId);

        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            ProjectId = projectId,
            IssueNumber = issueNumber,
            Variables = issue.ToJson(),
        });

        await db.SaveChangesAsync();
    }

    private static void SeedRunContext(
        MohistDbContext db,
        string projectId,
        int issueNumber,
        string runId,
        string? issueWorkflowProfileId = null)
    {
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = JSON.Serialize(new
            {
                Id = runId,
                Metadata = new
                {
                    CreatedAt = TestTime.UtcNow,
                    Annotations = new Dictionary<string, string>
                    {
                        ["projectId"] = projectId,
                        ["issueNumber"] = issueNumber.ToString(),
                    },
                },
                Status = "Failed",
                Stages = Array.Empty<object>(),
            }),
        });
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            State = JSON.Serialize(new
            {
                ProjectId = projectId,
                Number = issueNumber,
                Title = "Seeded issue",
                Priority = "p2",
                WorkflowRunId = runId,
                WorkflowProfileId = issueWorkflowProfileId,
            }),
        });
    }

    private class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => _options = options;
        public MohistDbContext CreateDbContext() => new(_options);
    }
}
