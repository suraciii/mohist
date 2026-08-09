using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.SpecTests.Support;
using System.Reflection;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Domain;

public sealed class RoutingRuleIdempotencyMigrationSpecs
{
    private const string PreviousMigration = "20260901000000_AddDeviceAuthorizations";
    private const string CurrentMigration = "20260902000000_AddRoutingRuleIdempotencyKey";
    private const string RollbackGuardTrigger = "__Mohist_RoutingRuleIdempotencyRollbackGuard";

    [Fact]
    public void Up_AddsNullableColumnAndFilteredUniqueIndex()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        Invoke("Up", builder);

        Assert.Collection(
            builder.Operations,
            operation =>
            {
                var nameIndexDrop = Assert.IsType<DropIndexOperation>(operation);
                Assert.Equal("UX_RoutingRules_ProjectId_Name", nameIndexDrop.Name);
                Assert.Equal("RoutingRules", nameIndexDrop.Table);
            },
            operation =>
            {
                var nameIndex = Assert.IsType<CreateIndexOperation>(operation);
                Assert.Equal("UX_RoutingRules_ProjectId_Name", nameIndex.Name);
                Assert.Equal("RoutingRules", nameIndex.Table);
                Assert.Equal(new[] { "ProjectId", "Name" }, nameIndex.Columns);
                Assert.True(nameIndex.IsUnique);
                Assert.Equal("\"Status\" <> 'deleted'", nameIndex.Filter);
            },
            operation =>
            {
                var column = Assert.IsType<AddColumnOperation>(operation);
                Assert.Equal("RoutingRules", column.Table);
                Assert.Equal("IdempotencyKey", column.Name);
                Assert.Equal(typeof(string), column.ClrType);
                Assert.True(column.IsNullable);
                Assert.Equal(256, column.MaxLength);
            },
            operation =>
            {
                var index = Assert.IsType<CreateIndexOperation>(operation);
                Assert.Equal("UX_RoutingRules_ProjectId_IdempotencyKey", index.Name);
                Assert.Equal("RoutingRules", index.Table);
                Assert.Equal(new[] { "ProjectId", "IdempotencyKey" }, index.Columns);
                Assert.True(index.IsUnique);
                Assert.Equal("\"IdempotencyKey\" IS NOT NULL", index.Filter);
            });
    }

    [Fact]
    public void Down_AddsDatabasePreflightBeforeAnyDestructiveOperation()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        Invoke("Down", builder);

        Assert.Collection(
            builder.Operations,
            operation =>
            {
                var sql = Assert.IsType<SqlOperation>(operation);
                Assert.Contains("CREATE TEMP TRIGGER", sql.Sql, StringComparison.Ordinal);
                Assert.Contains("UPDATE main.\"RoutingRules\"", sql.Sql, StringComparison.Ordinal);
                Assert.Contains("RAISE(ABORT", sql.Sql, StringComparison.Ordinal);
            },
            operation => Assert.Equal(
                "UX_RoutingRules_ProjectId_IdempotencyKey",
                Assert.IsType<DropIndexOperation>(operation).Name),
            operation => Assert.Equal(
                "UX_RoutingRules_ProjectId_Name",
                Assert.IsType<DropIndexOperation>(operation).Name),
            operation =>
            {
                var sql = Assert.IsType<SqlOperation>(operation);
                Assert.Contains("ALTER TABLE \"RoutingRules\" DROP COLUMN \"IdempotencyKey\"", sql.Sql, StringComparison.Ordinal);
            },
            operation =>
            {
                var nameIndex = Assert.IsType<CreateIndexOperation>(operation);
                Assert.Equal("UX_RoutingRules_ProjectId_Name", nameIndex.Name);
                Assert.Equal(new[] { "ProjectId", "Name" }, nameIndex.Columns);
                Assert.True(nameIndex.IsUnique);
                Assert.Null(nameIndex.Filter);
            });
    }

    [Fact]
    public async Task Down_WithoutIdempotencyFactsRevertsIndexesAndColumnOnSqlite()
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        await using var context = database.CreateContext();
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(CurrentMigration);
        Assert.Equal(CurrentMigration, await LatestMigrationIdAsync(connection));

        await migrator.MigrateAsync(PreviousMigration);
        Assert.Equal(PreviousMigration, await LatestMigrationIdAsync(connection));

        Assert.False(await ColumnExistsAsync(connection, "RoutingRules", "IdempotencyKey"));
        Assert.False(await IndexExistsAsync(connection, "UX_RoutingRules_ProjectId_IdempotencyKey"));
        Assert.True(await IndexExistsAsync(connection, "UX_RoutingRules_ProjectId_Name"));
        var nameIndexSql = await IndexSqlAsync(connection, "UX_RoutingRules_ProjectId_Name");
        Assert.NotNull(nameIndexSql);
        Assert.False(nameIndexSql!.Contains("WHERE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Down_WithIdempotencyFactsAbortsBeforeDroppingAnythingOnSqlite()
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        await using var context = database.CreateContext();
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await connection.OpenAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(CurrentMigration);
        Assert.Equal(CurrentMigration, await LatestMigrationIdAsync(connection));
        context.RoutingRules.Add(new RoutingRuleRow
        {
            Id = "rule_migration_fact",
            ProjectId = "project_migration_fact",
            Name = "migration-fact",
            Position = 1,
            Match = "event.type == \"migration\"",
            AgentId = "agent_migration_fact",
            ResponsePrompt = "Keep this fact.",
            Continue = false,
            Status = "active",
            CreatedAt = DateTimeOffset.Parse("2026-08-09T00:00:00+00:00"),
            UpdatedAt = DateTimeOffset.Parse("2026-08-09T00:00:00+00:00"),
            IdempotencyKey = "migration-fact-key",
        });
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<SqliteException>(() =>
            migrator.MigrateAsync(PreviousMigration));

        Assert.Equal(CurrentMigration, await LatestMigrationIdAsync(connection));
        Assert.True(await ColumnExistsAsync(connection, "RoutingRules", "IdempotencyKey"));
        Assert.True(await IndexExistsAsync(connection, "UX_RoutingRules_ProjectId_IdempotencyKey"));
        Assert.True(await IndexExistsAsync(connection, "UX_RoutingRules_ProjectId_Name"));
        Assert.Equal("migration-fact-key", await IdempotencyKeyAsync(connection));
        Assert.False(await TempTriggerExistsAsync(connection, RollbackGuardTrigger));
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string index)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name";
        command.Parameters.AddWithValue("$name", index);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<string?> IndexSqlAsync(SqliteConnection connection, string index)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name";
        command.Parameters.AddWithValue("$name", index);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<string?> IdempotencyKeyAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"IdempotencyKey\" FROM \"RoutingRules\" WHERE \"Id\" = 'rule_migration_fact'";
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<string?> LatestMigrationIdAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<bool> TempTriggerExistsAsync(SqliteConnection connection, string trigger)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_temp_master WHERE type = 'trigger' AND name = $name";
        command.Parameters.AddWithValue("$name", trigger);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static void Invoke(string methodName, MigrationBuilder builder)
    {
        var method = typeof(AddRoutingRuleIdempotencyKey).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(new AddRoutingRuleIdempotencyKey(), new object[] { builder });
    }
}
