using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
