using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Infrastructure;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class IssueWorkflowProfileManagerSpecs : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly IssueWorkflowProfileManager _manager;

    public IssueWorkflowProfileManagerSpecs()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"issue-profile-{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _manager = new IssueWorkflowProfileManager(new Factory(_options));

        using var db = new MohistDbContext(_options);
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = new MohistDbContext(_options);
        await db.Database.EnsureDeletedAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // ===================== Template =====================

    [Fact]
    public async Task GetProfile_ReturnsNull_WhenNoRecord()
    {
        var profile = await _manager.GetProfileAsync("issue_none");
        Assert.Null(profile);
    }

    [Fact]
    public async Task UpdateTemplate_ProjectReference_StoresSourceTemplateId()
    {
        var row = await _manager.UpdateTemplateAsync("issue_1",
            new IssueTemplateUpdateRequest(ProjectTemplateId: "some-template"));

        Assert.Equal("issue_1", row.IssueId);
        Assert.Equal("some-template", row.SourceTemplateId);
        Assert.Null(row.Template);
    }

    [Fact]
    public async Task UpdateTemplate_CustomYaml_StoresParsedDefinition()
    {
        var yaml = """
            id: my-custom
            stages:
              - stage: build
                tasks: []
                checks: []
            """;
        var row = await _manager.UpdateTemplateAsync("issue_2",
            new IssueTemplateUpdateRequest(Template: yaml));

        Assert.Null(row.SourceTemplateId);
        Assert.NotNull(row.Template);

        var def = await _manager.GetTemplateAsync("issue_2");
        Assert.NotNull(def);
        Assert.Equal("my-custom", def.Id);
    }

    [Fact]
    public async Task UpdateTemplate_BothSet_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.UpdateTemplateAsync("issue_3",
                new IssueTemplateUpdateRequest(
                    ProjectTemplateId: "t1",
                    Template: """
                        id: foo
                        stages:
                          - stage: s
                            tasks: []
                            checks: []
                        """)));
    }

    [Fact]
    public async Task UpdateTemplate_NullClears_BothFields()
    {
        // first set
        await _manager.UpdateTemplateAsync("issue_4",
            new IssueTemplateUpdateRequest(ProjectTemplateId: "t1"));
        // then clear
        var row = await _manager.UpdateTemplateAsync("issue_4",
            new IssueTemplateUpdateRequest());

        Assert.Null(row.SourceTemplateId);
        Assert.Null(row.Template);
    }

    [Fact]
    public async Task UpdateTemplate_OverwritesPreviousCustom()
    {
        var yaml1 = """
            id: v1
            stages:
              - stage: s1
                tasks: []
                checks: []
            """;
        var yaml2 = """
            id: v1
            stages:
              - stage: s2
                tasks: []
                checks: []
            """;
        await _manager.UpdateTemplateAsync("issue_5", new IssueTemplateUpdateRequest(Template: yaml1));
        await _manager.UpdateTemplateAsync("issue_5", new IssueTemplateUpdateRequest(Template: yaml2));

        var def = await _manager.GetTemplateAsync("issue_5");
        Assert.Single(def!.Stages);
        Assert.Equal("s2", def.Stages[0].Stage);
    }

    // ===================== Variables =====================

    [Fact]
    public async Task GetVariables_Empty_WhenNoRecord()
    {
        var bundle = await _manager.GetVariablesAsync("issue_none");
        Assert.Same(VariableBundle.Empty, bundle);
    }

    [Fact]
    public async Task SetVariables_StoresAndRetrieves()
    {
        var bundle = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { x = 42 })));

        await _manager.SetVariablesAsync("issue_s", bundle);
        var got = await _manager.GetVariablesAsync("issue_s");

        Assert.NotNull(got.Vars);
    }

    [Fact]
    public async Task PatchVariables_DeepMergesAcrossCalls()
    {
        var initial = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(
                new { agent = new { type = "opencode", timeout = 300 } })));
        await _manager.SetVariablesAsync("issue_p", initial);

        var patch = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(
                new { agent = new { model = "gpt-4o" } })));
        await _manager.PatchVariablesAsync("issue_p", patch);

        var result = await _manager.GetVariablesAsync("issue_p");
        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        Assert.Equal("opencode", agent.GetProperty("type").GetString());
        Assert.Equal(300, agent.GetProperty("timeout").GetInt32());
        Assert.Equal("gpt-4o", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task VariableOperations_AreIsolatedFromTemplateOperations()
    {
        // set variables
        var bundle = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { keep = 1 })));
        await _manager.SetVariablesAsync("issue_isolate", bundle);

        // set template
        await _manager.UpdateTemplateAsync("issue_isolate",
            new IssueTemplateUpdateRequest(ProjectTemplateId: "some-tmpl"));

        // variables still intact
        var got = await _manager.GetVariablesAsync("issue_isolate");
        Assert.NotNull(got.Vars);
        using var doc = JsonDocument.Parse(got.Vars.Value.GetRawText());
        Assert.Equal(1, doc.RootElement.GetProperty("keep").GetInt32());
    }

    // ===================== helpers =====================

    private class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;
        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;
        public MohistDbContext CreateDbContext() => new(_options);
    }
}
