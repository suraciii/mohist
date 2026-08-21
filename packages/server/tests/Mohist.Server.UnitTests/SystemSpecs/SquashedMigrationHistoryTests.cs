using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class SquashedMigrationHistoryTests
{
    [Fact]
    public async Task Initialize_WhenDatabasePredatesSquashFloor_FailsFast()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await CreateHistoryTableAsync(connection, "20260605025642_InitialSchema");

        var services = new ServiceCollection()
            .AddDbContext<MohistDbContext>(options => options.UseSqlite(connection))
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .BuildServiceProvider();
        await using (services)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new MohistDatabaseInitializer().InitializeAsync(services, CancellationToken.None));
            Assert.Contains(SquashedMigrationHistory.FloorId, error.Message);
            Assert.Contains("20260605025642_InitialSchema", error.Message);
        }
    }

    [Fact]
    public async Task Initialize_WhenDatabaseAtSquashFloor_RewritesHistoryAppliesTailAndKeepsData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        // Build the real floor schema through the baseline (proven
        // schema-equivalent to the deleted chain), then rewrite the history
        // to look like a pre-squash database.
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using (var setup = new MohistDbContext(options))
        {
            await setup.GetService<IMigrator>().MigrateAsync(SquashedMigrationHistory.BaselineId);
        }

        await ExecuteAsync(connection, """
            CREATE TABLE "AgentSubscriptions" ("Id" TEXT NOT NULL PRIMARY KEY);
            CREATE TABLE "RunnerWorks" ("Id" INTEGER NOT NULL PRIMARY KEY);
            INSERT INTO "Projects" ("Id", "Name", "RepositoriesJson", "CreatedAt", "UpdatedAt")
            VALUES ('project_kept', 'kept', '[{"name":"repo","gitUrl":"git@example.com:repo.git","baseBranch":"main","isDefault":true}]', '2026-09-06T00:00:00+00:00', '2026-09-06T00:00:00+00:00');
            DELETE FROM "__EFMigrationsHistory";
            """);
        await CreateHistoryRowAsync(connection, SquashedMigrationHistory.FloorId);

        var services = new ServiceCollection()
            .AddDbContext<MohistDbContext>(options => options.UseSqlite(connection))
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .BuildServiceProvider();
        await using (services)
        {
            await new MohistDatabaseInitializer().InitializeAsync(services, CancellationToken.None);
        }

        var applied = await ReadHistoryAsync(connection);
        Assert.Contains(SquashedMigrationHistory.BaselineId, applied);
        Assert.Contains("20260911000000_DropVestigialTables", applied);
        Assert.DoesNotContain(SquashedMigrationHistory.FloorId, applied);
        Assert.DoesNotContain("20260605025642_InitialSchema", applied);

        Assert.False(await TableExistsAsync(connection, "AgentSubscriptions"));
        Assert.False(await TableExistsAsync(connection, "RunnerWorks"));
        Assert.Equal(1L, await ScalarAsync(connection, """SELECT COUNT(*) FROM "Projects" WHERE "Id" = 'project_kept';"""));
        Assert.True(await ScalarAsync(connection, """SELECT COUNT(*) FROM "OrleansQuery";""") > 0);
    }

    private static async Task CreateHistoryTableAsync(SqliteConnection connection, params string[] migrationIds)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL);
            """);
        foreach (var id in migrationIds)
        {
            await CreateHistoryRowAsync(connection, id);
        }
    }

    private static async Task CreateHistoryRowAsync(SqliteConnection connection, string migrationId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ($id, '10.0.8');
            """;
        command.Parameters.AddWithValue("$id", migrationId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadHistoryAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";""";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name)
        => await ScalarAsync(
               connection,
               $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{name}';") == 1;

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
