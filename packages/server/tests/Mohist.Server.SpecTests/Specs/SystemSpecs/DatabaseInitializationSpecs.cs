using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Xunit;
using Mohist.Server.TestSupport;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

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
        db.Database.Migrate();

        Assert.True(await TableExistsAsync(connection, "AgentSessions"));
        Assert.False(await TableExistsAsync(connection, "AgentSessionLabels"));
        Assert.False(await TableExistsAsync(connection, "AgentSessionRuntimeEvents"));
        Assert.False(await TableExistsAsync(connection, "AgentSessionTranscriptSegments"));
        Assert.True(await TableExistsAsync(connection, "AgentSessionTranscriptTurns"));
        Assert.True(await TableExistsAsync(connection, "AgentSessionTranscriptParts"));
        Assert.True(await ColumnExistsAsync(connection, "WorkflowRuns", "ETag"));
        Assert.True(await TableExistsAsync(connection, "OrleansQuery"));
        Assert.True(await TableExistsAsync(connection, "OrleansRemindersTable"));
        Assert.True(await OrleansQueryExistsAsync(connection, "UpsertReminderRowKey"));
        Assert.True(await IndexExistsAsync(connection, "IX_AgentSessionTranscriptTurns_SessionId_Sequence"));
        Assert.True(await IndexExistsAsync(connection, "IX_AgentSessionTranscriptParts_TurnId_Type_CorrelationKey"));
        Assert.True(await TableExistsAsync(connection, "__EFMigrationsHistory"));
    }

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
        var migrations = await RecordedMigrationsAsync(connection);
        Assert.NotEmpty(migrations);
        Assert.Contains(SquashedMigrationHistory.BaselineId, migrations);
    }

    [Fact]
    public async Task Migrate_WhenEmptyDatabase_AppliesSquashedBaseline()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new MohistDbContext(options);
        await db.Database.MigrateAsync();

        Assert.True(await ColumnExistsAsync(connection, "ProjectWorkflowProfiles", "DisabledWorkflowProfileIds"));
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(SquashedMigrationHistory.BaselineId, applied);
        Assert.DoesNotContain("20260629000000_AddDisabledWorkflowProfileIds", applied);
    }

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

    [Fact]
    public async Task MohistDatabaseInitializer_AppliesEfMigrationsToEmptyDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection()
            .AddDbContext<MohistDbContext>(options => options.UseSqlite(connection))
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .BuildServiceProvider();

        await using (services)
        {
            var initializer = new MohistDatabaseInitializer();
            await initializer.InitializeAsync(services, CancellationToken.None);

            Assert.True(await TableExistsAsync(connection, "Projects"));
            Assert.True(await TableExistsAsync(connection, "WorkflowRuns"));
            Assert.True(await TableExistsAsync(connection, "__EFMigrationsHistory"));
            var migrations = await RecordedMigrationsAsync(connection);
            Assert.NotEmpty(migrations);
            Assert.Contains(SquashedMigrationHistory.BaselineId, migrations);
        }
    }

    [Fact]
    public async Task MohistDatabaseInitializer_IsIdempotentAcrossRepeatedInvocations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection()
            .AddDbContext<MohistDbContext>(options => options.UseSqlite(connection))
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .BuildServiceProvider();

        await using (services)
        {
            var initializer = new MohistDatabaseInitializer();
            await initializer.InitializeAsync(services, CancellationToken.None);
            await initializer.InitializeAsync(services, CancellationToken.None);

            Assert.True(await TableExistsAsync(connection, "Projects"));
            Assert.True(await ColumnExistsAsync(connection, "WorkflowRuns", "ETag"));
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
