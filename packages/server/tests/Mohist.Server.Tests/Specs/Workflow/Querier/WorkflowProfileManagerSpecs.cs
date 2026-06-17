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
        _manager = new WorkflowProfileManager(factory, null!, new PromptTemplateEngine());

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
    public async Task LoadTemplate_Priority4_FallsBackToProjectDefault()
    {
        var runId = "wr_default01";
        await SeedAsync(projectId: "proj4", issueId: "issue_4", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "default-tmpl",
            projectTemplateJson: SerializeDefinition("default-tmpl", stageCount: 5));

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
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
    public async Task LoadVariables_ReturnsEmpty_WhenOnlyRunExists()
    {
        var runId = "wr_vars01";
        await SeedRunOnlyAsync("proj5", "issue_5", runId);

        var result = await _manager.LoadVariablesAsync(runId);

        Assert.False(result.Vars.HasValue);
        Assert.Null(result.Stages);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadVariables_ReturnsIssueLayerDirectly_DoesNotReMergeProject()
    {
        // T1 has already snapshotted project+global into the issue layer
        // (IssueGrain.StartWorkflowAsync). Runtime must read that snapshot
        // directly and MUST NOT re-merge the project layer — the project
        // bundle here is intentionally divergent from the issue bundle, and
        // the result must reflect the issue values verbatim.
        var runId = "wr_direct01";
        var proj = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { a = 1, b = "proj-b", c = "proj-c" })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { b = "issue-b", c = "issue-c", d = "issue-d" })));

        await SeedAllLayersAsync("proj6", "issue_6", runId, proj, issue);

        var result = await _manager.LoadVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        // Project-only key `a` is gone — runtime does not see the project layer.
        Assert.False(doc.RootElement.TryGetProperty("a", out _));
        // Issue values come through verbatim.
        Assert.Equal("issue-b", doc.RootElement.GetProperty("b").GetString());
        Assert.Equal("issue-c", doc.RootElement.GetProperty("c").GetString());
        Assert.Equal("issue-d", doc.RootElement.GetProperty("d").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadVariables_SnapshotIsStable_ProjectEditsDoNotAffectExistingIssue()
    {
        // Snapshot semantics: editing the project layer after an issue
        // already has its T1-merged snapshot must not change that issue's
        // effective variables. The runtime reads the issue layer directly.
        var runId = "wr_snapshot01";
        var proj = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { agent = new { model = "minimax-coding-plan/MiniMax-M3" } })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { agent = new { model = "kimi-for-coding/k2p6" } })));

        await SeedAllLayersAsync("proj_snap", "issue_snap", runId, proj, issue);

        // Mutate the project layer; the runtime result must still reflect
        // the issue snapshot, not the new project value.
        await using (var db = new MohistDbContext(_options))
        {
            var row = db.ProjectWorkflowProfiles.Single(x => x.ProjectId == "proj_snap");
            row.Variables = new VariableBundle(
                Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
                { agent = new { model = "anthropic/claude-sonnet-4-6" } }))
            ).ToJson();
            await db.SaveChangesAsync();
        }

        var result = await _manager.LoadVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        Assert.Equal("kimi-for-coding/k2p6",
            doc.RootElement.GetProperty("agent").GetProperty("model").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task LoadVariables_ReadsIssueStagesDirectly_ForStageDispatch()
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

        var result = await _manager.LoadVariablesAsync(runId);

        Assert.NotNull(result.Stages);
        Assert.True(result.Stages!.TryGetValue("build", out var buildStage));
        Assert.NotNull(buildStage.Vars);
        using var stageDoc = JsonDocument.Parse(buildStage.Vars.Value.GetRawText());
        Assert.Equal("anthropic/claude-sonnet-4-6",
            stageDoc.RootElement.GetProperty("agent").GetProperty("model").GetString());

        Assert.NotNull(result.Vars);
        using var varsDoc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        Assert.Equal("minimax-coding-plan/MiniMax-M3",
            varsDoc.RootElement.GetProperty("agent").GetProperty("model").GetString());
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

    private static string SerializeDefinition(string id, int stageCount)
    {
        var stages = new List<StageDefinition>();
        for (var i = 0; i < stageCount; i++)
            stages.Add(new StageDefinition($"stage-{i}", [], []));
        var def = new WorkflowDefinition(id, stages);
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
        string? projectTemplateJson = null)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueId, runId);

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
        VariableBundle project, VariableBundle issue)
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
            Variables = issue.ToJson(),
        });

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
        string runId)
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
                WorkflowRunId = runId,
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
