using System.Text.Json;
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
    private const string ProjectId = "proj_profile";
    private readonly TestSqliteDatabase _database;
    private readonly IssueWorkflowProfileManager _manager;

    public IssueWorkflowProfileManagerSpecs()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
        _manager = new IssueWorkflowProfileManager(new TestDbContextFactory(_database.Options));

        using var db = new MohistDbContext(_database.Options);
        var issueNumbers = new[] { 1, 2, 4, 5, 6, 7, 8, 9 };
        foreach (var issueNumber in issueNumbers)
        {
            db.Issues.Add(new IssueRow
            {
                State = JSON.Serialize(new
                {
                    projectId = ProjectId,
                    number = issueNumber,
                }),
            });
        }
        db.SaveChanges();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    // ===================== Template =====================

    [Fact]
    public async Task GetProfile_ReturnsNull_WhenNoRecord()
    {
        var profile = await _manager.GetProfileAsync(ProjectId, 99);
        Assert.Null(profile);
    }

    [Fact]
    public async Task UpdateTemplate_ProjectReference_StoresSourceTemplateId()
    {
        var row = await _manager.UpdateTemplateAsync(ProjectId, 1,
            new IssueTemplateUpdateRequest(ProjectTemplateId: "some-template"));

        Assert.Equal(ProjectId, row.ProjectId);
        Assert.Equal(1, row.IssueNumber);
        Assert.Equal("some-template", row.SourceTemplateId);
        Assert.Null(row.Template);

        var stored = await _manager.GetProfileAsync(ProjectId, 1);
        Assert.Equal(ProjectId, stored!.ProjectId);
        Assert.Equal(1, stored.IssueNumber);
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
        var row = await _manager.UpdateTemplateAsync(ProjectId, 2,
            new IssueTemplateUpdateRequest(Template: yaml));

        Assert.Null(row.SourceTemplateId);
        Assert.NotNull(row.Template);

        var def = await _manager.GetTemplateAsync(ProjectId, 2);
        Assert.NotNull(def);
        Assert.Equal("my-custom", def.Id);
    }

    [Fact]
    public async Task UpdateTemplate_BothSet_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.UpdateTemplateAsync(ProjectId, 3,
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
        await _manager.UpdateTemplateAsync(ProjectId, 4,
            new IssueTemplateUpdateRequest(ProjectTemplateId: "t1"));
        // then clear
        var row = await _manager.UpdateTemplateAsync(ProjectId, 4,
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
        await _manager.UpdateTemplateAsync(ProjectId, 5, new IssueTemplateUpdateRequest(Template: yaml1));
        await _manager.UpdateTemplateAsync(ProjectId, 5, new IssueTemplateUpdateRequest(Template: yaml2));

        var def = await _manager.GetTemplateAsync(ProjectId, 5);
        Assert.Single(def!.Stages);
        Assert.Equal("s2", def.Stages[0].Stage);
    }

    // ===================== Variables =====================

    [Fact]
    public async Task GetVariables_Empty_WhenNoRecord()
    {
        var bundle = await _manager.GetVariablesAsync(ProjectId, 99);
        Assert.Same(VariableBundle.Empty, bundle);
    }

    [Fact]
    public async Task SetVariables_StoresAndRetrieves()
    {
        var bundle = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { x = 42 })));

        await _manager.SetVariablesAsync(ProjectId, 6, bundle);
        var got = await _manager.GetVariablesAsync(ProjectId, 6);

        Assert.NotNull(got.Vars);
    }

    [Fact]
    public async Task PatchVariables_DeepMergesAcrossCalls()
    {
        var initial = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(
                new { agent = new { model = "gpt-5.6" } })));
        await _manager.SetVariablesAsync(ProjectId, 7, initial);

        var patch = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(
                new { agent = new { model = "gpt-4o", variant = "high" } })));
        await _manager.PatchVariablesAsync(ProjectId, 7, patch);

        var result = await _manager.GetVariablesAsync(ProjectId, 7);
        using var doc = JsonDocument.Parse(result.Vars!.Value.GetRawText());
        var agent = doc.RootElement.GetProperty("agent");

        Assert.False(agent.TryGetProperty("type", out _));
        Assert.Equal("gpt-4o", agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task VariableOperations_AreIsolatedFromTemplateOperations()
    {
        // set variables
        var bundle = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { keep = 1 })));
        await _manager.SetVariablesAsync(ProjectId, 8, bundle);

        // set template
        await _manager.UpdateTemplateAsync(ProjectId, 8,
            new IssueTemplateUpdateRequest(ProjectTemplateId: "some-tmpl"));

        // variables still intact
        var got = await _manager.GetVariablesAsync(ProjectId, 8);
        Assert.NotNull(got.Vars);
        using var doc = JsonDocument.Parse(got.Vars.Value.GetRawText());
        Assert.Equal(1, doc.RootElement.GetProperty("keep").GetInt32());
    }

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

        await _manager.SetVariablesAsync(ProjectId, 9, bundle);

        await using (var db = new MohistDbContext(_database.Options))
        {
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"Variables\" FROM \"IssueWorkflowProfiles\" WHERE \"ProjectId\" = $projectId AND \"IssueNumber\" = $issueNumber";
            var project = cmd.CreateParameter();
            project.ParameterName = "$projectId";
            project.Value = ProjectId;
            cmd.Parameters.Add(project);
            var issue = cmd.CreateParameter();
            issue.ParameterName = "$issueNumber";
            issue.Value = 9;
            cmd.Parameters.Add(issue);

            var persisted = (string?)await cmd.ExecuteScalarAsync();
            Assert.NotNull(persisted);
            Assert.Contains("中文变量值", persisted);
            Assert.Contains("用于测试中文变量持久化", persisted);
            Assert.Contains("构建", persisted);
            Assert.DoesNotContain("\\u4e2d", persisted);
            Assert.DoesNotContain("\\u6587", persisted);
            Assert.DoesNotContain("\\u6784", persisted);
        }

        var got = await _manager.GetVariablesAsync(ProjectId, 9);
        Assert.NotNull(got.Vars);
        var raw = got.Vars!.Value.GetRawText();
        Assert.Contains("\"greeting\":\"中文变量值\"", raw);
        Assert.Contains("\"description\":\"用于测试中文变量持久化\"", raw);
        Assert.Contains("\"stageName\":\"构建\"", raw);
    }

}
