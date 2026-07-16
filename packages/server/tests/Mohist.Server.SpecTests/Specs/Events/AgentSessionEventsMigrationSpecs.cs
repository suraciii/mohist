using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

[Trait(Traits.Speed.Name, Traits.Speed.Service)]
[Trait(Traits.Sut.Name, Traits.Sut.System)]
public class AgentSessionEventsMigrationSpecs
{
    private const string PreviousMigrationId = "20260707120000_WorkflowWorkerAssignment";
    private const string MigrationId = "20260708053533_AddAgentSessionEvents";

    [Fact]
    public async Task DatabaseMigrate_CreatesAgentSessionEventsTableWithDispatchedAtAndUndeliveredIndex()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        await context.Database.MigrateAsync();

        Assert.True(await TableExistsAsync(database.Connection, "AgentSessionEvents"));
        await AssertColumnExistsAsync(database.Connection, "AgentSessionEvents", "DispatchedAt");
        await AssertHasPartialUndeliveredIndexAsync(database.Connection, "AgentSessionEvents");
        await AssertHasTypeTimeIndexAsync(database.Connection, "AgentSessionEvents");
    }

    [Fact]
    public async Task Migration_AppliesCleanly_OnDatabaseSeededWithPreviousMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigrationId);
        Assert.False(await TableExistsAsync(database.Connection, "AgentSessionEvents"));

        await migrator.MigrateAsync(MigrationId);

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(MigrationId, applied);
        Assert.True(await TableExistsAsync(database.Connection, "AgentSessionEvents"));
        await AssertHasPartialUndeliveredIndexAsync(database.Connection, "AgentSessionEvents");
    }

    private static async Task AssertColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM pragma_table_info('{tableName}') WHERE name = '{columnName}' LIMIT 1;";
        Assert.NotNull(await command.ExecuteScalarAsync());
    }

    private static async Task AssertHasPartialUndeliveredIndexAsync(SqliteConnection connection, string tableName)
    {
        var sql = await ReadIndexSqlAsync(connection, tableName);
        Assert.Contains(sql, statement => statement.Contains("WHERE \"DispatchedAt\" IS NULL", StringComparison.Ordinal));
    }

    private static async Task AssertHasTypeTimeIndexAsync(SqliteConnection connection, string tableName)
    {
        var sql = await ReadIndexSqlAsync(connection, tableName);
        Assert.Contains(sql, statement => statement.Contains("IX_AgentSessionEvents_Type_Time", StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<string>> ReadIndexSqlAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'index'
              AND tbl_name = $tableName
              AND sql IS NOT NULL
            ORDER BY name;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);

        var results = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(reader.GetString(0));
        return results;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new TestDatabase(connection, options);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public TestDatabase(SqliteConnection connection, DbContextOptions<MohistDbContext> options)
        {
            Connection = connection;
            _options = options;
        }

        public SqliteConnection Connection { get; }

        public MohistDbContext CreateDbContext() => new(_options);

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }
}