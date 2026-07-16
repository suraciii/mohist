using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.Issue.Profile;

public class IssueWorkflowProfileManagerSpecs : IAsyncLifetime
{
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly IssueWorkflowProfileManager _manager;
    private readonly SqliteConnection _keeper;

    public IssueWorkflowProfileManagerSpecs()
    {
        var connectionString = $"Data Source=issue-profile-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .Options;
        _manager = new IssueWorkflowProfileManager(new Factory(_options));

        MigratedSqliteTemplate.CopyModelSchemaTo(_keeper);
        using var db = new MohistDbContext(_options);
        var issueIds = new[]
        {
            "issue_1", "issue_2", "issue_4", "issue_5",
            "issue_s", "issue_p", "issue_isolate", "issue_zh",
        };
        for (var index = 0; index < issueIds.Length; index++)
        {
            var issueId = issueIds[index];
            db.Issues.Add(new IssueRow
            {
                IssueId = issueId,
                State = JSON.Serialize(new
                {
                    id = issueId,
                    projectId = "proj_profile",
                    number = index + 1,
                }),
            });
        }
        db.SaveChanges();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    // ===================== Template =====================

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetProfile_ReturnsNull_WhenNoRecord()
    {
        var profile = await _manager.GetProfileAsync("issue_none");
        Assert.Null(profile);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task UpdateTemplate_ProjectReference_StoresSourceTemplateId()
    {
        var row = await _manager.UpdateTemplateAsync("issue_1",
            new IssueTemplateUpdateRequest(ProjectTemplateId: "some-template"));

        Assert.Equal("issue_1", row.IssueId);
        Assert.Equal("some-template", row.SourceTemplateId);
        Assert.Null(row.Template);

        var stored = await _manager.GetProfileAsync("issue_1");
        Assert.Equal("proj_profile", stored!.ProjectId);
        Assert.Equal(1, stored.IssueNumber);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetVariables_Empty_WhenNoRecord()
    {
        var bundle = await _manager.GetVariablesAsync("issue_none");
        Assert.Same(VariableBundle.Empty, bundle);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SetVariables_StoresAndRetrieves()
    {
        var bundle = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { x = 42 })));

        await _manager.SetVariablesAsync("issue_s", bundle);
        var got = await _manager.GetVariablesAsync("issue_s");

        Assert.NotNull(got.Vars);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SetVariables_ChineseValues_PersistAndRoundTripVerbatimFromSqlite()
    {
        // T-004 acceptance: a Chinese workflow variable persists to SQLite
        // and round-trips back as readable Chinese (not as \uXXXX escapes).
        // The set path goes through VariableBundle.ToJson() which uses JSON.Options
        // (the unified facade); the get path reads back the raw SQLite TEXT and
        // deserializes it through VariableBundle.FromJson. Verify both:
        //   1. the persisted SQLite TEXT contains the verbatim characters
        //   2. the readback yields a VariableBundle whose Vars contains the same
        //      characters (no \uXXXX escapes appear at any step).
        var bundle = new VariableBundle(
            Vars: JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                greeting = "中文变量值",
                description = "用于测试中文变量持久化",
                stageName = "构建",
            })).RootElement);

        await _manager.SetVariablesAsync("issue_zh", bundle);

        await using (var db = new MohistDbContext(_options))
        {
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"Variables\" FROM \"IssueWorkflowProfiles\" WHERE \"IssueId\" = $id";
            var p = cmd.CreateParameter();
            p.ParameterName = "$id";
            p.Value = "issue_zh";
            cmd.Parameters.Add(p);

            var persisted = (string?)await cmd.ExecuteScalarAsync();
            Assert.NotNull(persisted);
            Assert.Contains("中文变量值", persisted);
            Assert.Contains("用于测试中文变量持久化", persisted);
            Assert.Contains("构建", persisted);
            Assert.DoesNotContain("\\u4e2d", persisted);
            Assert.DoesNotContain("\\u6587", persisted);
            Assert.DoesNotContain("\\u6784", persisted);
        }

        var got = await _manager.GetVariablesAsync("issue_zh");
        Assert.NotNull(got.Vars);
        var raw = got.Vars!.Value.GetRawText();
        Assert.Contains("\"greeting\":\"中文变量值\"", raw);
        Assert.Contains("\"description\":\"用于测试中文变量持久化\"", raw);
        Assert.Contains("\"stageName\":\"构建\"", raw);
    }

    // ===================== helpers =====================

    private class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;
        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;
        public MohistDbContext CreateDbContext() => new(_options);
    }
}
