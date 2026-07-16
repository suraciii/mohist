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

public class DeadLettersMigrationSpecs
{
    private const string PreviousMigrationId = "20260709000000_AddEventDeliveryDispatchedAt";
    private const string MigrationId = "20260709002625_AddDeadLetters";

    [Fact]
    public async Task DatabaseMigrate_CreatesDeadLettersTableWithRequiredColumns()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        await context.Database.MigrateAsync();

        Assert.True(await TableExistsAsync(database.Connection, "DeadLetters"));
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "DeadLetterId");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "Origin");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "Id");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "Source");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "EventId");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "Type");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "Time");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "SpecVersion");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "Subject");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "DataContentType");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "Data");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "ExtensionsJson");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "FailingHandler");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "ErrorMessage");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "ErrorStack");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "AttemptCount");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "DeadLetteredAt");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "Status");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "RedeliveryAttemptedAt");
        await AssertColumnExistsAsync(database.Connection, "DeadLetters", "ResolvedAt");
    }

    [Fact]
    public async Task Migration_CreatesBothDeadLettersIndexes()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        await context.Database.MigrateAsync();

        await AssertHasIndexAsync(database.Connection, "DeadLetters", "IX_DeadLetters_DeadLetteredAt", "DeadLetteredAt");
        await AssertHasIndexAsync(database.Connection, "DeadLetters", "IX_DeadLetters_FailingHandler_DeadLetteredAt", "FailingHandler", "DeadLetteredAt");
        await AssertHasIndexAsync(database.Connection, "DeadLetters", "IX_DeadLetters_Source_Id_FailingHandler", "Source", "Id", "FailingHandler");
    }

    [Fact]
    public async Task Migration_AppliesCleanly_OnDatabaseSeededWithPreviousMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigrationId);
        Assert.False(await TableExistsAsync(database.Connection, "DeadLetters"));

        await migrator.MigrateAsync(MigrationId);

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(MigrationId, applied);
        Assert.True(await TableExistsAsync(database.Connection, "DeadLetters"));
    }

    [Fact]
    public async Task MigratedSqliteTemplate_AlreadyContainsDeadLettersTable()
    {
        await using var database = CreateDatabase();
        MigratedSqliteTemplate.CopyTo(database.Connection);

        Assert.True(await TableExistsAsync(database.Connection, "DeadLetters"));
        await AssertHasIndexAsync(database.Connection, "DeadLetters", "IX_DeadLetters_DeadLetteredAt", "DeadLetteredAt");
        await AssertHasIndexAsync(database.Connection, "DeadLetters", "IX_DeadLetters_FailingHandler_DeadLetteredAt", "FailingHandler", "DeadLetteredAt");
    }

    private static async Task AssertColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM pragma_table_info('{tableName}') WHERE name = '{columnName}' LIMIT 1;";
        Assert.NotNull(await command.ExecuteScalarAsync());
    }

    private static async Task AssertHasIndexAsync(SqliteConnection connection, string tableName, string indexName, params string[] expectedColumns)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'index'
              AND tbl_name = $tableName
              AND name = $indexName
              AND sql IS NOT NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        command.Parameters.AddWithValue("$indexName", indexName);

        var sql = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.NotNull(sql);
        foreach (var column in expectedColumns)
        {
            Assert.Contains($"\"{column}\"", sql, StringComparison.Ordinal);
        }
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
