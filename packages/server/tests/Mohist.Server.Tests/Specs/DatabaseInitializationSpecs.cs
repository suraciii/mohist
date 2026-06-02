using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class DatabaseInitializationSpecs
{
    [Fact]
    public async Task Migrate_WhenEmptyDatabase_CreatesAllTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MohistDbContext(options);
        MohistDatabaseMigrator.Migrate(db);

        Assert.True(await TableExistsAsync(connection, "WorkflowAgentSessions"));
        Assert.True(await TableExistsAsync(connection, "WorkflowAgentSessionEvents"));
        Assert.True(await ColumnExistsAsync(connection, "workflow_runs", "ETag"));
        Assert.True(await TableExistsAsync(connection, "OrleansQuery"));
        Assert.True(await TableExistsAsync(connection, "OrleansRemindersTable"));
        Assert.True(await OrleansQueryExistsAsync(connection, "UpsertReminderRowKey"));
        Assert.True(await IndexExistsAsync(connection, "IX_WorkflowAgentSessions_WorkflowRunId_SessionName"));
        Assert.False(await IndexIsUniqueAsync(connection, "IX_WorkflowAgentSessions_WorkflowRunId_WorkId"));
        Assert.True(await IndexExistsAsync(connection, "IX_WorkflowAgentSessionEvents_SessionId_Sequence"));
        Assert.True(await TableExistsAsync(connection, "__EFMigrationsHistory"));
    }

    [Fact]
    public async Task Migrate_WhenCalledTwice_IsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db1 = new MohistDbContext(options))
        {
            MohistDatabaseMigrator.Migrate(db1);
        }

        await using (var db2 = new MohistDbContext(options))
        {
            MohistDatabaseMigrator.Migrate(db2);
        }

        Assert.True(await TableExistsAsync(connection, "Projects"));
        Assert.True(await ColumnExistsAsync(connection, "workflow_runs", "ETag"));
    }

    [Fact]
    public async Task MohistMigrator_WhenLegacyDatabaseHasTablesWithoutMigrationHistory_CompletesInitialMigration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                CREATE TABLE "Configs" (
                    "Key" TEXT NOT NULL CONSTRAINT "PK_Configs" PRIMARY KEY,
                    "Value" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MohistDbContext(options);
        MohistDatabaseMigrator.Migrate(db);

        Assert.True(await TableExistsAsync(connection, "Projects"));
        Assert.True(await TableExistsAsync(connection, "WorkflowAgentSessions"));
        Assert.True(await ColumnExistsAsync(connection, "workflow_runs", "ETag"));
        Assert.True(await TableExistsAsync(connection, "OrleansRemindersTable"));
        Assert.True(await MigrationRecordedAsync(connection, "20260530040459_InitialCreate"));
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

    private static async Task<bool> MigrationRecordedAsync(SqliteConnection connection, string migrationId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = $migrationId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$migrationId", migrationId);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""SELECT 1 FROM pragma_table_info('{tableName}') WHERE name = $columnName LIMIT 1;""";
        command.Parameters.AddWithValue("$columnName", columnName);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<bool> OrleansQueryExistsAsync(SqliteConnection connection, string queryKey)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM OrleansQuery
            WHERE QueryKey = $queryKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$queryKey", queryKey);
        return await command.ExecuteScalarAsync() is not null;
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

    private static async Task<bool> IndexIsUniqueAsync(SqliteConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [unique]
            FROM pragma_index_list('WorkflowAgentSessions')
            WHERE name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", name);
        var value = await command.ExecuteScalarAsync();
        return value is long unique && unique == 1;
    }
}
