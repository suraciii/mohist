using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Profile;

public sealed class IssueVariableStoreSpecs : IAsyncLifetime
{
    private const string ProjectId = "proj_issue_variables";
    private readonly TestSqliteDatabase _database;
    private readonly TestDbContextFactory _dbFactory;
    private readonly IssueVariableStore _store;

    public IssueVariableStoreSpecs()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
        _dbFactory = new TestDbContextFactory(_database.Options);
        _store = new IssueVariableStore(_dbFactory);

        using var db = new MohistDbContext(_database.Options);
        foreach (var issueNumber in Enumerable.Range(1, 9))
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

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task GetVariables_ReturnsEmpty_WhenNotSet()
    {
        var bundle = await _store.GetVariablesAsync(ProjectId, 1);

        Assert.Same(VariableBundle.Empty, bundle);
    }

    [Fact]
    public async Task SetVariables_StoresBundle()
    {
        var bundle = new VariableBundle(JsonSerializer.SerializeToElement(new { value = 42 }));

        var written = await _store.SetVariablesAsync(ProjectId, 2, bundle);
        var persisted = await _store.GetVariablesAsync(ProjectId, 2);

        Assert.Equal(written.ToJson(), persisted.ToJson());
    }

    [Fact]
    public async Task PatchVariables_DeepMergesNestedFieldsAndUnknownStage()
    {
        var initial = new VariableBundle(
            JsonSerializer.SerializeToElement(new
            {
                agent = new { model = "gpt-5", variant = "low" },
                settings = new { keep = true },
            }),
            new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["plan"] = new(JsonSerializer.SerializeToElement(new { existing = 1 })),
            });
        await _store.SetVariablesAsync(ProjectId, 3, initial);

        var patch = new VariableBundle(
            JsonSerializer.SerializeToElement(new { agent = new { variant = "high" } }),
            new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
            {
                ["build"] = new(JsonSerializer.SerializeToElement(new { added = 2 })),
            });

        var merged = await _store.PatchVariablesAsync(ProjectId, 3, patch);

        var agent = merged.Vars!.Value.GetProperty("agent");
        Assert.Equal("gpt-5", agent.GetProperty("model").GetString());
        Assert.Equal("high", agent.GetProperty("variant").GetString());
        Assert.True(merged.Vars.Value.GetProperty("settings").GetProperty("keep").GetBoolean());
        Assert.Equal(1, merged.Stages!["plan"].Vars!.Value.GetProperty("existing").GetInt32());
        Assert.Equal(2, merged.Stages["build"].Vars!.Value.GetProperty("added").GetInt32());
    }

    [Fact]
    public async Task SetVariables_RejectsRootAgentRuntimeWithoutPersisting()
    {
        var bundle = new VariableBundle(JsonSerializer.SerializeToElement(new
        {
            agent = new { model = "gpt-5", runtime = "opencode" },
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetVariablesAsync(ProjectId, 4, bundle));

        Assert.Same(VariableBundle.Empty, await _store.GetVariablesAsync(ProjectId, 4));
    }

    [Fact]
    public async Task PatchVariables_RejectsStageAgentRuntimeWithoutChangingPersistedBundle()
    {
        var initial = new VariableBundle(JsonSerializer.SerializeToElement(new { keep = 1 }));
        await _store.SetVariablesAsync(ProjectId, 5, initial);
        var patch = new VariableBundle(null, new Dictionary<string, StageVariables>
        {
            ["plan"] = new(JsonSerializer.SerializeToElement(new
            {
                agent = new { runtime = "opencode" },
            })),
        });

        await Assert.ThrowsAsync<ArgumentException>(() => _store.PatchVariablesAsync(ProjectId, 5, patch));

        var persisted = await _store.GetVariablesAsync(ProjectId, 5);
        Assert.Equal(1, persisted.Vars!.Value.GetProperty("keep").GetInt32());
        Assert.Null(persisted.Stages);
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(7, true)]
    public async Task SetVariables_RejectsNonObjectVarsWithoutPersisting(int issueNumber, bool invalidStage)
    {
        var invalid = JsonSerializer.SerializeToElement(1);
        var bundle = invalidStage
            ? new VariableBundle(null, new Dictionary<string, StageVariables>
            {
                ["plan"] = new(invalid),
            })
            : new VariableBundle(invalid);

        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetVariablesAsync(ProjectId, issueNumber, bundle));

        Assert.Same(VariableBundle.Empty, await _store.GetVariablesAsync(ProjectId, issueNumber));
    }

    [Fact]
    public async Task SetVariables_ChineseValues_PersistAndRoundTripVerbatimFromSqlite()
    {
        var bundle = new VariableBundle(JsonSerializer.SerializeToElement(new
        {
            greeting = "中文变量值",
            description = "用于测试中文变量持久化",
            stageName = "构建",
        }));

        await _store.SetVariablesAsync(ProjectId, 9, bundle);

        await using (var db = new MohistDbContext(_database.Options))
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"Variables\" FROM \"IssueWorkflowProfiles\" WHERE \"ProjectId\" = $projectId AND \"IssueNumber\" = $issueNumber";
            var project = command.CreateParameter();
            project.ParameterName = "$projectId";
            project.Value = ProjectId;
            command.Parameters.Add(project);
            var issue = command.CreateParameter();
            issue.ParameterName = "$issueNumber";
            issue.Value = 9;
            command.Parameters.Add(issue);

            var persisted = (string?)await command.ExecuteScalarAsync();
            Assert.NotNull(persisted);
            Assert.Contains("中文变量值", persisted);
            Assert.Contains("用于测试中文变量持久化", persisted);
            Assert.Contains("构建", persisted);
            Assert.DoesNotContain("\\u4e2d", persisted);
            Assert.DoesNotContain("\\u6587", persisted);
            Assert.DoesNotContain("\\u6784", persisted);
        }

        var read = await _store.GetVariablesAsync(ProjectId, 9);
        var raw = read.Vars!.Value.GetRawText();
        Assert.Contains("\"greeting\":\"中文变量值\"", raw);
        Assert.Contains("\"description\":\"用于测试中文变量持久化\"", raw);
        Assert.Contains("\"stageName\":\"构建\"", raw);
    }
}
