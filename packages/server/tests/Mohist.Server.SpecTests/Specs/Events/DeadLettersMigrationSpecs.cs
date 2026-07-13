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
public class DeadLettersMigrationSpecs
{
    private const string PreviousMigrationId = "20260709000000_AddEventDeliveryDispatchedAt";
    private const string MigrationId = "20260709002625_AddDeadLetters";
    private const string RecoveryMigrationId = "20260711041122_HardenDeadLetterRecovery";

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
    public void FirstMigration_Up_CreatesTableWithPrimaryKeyAndBothIndexes()
    {
        var source = File.ReadAllText(MigrationSourcePath(MigrationId));

        Assert.Contains("CreateTable", source, StringComparison.Ordinal);
        Assert.Contains("name: \"DeadLetters\"", source, StringComparison.Ordinal);
        Assert.Contains("table.PrimaryKey(\"PK_DeadLetters\", x => x.DeadLetterId);", source, StringComparison.Ordinal);
        Assert.Contains("IX_DeadLetters_DeadLetteredAt", source, StringComparison.Ordinal);
        Assert.Contains("IX_DeadLetters_FailingHandler_DeadLetteredAt", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryMigration_AddsStateAndNaturalKey()
    {
        var source = File.ReadAllText(MigrationSourcePath(RecoveryMigrationId));

        Assert.Contains("RedeliveryAttemptedAt", source, StringComparison.Ordinal);
        Assert.Contains("ResolvedAt", source, StringComparison.Ordinal);
        Assert.Contains("Status", source, StringComparison.Ordinal);
        Assert.Contains("defaultValue: \"Pending\"", source, StringComparison.Ordinal);
        Assert.Contains("IX_DeadLetters_Source_Id_FailingHandler", source, StringComparison.Ordinal);
        Assert.Contains("unique: true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelSnapshot_IncludesDeadLetterRowWithBothIndexes()
    {
        var source = File.ReadAllText(SnapshotPath());

        Assert.Contains("\"Mohist.Server.Infrastructure.Data.Events.DeadLetterRow\"", source, StringComparison.Ordinal);
        Assert.Contains("ToTable(\"DeadLetters\"", source, StringComparison.Ordinal);
        Assert.Contains("HasKey(\"DeadLetterId\");", source, StringComparison.Ordinal);
        Assert.Contains("HasIndex(\"DeadLetteredAt\");", source, StringComparison.Ordinal);
        Assert.Contains("HasIndex(\"FailingHandler\", \"DeadLetteredAt\");", source, StringComparison.Ordinal);
        Assert.Contains("HasIndex(\"Source\", \"Id\", \"FailingHandler\")", source, StringComparison.Ordinal);
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

    private static string MigrationSourcePath(string migrationId) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "Mohist.Server", "Infrastructure", "Data", "Migrations",
        $"{migrationId}.cs"));

    private static string SnapshotPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "Mohist.Server", "Infrastructure", "Data", "Migrations",
        "MohistDbContextModelSnapshot.cs"));

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
