using System.Text.Json;
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
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Workflow.Querier;

public class WorkflowProfileManagerSpecs : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly WorkflowProfileManager _manager;

    public WorkflowProfileManagerSpecs()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"profile-specs-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        var factory = new TestDbContextFactory(_options);
        var runProfileManager = new WorkflowRunProfileManager(factory);
        var promptLoader = new Mohist.Server.Workflow.Services.Prompts.FilePromptLoader();
        var registry = new Mohist.Server.Issue.Services.WorkflowProfiles.IssueWorkflowProfileRegistry(promptLoader, factory);
        _manager = new WorkflowProfileManager(
            factory,
            promptLoader,
            new PromptTemplateEngine(),
            WorkflowGrainTestHelpers.CreateEmptyConfigService(),
            runProfileManager,
            new Mohist.Server.Issue.Services.WorkflowProfiles.EffectiveWorkflowProfileResolver(registry));

        using var initDb = new MohistDbContext(_options);
        initDb.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = new MohistDbContext(_options);
        await db.Database.EnsureDeletedAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadTemplate_FallsBackToSystemDefault_WhenRunContextMissing()
    {
        var result = await _manager.LoadTemplateAsync("unknown-run-id");

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/default", result.Id ?? "");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadTemplate_UsesIssueCustomWithoutRunProfileBinding()
    {
        var runId = "wr_snap01";
        await SeedAsync(projectId: "proj1", issueId: "issue_1", runId: runId,
            issueTemplateJson: SerializeDefinition("issue-custom", stageCount: 2),
            projectTemplateJson: SerializeDefinition("project-tmpl", stageCount: 3));

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("issue-custom", result.Id ?? "");
        Assert.Equal(2, result.Structure.Stages.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadTemplate_Priority2_ReturnsIssueCustomTemplate()
    {
        var runId = "wr_issue01";
        await SeedAsync(projectId: "proj2", issueId: "issue_2", runId: runId,
            issueTemplateJson: SerializeDefinition("issue-custom", stageCount: 2),
            projectTemplateJson: SerializeDefinition("project-tmpl", stageCount: 3));

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("issue-custom", result.Id ?? "");
        Assert.Equal(2, result.Structure.Stages.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadTemplate_Priority3_ReturnsIssueReferencedTemplate()
    {
        var runId = "wr_ref01";
        await SeedAsync(projectId: "proj3", issueId: "issue_3", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: "my-tmpl",
            projectTemplateJson: SerializeDefinition("my-tmpl", stageCount: 4));

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("project-template", result.Id ?? "");
        Assert.Equal(4, result.Structure.Stages.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadTemplate_ProjectDefaultCustomTemplate_NoIssueSelection_UsesProjectDefault()
    {
        var runId = "wr_default01";
        await SeedAsync(projectId: "proj4", issueId: "issue_4", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "default-tmpl",
            projectTemplateJson: SerializeDefinition("default-tmpl", stageCount: 5));

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("project-template", result.Id ?? "");
        Assert.Equal(5, result.Structure.Stages.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadTemplate_ProjectDefaultSystemTemplate_FallsBackToSystemTemplate()
    {
        var runId = "wr_system_default01";
        await SeedAsync(projectId: "proj-sys", issueId: "issue_sys", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "mohist/default");

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/default", result.Id ?? "");
        Assert.Contains(result.Structure.Stages, s => s.Stage == "plan");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadTemplate_IssuePrProfile_NoOverrides_UsesPrSystemTemplate()
    {
        var runId = "wr_issue_pr";
        await SeedAsync(projectId: "proj-issue-pr", issueId: "issue_pr", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/pr");

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/pr", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:open-pr");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:rebase");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadTemplate_IssueDefaultProfile_NoOverrides_UsesDefaultSystemTemplate()
    {
        var runId = "wr_issue_default";
        await SeedAsync(projectId: "proj-issue-default", issueId: "issue_default", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/default");

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/default", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:rebase");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:open-pr");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadTemplate_IssuePrProfile_ProjectDefaultIsDifferent_UsesIssueProfile()
    {
        var runId = "wr_issue_pr_proj_default";
        await SeedAsync(projectId: "proj-issue-pr-proj-default", issueId: "issue_pr_proj", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "mohist/default",
            issueWorkflowProfileId: "mohist/pr");

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/pr", result.Id ?? "");
        var integrate = Assert.Single(result.Structure.Stages, s => s.Stage == "integrate");
        Assert.Contains(integrate.Tasks, t => t.Id == "integrate:open-pr");
        Assert.DoesNotContain(integrate.Tasks, t => t.Id == "integrate:rebase");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadTemplate_IssuePrProfile_CustomYamlOverride_TakesPrecedence()
    {
        var runId = "wr_issue_pr_custom";
        await SeedAsync(projectId: "proj-pr-custom", issueId: "issue_pr_custom", runId: runId,
            issueTemplateJson: SerializeDefinition("custom-override", stageCount: 1),
            issueSourceTemplateId: null,
            projectDefaultTemplateId: null,
            issueWorkflowProfileId: "mohist/pr");

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("issue-custom", result.Id ?? "");
        Assert.Single(result.Structure.Stages);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ResolveLayeredVariables_ReturnsEmpty_WhenNoProfileVariablesExist()
    {
        var runId = "wr_vars01";
        await SeedAsync(
            projectId: "proj5",
            issueId: "issue_5",
            runId: runId,
            issueTemplateJson: SerializeDefinition("empty-vars-template"));

        var result = await _manager.ResolveLayeredVariablesAsync(runId);

        Assert.False(result.Vars.HasValue);
        Assert.Null(result.Stages);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

        await SeedAllLayersAsync("proj6", "issue_6", runId, proj, issue);

        var result = await _manager.ResolveLayeredVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        Assert.Equal(1, doc.RootElement.GetProperty("a").GetInt32());
        Assert.Equal("issue-b", doc.RootElement.GetProperty("b").GetString());
        Assert.Equal("issue-c", doc.RootElement.GetProperty("c").GetString());
        Assert.Equal("issue-d", doc.RootElement.GetProperty("d").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

        await SeedAllLayersAsync("proj_snap", "issue_snap", runId, proj, issue);

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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

        await SeedAllLayersAsync("proj_override", "issue_override", runId, proj, issue);

        var result = await _manager.ResolveLayeredVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("issue/override", agent.GetProperty("model").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

        await SeedAllLayersAsync("proj_project_stage", "issue_project_stage", runId, project, issue);

        var result = await _manager.ResolveEffectiveVariablesAsync(runId, "check");

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        var agent = result.GetProperty("agent");
        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal("openai/gpt-5.5", agent.GetProperty("model").GetString());
        Assert.Equal(122, result.GetProperty("issue").GetProperty("number").GetInt32());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

        await SeedIssueOnlyAsync("proj_stage", "issue_stage", runId, issue);

        var result = await _manager.ResolveEffectiveVariablesAsync(runId, "build");

        Assert.Equal("anthropic/claude-sonnet-4-6",
            result.GetProperty("agent").GetProperty("model").GetString());

        var topLevelResult = await _manager.ResolveEffectiveVariablesAsync(runId, null);
        Assert.Equal("minimax-coding-plan/MiniMax-M3",
            topLevelResult.GetProperty("agent").GetProperty("model").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

        await SeedAllLayersAsync("proj_effective", "issue_effective", runId, project, issue,
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

        await SeedAllLayersAsync("proj_effective_stage", "issue_effective_stage", runId, project, issue,
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ExpandTaskWith_NullTaskWith_ReturnsNull()
    {
        var result = WorkflowProfileManager.ExpandTaskWith(VariableBundle.Empty, null);

        Assert.Null(result);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

    private async Task SeedAsync(
        string projectId, string issueId, string runId,
        string? issueTemplateJson,
        string? issueSourceTemplateId = null,
        string? projectDefaultTemplateId = null,
        string? projectTemplateJson = null,
        string? issueWorkflowProfileId = null)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueId, runId, issueWorkflowProfileId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            DefaultTemplateId = projectDefaultTemplateId,
            Variables = "{}",
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
            IssueId = issueId,
            SourceTemplateId = issueSourceTemplateId,
            Template = issueTemplateJson,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    private async Task SeedRunOnlyAsync(
        string projectId, string issueId, string runId)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueId, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            Variables = "{}",
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            IssueId = issueId,
            Variables = "{}",
        });

        await db.SaveChangesAsync();
    }

    private async Task SeedAllLayersAsync(
        string projectId, string issueId, string runId,
        VariableBundle project,
        VariableBundle issue,
        string? issueTemplateJson = null,
        VariableBundle? runtime = null)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueId, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = projectId,
            Variables = project.ToJson(),
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            IssueId = issueId,
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
        string projectId, string issueId, string runId, VariableBundle issue)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueId, runId);

        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfile
        {
            IssueId = issueId,
            Variables = issue.ToJson(),
        });

        await db.SaveChangesAsync();
    }

    private static void SeedRunContext(
        MohistDbContext db,
        string projectId,
        string issueId,
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
                    CreatedAt = DateTimeOffset.UtcNow,
                    Annotations = new Dictionary<string, string> { ["issueId"] = issueId },
                },
                Status = "Failed",
                Stages = Array.Empty<object>(),
            }),
        });
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            State = JSON.Serialize(new
            {
                Id = issueId,
                ProjectId = projectId,
                Number = 1,
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
