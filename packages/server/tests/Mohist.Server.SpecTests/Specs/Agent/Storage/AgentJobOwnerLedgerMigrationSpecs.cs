using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Storage;

public sealed class AgentJobOwnerLedgerMigrationSpecs
{
    private const string BeforeMigration = "20260729000000_AddConnectionSecrets";
    private const string Migration = "20260729000000_AgentJobOwnerLedger";

    [Fact]
    public async Task Up_AssignsOneExecutionTimestampToEveryLegacyPendingRow()
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        MigratedSqliteTemplate.CopyTo(database.Keeper, BeforeMigration);

        await using (var db = database.CreateContext())
        {
            await InsertStateAsync(db, "pending-a", PendingState("first"));
            await InsertStateAsync(db, "pending-b", PendingState("second"));
            await db.Database.GetService<IMigrator>().MigrateAsync(Migration);
        }

        await using var migrated = database.CreateContext();
        var readySince = await ReadColumnAsync(migrated, "ReadySince");

        Assert.Equal(2, readySince.Count);
        Assert.All(readySince, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Single(readySince.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Up_MalformedNonterminalStateFailsWithoutChangingLegacyRow()
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        MigratedSqliteTemplate.CopyTo(database.Keeper, BeforeMigration);
        const string malformed = "{\"status\":\"running\",\"runnerId\":\"runner-a\",\"runningSince\":\"2026-01-01T00:00:00Z\",\"input\":{\"prompt\":\"run\"}}";

        await using (var db = database.CreateContext())
        {
            Assert.DoesNotContain("Revision", await ReadColumnAsync(db, "name", "PRAGMA table_info('AgentJobs')"));
            await InsertStateAsync(db, "malformed", malformed);
            var error = await Assert.ThrowsAsync<SqliteException>(
                () => db.Database.GetService<IMigrator>().MigrateAsync(Migration));
            Assert.Contains("CHECK constraint failed", error.Message, StringComparison.Ordinal);
        }

        await using var unchanged = database.CreateContext();
        Assert.DoesNotContain(Migration, await unchanged.Database.GetAppliedMigrationsAsync());
        Assert.Equal(malformed, Assert.Single(await ReadColumnAsync(unchanged, "State")));
        Assert.DoesNotContain("Revision", await ReadColumnAsync(unchanged, "name", "PRAGMA table_info('AgentJobs')"));
    }

    [Fact]
    public async Task Up_RunningFallbackRebuildsCompleteDispatchEnvelope()
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        MigratedSqliteTemplate.CopyTo(database.Keeper, BeforeMigration);
        const string state = "{\"status\":\"running\",\"runnerId\":\"runner-a\",\"workId\":\"work-a\",\"runningSince\":\"2026-01-01T00:00:00Z\",\"input\":{\"prompt\":\"run\",\"projectId\":\"project-a\",\"agentId\":\"agent-a\",\"workspacePath\":\"/work/a\",\"agentInstructions\":\"be exact\",\"model\":\"model-a\",\"variant\":\"fast\",\"runtime\":\"opencode\",\"skills\":[\"review\",\"test\"]}}";

        await using (var db = database.CreateContext())
        {
            await InsertStateAsync(db, "running-full", state);
            await db.Database.GetService<IMigrator>().MigrateAsync(Migration);
        }

        await using var migrated = database.CreateContext();
        var dispatchJson = Assert.Single(await ReadColumnAsync(
            migrated,
            "DispatchJson",
            "SELECT \"DispatchJson\" FROM \"AgentJobs\" WHERE \"JobKey\" = 'running-full'"));
        var dispatch = JsonSerializer.Deserialize<WorkDispatch>(dispatchJson!, JSON.Options);

        Assert.NotNull(dispatch);
        Assert.Equal("work-a", dispatch!.WorkId);
        using var with = JsonDocument.Parse(dispatch.With!);
        Assert.Equal("run", with.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("be exact", with.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("model-a", with.RootElement.GetProperty("model").GetString());
        Assert.Equal("fast", with.RootElement.GetProperty("variant").GetString());
        Assert.Equal("opencode", with.RootElement.GetProperty("runtime").GetString());
        Assert.Equal(["review", "test"], with.RootElement.GetProperty("skills").EnumerateArray().Select(value => value.GetString()));
        using var variables = JsonDocument.Parse(dispatch.Variables!);
        Assert.Equal("/work/a", variables.RootElement.GetProperty("workspace").GetProperty("path").GetString());
    }

    [Fact]
    public async Task Up_PascalCaseRunningStateIsVisibleToOwnerLedgerQueries()
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        MigratedSqliteTemplate.CopyTo(database.Keeper, BeforeMigration);
        const string state = "{\"Status\":\"Running\",\"RunnerId\":\"runner-pascal\",\"WorkId\":\"work-pascal\",\"RunningSince\":\"2026-01-01T00:00:00Z\",\"Input\":{\"Prompt\":\"run\",\"ProjectId\":\"project-pascal\",\"AgentId\":\"agent-pascal\"}}";

        await using (var db = database.CreateContext())
        {
            await InsertStateAsync(db, "running-pascal", state);
            await db.Database.GetService<IMigrator>().MigrateAsync(Migration);
        }

        var store = new AgentJobStore(
            new TestDbContextFactory(database.Options),
            NullLogger<AgentJobStore>.Instance,
            new FakeTimeProvider());
        var running = await store.ListRunningForRunnerAsync("runner-pascal");

        var migrated = Assert.Single(running);
        Assert.Equal("running-pascal", migrated.JobKey);
        Assert.Equal("project-pascal", migrated.IssueProjectId);
    }

    private static Task InsertStateAsync(DbContext db, string jobKey, string state) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AgentJobs" ("JobKey", "State")
            VALUES ({jobKey}, {state});
            """);

    private static async Task<IReadOnlyList<string?>> ReadColumnAsync(
        DbContext db,
        string column,
        string? sql = null)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql ?? $"SELECT \"{column}\" FROM \"AgentJobs\" ORDER BY \"JobKey\"";
        await db.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string?>();
        while (await reader.ReadAsync())
            values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return values;
    }

    private static string PendingState(string prompt) =>
        "{\"status\":\"pending\",\"input\":{\"prompt\":\"" + prompt + "\",\"agentId\":\"agent-a\"}}";
}
