using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.SystemSpecs;

public class DatabaseInitializationSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Migrate_WhenEmptyDatabase_CreatesAllTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MohistDbContext(options);
        db.Database.Migrate();

        Assert.True(await TableExistsAsync(connection, "AgentSessions"));
        Assert.True(await TableExistsAsync(connection, "AgentSessionRuntimeEvents"));
        Assert.True(await ColumnExistsAsync(connection, "WorkflowRuns", "ETag"));
        Assert.True(await TableExistsAsync(connection, "OrleansQuery"));
        Assert.True(await TableExistsAsync(connection, "OrleansRemindersTable"));
        Assert.True(await OrleansQueryExistsAsync(connection, "UpsertReminderRowKey"));
        Assert.True(await IndexExistsAsync(connection, "IX_AgentSessions_WorkflowRunId_SessionName"));
        Assert.False(await IndexIsUniqueAsync(connection, "IX_AgentSessions_WorkflowRunId_WorkId"));
        Assert.True(await IndexExistsAsync(connection, "IX_AgentSessionRuntimeEvents_SessionId_Sequence"));
        Assert.True(await TableExistsAsync(connection, "__EFMigrationsHistory"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Migrate_WhenEmptyDatabase_CreatesProjectPromptTemplatesTableAndIndex()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MohistDbContext(options);
        db.Database.Migrate();

        Assert.True(await TableExistsAsync(connection, "ProjectPromptTemplates"));
        Assert.True(await IndexExistsAsync(connection, "IX_ProjectPromptTemplates_ProjectId_UpdatedAt"));
        Assert.Single(await RecordedMigrationsAsync(connection));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Migrate_WhenCalledTwiceOnProjectPromptTemplates_IsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db1 = new MohistDbContext(options))
        {
            db1.Database.Migrate();
        }

        await using (var db2 = new MohistDbContext(options))
        {
            db2.Database.Migrate();
        }

        Assert.True(await TableExistsAsync(connection, "ProjectPromptTemplates"));
        Assert.True(await IndexExistsAsync(connection, "IX_ProjectPromptTemplates_ProjectId_UpdatedAt"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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
            db1.Database.Migrate();
        }

        await using (var db2 = new MohistDbContext(options))
        {
            db2.Database.Migrate();
        }

        Assert.True(await TableExistsAsync(connection, "Projects"));
        Assert.True(await ColumnExistsAsync(connection, "WorkflowRuns", "ETag"));
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

    private static async Task<IReadOnlyList<string>> RecordedMigrationsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "MigrationId"
            FROM "__EFMigrationsHistory"
            ORDER BY "MigrationId";
            """;
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
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
            FROM pragma_index_list('AgentSessions')
            WHERE name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", name);
        var value = await command.ExecuteScalarAsync();
        return value is long unique && unique == 1;
    }
}
