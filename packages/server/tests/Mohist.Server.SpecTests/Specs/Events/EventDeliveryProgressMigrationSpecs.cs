using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public class EventDeliveryProgressMigrationSpecs
{
    private const string PreviousMigrationId = "20260707120000_WorkflowWorkerAssignment";
    private const string MigrationId = "20260708015352_AddEventDeliveryProgressAndDeadLetters";

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task DatabaseMigrate_CreatesPartialUndeliveredIndexes_OnAllThreeEventTables()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        await context.Database.MigrateAsync();

        await AssertHasPartialUndeliveredIndexAsync(database.Connection, "WorkflowRunEvents");
        await AssertHasPartialUndeliveredIndexAsync(database.Connection, "IssueEvents");
        await AssertHasPartialUndeliveredIndexAsync(database.Connection, "EpicEvents");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task DatabaseMigrate_CreatesTypeTimeIndexes_OnWorkflowAndIssueOnly()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        await context.Database.MigrateAsync();

        Assert.True(await IndexExistsAsync(database.Connection, "IX_WorkflowRunEvents_Type_Time"));
        Assert.True(await IndexExistsAsync(database.Connection, "IX_IssueEvents_Type_Time"));
        Assert.False(await IndexExistsAsync(database.Connection, "IX_EpicEvents_Type_Time"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Migration_AppliesCleanly_OnDatabaseSeededWithPreviousMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigrationId);
        await AssertDispatchedAtColumnMissingAsync(database.Connection, "WorkflowRunEvents");
        await AssertDispatchedAtColumnMissingAsync(database.Connection, "IssueEvents");
        await AssertDispatchedAtColumnMissingAsync(database.Connection, "EpicEvents");

        await migrator.MigrateAsync(MigrationId);

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(MigrationId, applied);
        await AssertHasPartialUndeliveredIndexAsync(database.Connection, "WorkflowRunEvents");
        await AssertHasPartialUndeliveredIndexAsync(database.Connection, "IssueEvents");
        await AssertHasPartialUndeliveredIndexAsync(database.Connection, "EpicEvents");
        Assert.True(await TableExistsAsync(database.Connection, "DeadLetters"));
    }

    private static async Task AssertHasPartialUndeliveredIndexAsync(SqliteConnection connection, string tableName)
    {
        var sql = await ReadIndexSqlAsync(connection, tableName);
        Assert.Contains(sql, statement => statement.Contains("WHERE \"DispatchedAt\" IS NULL", StringComparison.Ordinal));
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

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'index' AND name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task AssertDispatchedAtColumnMissingAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""SELECT 1 FROM pragma_table_info('{tableName}') WHERE name = 'DispatchedAt' LIMIT 1;""";
        Assert.Null(await command.ExecuteScalarAsync());
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
