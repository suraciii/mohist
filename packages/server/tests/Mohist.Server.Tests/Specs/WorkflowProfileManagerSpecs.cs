using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Infrastructure;
using Mohist.Server.Workflow.Storage;
using Xunit;

namespace Mohist.Server.Tests.Specs;

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
        _manager = new WorkflowProfileManager(factory);

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

    [Fact]
    public async Task LoadTemplate_FallsBackToSystemDefault_WhenRunContextMissing()
    {
        var result = await _manager.LoadTemplateAsync("unknown-run-id");

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/default", result.Id ?? "");
    }

    [Fact]
    public async Task LoadTemplate_UsesIssueCustomWithoutRunProfileBinding()
    {
        var runId = "wr_snap01";
        await SeedAsync(projectId: "proj1", issueKey: "proj1:1", runId: runId,
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
        await SeedAsync(projectId: "proj2", issueKey: "proj2:1", runId: runId,
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
        await SeedAsync(projectId: "proj3", issueKey: "proj3:1", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: "my-tmpl",
            projectTemplateJson: SerializeDefinition("my-tmpl", stageCount: 4));

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("project-template", result.Id ?? "");
        Assert.Equal(4, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_Priority4_FallsBackToProjectDefault()
    {
        var runId = "wr_default01";
        await SeedAsync(projectId: "proj4", issueKey: "proj4:1", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "default-tmpl",
            projectTemplateJson: SerializeDefinition("default-tmpl", stageCount: 5));

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Equal(5, result.Structure.Stages.Count);
    }

    [Fact]
    public async Task LoadTemplate_ProjectDefaultSystemTemplate_FallsBackToSystemTemplate()
    {
        var runId = "wr_system_default01";
        await SeedAsync(projectId: "proj-sys", issueKey: "proj-sys:1", runId: runId,
            issueTemplateJson: null,
            issueSourceTemplateId: null,
            projectDefaultTemplateId: "mohist/default");

        var result = await _manager.LoadTemplateAsync(runId);

        Assert.NotNull(result.Structure);
        Assert.Contains("system-template:mohist/default", result.Id ?? "");
        Assert.Contains(result.Structure.Stages, s => s.Stage == "plan");
    }

    [Fact]
    public async Task LoadVariables_ReturnsEmpty_WhenOnlyRunExists()
    {
        var runId = "wr_vars01";
        await SeedRunOnlyAsync("proj5", "proj5:1", runId);

        var result = await _manager.LoadVariablesAsync(runId);

        Assert.False(result.Vars.HasValue);
        Assert.Null(result.Stages);
    }

    [Fact]
    public async Task LoadVariables_MergesProjectAndIssueByPriority()
    {
        var runId = "wr_merge01";
        var proj = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { a = 1, b = "proj-b", c = "proj-c" })));
        var issue = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            { b = "issue-b", c = "issue-c" })));

        await SeedAllLayersAsync("proj6", "proj6:1", runId, proj, issue);

        var result = await _manager.LoadVariablesAsync(runId);

        Assert.NotNull(result.Vars);
        using var doc = JsonDocument.Parse(result.Vars.Value.GetRawText());
        Assert.Equal(1, doc.RootElement.GetProperty("a").GetInt32());           // from project
        Assert.Equal("issue-b", doc.RootElement.GetProperty("b").GetString());  // issue overrides project
        Assert.Equal("issue-c", doc.RootElement.GetProperty("c").GetString());  // issue overrides project
    }

    [Fact]
    public void ExpandTaskWith_NullTaskWith_ReturnsNull()
    {
        var result = WorkflowProfileManager.ExpandTaskWith(VariableBundle.Empty, null);

        Assert.Null(result);
    }

    [Fact]
    public void ExpandTaskWith_PreservesTemplateStrings()
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
        // Template expression is PRESERVED, not expanded (runner expands later)
        Assert.Equal("${{ agent }}", result["agent"]!.Value.GetString());
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
    public void ExpandTaskWith_PreservesNestedTemplatePath()
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

        // Template preserved (runner expands later)
        Assert.Equal("${{ config.deep.value }}", result!["x"]!.Value.GetString());
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
        string projectId, string issueKey, string runId,
        string? issueTemplateJson,
        string? issueSourceTemplateId = null,
        string? projectDefaultTemplateId = null,
        string? projectTemplateJson = null)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueKey, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfileRow
        {
            ProjectId = projectId,
            DefaultTemplateId = projectDefaultTemplateId,
            VariablesJson = "{}",
        });

        if (projectDefaultTemplateId is not null && projectTemplateJson is not null)
        {
            db.ProjectTemplates.Add(new ProjectTemplateRow
            {
                ProjectId = projectId,
                TemplateId = projectDefaultTemplateId,
                TemplateJson = projectTemplateJson,
            });
        }
        if (issueSourceTemplateId is not null && projectTemplateJson is not null)
        {
            db.ProjectTemplates.Add(new ProjectTemplateRow
            {
                ProjectId = projectId,
                TemplateId = issueSourceTemplateId,
                TemplateJson = projectTemplateJson,
            });
        }

        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfileRow
        {
            IssueKey = issueKey,
            SourceTemplateId = issueSourceTemplateId,
            TemplateJson = issueTemplateJson,
            VariablesJson = "{}",
        });

        await db.SaveChangesAsync();
    }

    private async Task SeedRunOnlyAsync(
        string projectId, string issueKey, string runId)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueKey, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfileRow
        {
            ProjectId = projectId,
            VariablesJson = "{}",
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfileRow
        {
            IssueKey = issueKey,
            VariablesJson = "{}",
        });

        await db.SaveChangesAsync();
    }

    private async Task SeedAllLayersAsync(
        string projectId, string issueKey, string runId,
        VariableBundle project, VariableBundle issue)
    {
        await using var db = new MohistDbContext(_options);
        SeedRunContext(db, projectId, issueKey, runId);

        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfileRow
        {
            ProjectId = projectId,
            VariablesJson = project.ToJson(),
        });
        db.IssueWorkflowProfiles.Add(new IssueWorkflowProfileRow
        {
            IssueKey = issueKey,
            VariablesJson = issue.ToJson(),
        });

        await db.SaveChangesAsync();
    }

    private static void SeedRunContext(MohistDbContext db, string projectId, string issueKey, string runId)
    {
        var number = int.Parse(issueKey.Split(':')[1]);
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = runId,
            State = JsonSerializer.Serialize(new
            {
                Id = runId,
                Metadata = new { CreatedAt = DateTimeOffset.UtcNow },
                Status = "Failed",
                Stages = Array.Empty<object>(),
            }),
        });
        db.IssueStates.Add(new IssueStateRow
        {
            Key = issueKey,
            StateJson = JsonSerializer.Serialize(new
            {
                ProjectId = projectId,
                Number = number,
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
