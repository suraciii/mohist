using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.Infrastructure.Hosting;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.Artifacts;

public sealed class WorkflowArtifactActionAttemptMigrationTests
{
    [Fact]
    public async Task Migration_FromEmptyDatabase_CreatesFinalActionAttemptSchema()
    {
        await using var connection = await OpenAsync();
        await using (var db = CreateContext(connection))
        {
            await db.Database.MigrateAsync();
        }

        Assert.True(await ColumnExistsAsync(connection, "WorkflowArtifacts", "ActionAttemptId"));
        Assert.False(await ColumnExistsAsync(connection, "WorkflowArtifacts", "TaskRunId"));
        Assert.True(await ColumnExistsAsync(connection, "WorkflowArtifactPendingUploads", "ActionAttemptId"));
        Assert.False(await ColumnExistsAsync(connection, "WorkflowArtifactPendingUploads", "TaskRunId"));
        Assert.Equal(
            ["WorkflowRunId", "ActionAttemptId", "RecordedAt"],
            await ReadIndexColumnsAsync(connection, "IX_WorkflowArtifacts_WorkflowRunId_ActionAttemptId_RecordedAt"));
        Assert.Equal(
            ["WorkflowRunId", "WorkId", "ActionAttemptId", "Path"],
            await ReadIndexColumnsAsync(connection, "UX_WorkflowArtifactPendingUploads_IdempotencyKey"));
        Assert.True(await MigrationAppliedAsync(connection));
    }

    [Fact]
    public async Task Migration_UpgradesTaskRunColumnsAndIndexesWithoutChangingRows()
    {
        await using var connection = await OpenAsync();
        await MigrateToBaselineAsync(connection);
        await SeedLegacyRowsAsync(connection);

        await InitializeAsync(connection);

        Assert.True(await ColumnExistsAsync(connection, "WorkflowArtifacts", "ActionAttemptId"));
        Assert.False(await ColumnExistsAsync(connection, "WorkflowArtifacts", "TaskRunId"));
        Assert.True(await ColumnExistsAsync(connection, "WorkflowArtifactPendingUploads", "ActionAttemptId"));
        Assert.False(await ColumnExistsAsync(connection, "WorkflowArtifactPendingUploads", "TaskRunId"));
        Assert.Equal(
            "plan.1",
            await ScalarStringAsync(connection, "SELECT ActionAttemptId FROM WorkflowArtifacts WHERE ArtifactId = 'artifact-1';"));
        Assert.Equal(
            "plan.1",
            await ScalarStringAsync(connection, "SELECT ActionAttemptId FROM WorkflowArtifactPendingUploads WHERE UploadId = 'upload-1';"));
        Assert.Equal(
            ["WorkflowRunId", "ActionAttemptId", "RecordedAt"],
            await ReadIndexColumnsAsync(connection, "IX_WorkflowArtifacts_WorkflowRunId_ActionAttemptId_RecordedAt"));
        Assert.Equal(
            ["WorkflowRunId", "WorkId", "ActionAttemptId", "Path"],
            await ReadIndexColumnsAsync(connection, "UX_WorkflowArtifactPendingUploads_IdempotencyKey"));
        Assert.True(await MigrationAppliedAsync(connection));
    }

    [Fact]
    public async Task Initialization_WhenPreSquashHistoryIsRemapped_UpgradesArtifactRows()
    {
        await using var connection = await OpenAsync();
        await MigrateToBaselineAsync(connection);
        await RewriteHistoryAsSquashFloorAsync(connection);
        await SeedLegacyRowsAsync(connection);

        await InitializeAsync(connection);

        Assert.True(await HistoryContainsAsync(connection, SquashedMigrationHistory.BaselineId));
        Assert.False(await HistoryContainsAsync(connection, SquashedMigrationHistory.FloorId));
        Assert.True(await MigrationAppliedAsync(connection));
        Assert.Equal(
            "plan.1",
            await ScalarStringAsync(connection, "SELECT ActionAttemptId FROM WorkflowArtifacts WHERE ArtifactId = 'artifact-1';"));
        Assert.Equal(
            "plan.1",
            await ScalarStringAsync(connection, "SELECT ActionAttemptId FROM WorkflowArtifactPendingUploads WHERE UploadId = 'upload-1';"));
    }

    [Fact]
    public async Task Initialization_WhenShortLivedBaselineAlreadyRenamedSchema_RecordsMigrationAndKeepsData()
    {
        await using var connection = await OpenAsync();
        await MigrateToBaselineAsync(connection);
        await RenameAsShortLivedBaselineAsync(connection);
        await SeedRenamedRowsAsync(connection);

        await InitializeAsync(connection);

        Assert.Equal(
            "check.2",
            await ScalarStringAsync(connection, "SELECT ActionAttemptId FROM WorkflowArtifacts WHERE ArtifactId = 'artifact-2';"));
        Assert.Equal(
            "check.2",
            await ScalarStringAsync(connection, "SELECT ActionAttemptId FROM WorkflowArtifactPendingUploads WHERE UploadId = 'upload-2';"));
        Assert.True(await MigrationAppliedAsync(connection));
    }

    [Fact]
    public async Task Initialization_WhenArtifactSchemaIsMixed_FailsWithoutChangingHistory()
    {
        await using var connection = await OpenAsync();
        await MigrateToBaselineAsync(connection);
        await ExecuteAsync(connection, """
            DROP INDEX "IX_WorkflowArtifacts_WorkflowRunId_TaskRunId_RecordedAt";
            ALTER TABLE "WorkflowArtifacts" RENAME COLUMN "TaskRunId" TO "ActionAttemptId";
            CREATE INDEX "IX_WorkflowArtifacts_WorkflowRunId_ActionAttemptId_RecordedAt"
                ON "WorkflowArtifacts" ("WorkflowRunId", "ActionAttemptId", "RecordedAt");
            """);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => InitializeAsync(connection));

        Assert.Contains("neither the supported TaskRunId shape nor the complete ActionAttemptId shape", error.Message);
        Assert.False(await MigrationAppliedAsync(connection));
    }

    [Fact]
    public async Task Initialization_WhenRenamedIdempotencyIndexIsNotUnique_FailsWithoutMarkingMigration()
    {
        await using var connection = await OpenAsync();
        await MigrateToBaselineAsync(connection);
        await RenameAsShortLivedBaselineAsync(connection);
        await ExecuteAsync(connection, """
            DROP INDEX "UX_WorkflowArtifactPendingUploads_IdempotencyKey";
            CREATE INDEX "UX_WorkflowArtifactPendingUploads_IdempotencyKey"
                ON "WorkflowArtifactPendingUploads" ("WorkflowRunId", "WorkId", "ActionAttemptId", "Path");
            """);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => InitializeAsync(connection));

        Assert.Contains("neither the supported TaskRunId shape nor the complete ActionAttemptId shape", error.Message);
        Assert.False(await MigrationAppliedAsync(connection));
    }

    [Fact]
    public void Migration_IsDiscoverable()
    {
        var type = typeof(RenameWorkflowArtifactActionAttempts);
        var migration = Assert.Single(type.GetCustomAttributes<MigrationAttribute>());
        Assert.Equal(RenameWorkflowArtifactActionAttempts.MigrationId, migration.Id);
        Assert.Single(type.GetCustomAttributes<DbContextAttribute>());
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task MigrateToBaselineAsync(SqliteConnection connection)
    {
        await using var db = CreateContext(connection);
        await db.GetService<IMigrator>().MigrateAsync(SquashedMigrationHistory.BaselineId);
    }

    private static async Task InitializeAsync(SqliteConnection connection)
    {
        var services = new ServiceCollection()
            .AddDbContext<MohistDbContext>(options => options.UseSqlite(connection))
            .AddSingleton<TimeProvider>(new FakeTimeProvider())
            .BuildServiceProvider();
        await using (services)
        {
            await new MohistDatabaseInitializer().InitializeAsync(services, CancellationToken.None);
        }
    }

    private static MohistDbContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new MohistDbContext(options);
    }

    private static Task RewriteHistoryAsSquashFloorAsync(SqliteConnection connection)
        => ExecuteAsync(connection, $$"""
            DELETE FROM "__EFMigrationsHistory";
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('{{SquashedMigrationHistory.FloorId}}', '10.0.8');
            """);

    private static Task SeedLegacyRowsAsync(SqliteConnection connection)
        => ExecuteAsync(connection, """
            INSERT INTO "WorkflowArtifacts"
                ("ArtifactId", "WorkflowRunId", "TaskRunId", "Path", "RecordedAt", "ArtifactStoragePath", "Kind")
            VALUES
                ('artifact-1', 'run-1', 'plan.1', 'PLANS/PLAN.md', '2026-09-22T00:00:00+00:00', 'runs/run-1/PLAN.md', 'file');
            INSERT INTO "WorkflowArtifactPendingUploads"
                ("UploadId", "WorkflowRunId", "WorkId", "TaskRunId", "Path", "Kind", "StoragePath", "CreatedAt", "ExpiresAt")
            VALUES
                ('upload-1', 'run-1', 'plan', 'plan.1', 'PLANS/PLAN.md', 'file', 'pending/upload-1',
                 '2026-09-22T00:00:00+00:00', '2026-09-23T00:00:00+00:00');
            """);

    private static Task SeedRenamedRowsAsync(SqliteConnection connection)
        => ExecuteAsync(connection, """
            INSERT INTO "WorkflowArtifacts"
                ("ArtifactId", "WorkflowRunId", "ActionAttemptId", "Path", "RecordedAt", "ArtifactStoragePath", "Kind")
            VALUES
                ('artifact-2', 'run-2', 'check.2', 'PLANS/REVIEW.md', '2026-09-22T00:00:00+00:00', 'runs/run-2/REVIEW.md', 'file');
            INSERT INTO "WorkflowArtifactPendingUploads"
                ("UploadId", "WorkflowRunId", "WorkId", "ActionAttemptId", "Path", "Kind", "StoragePath", "CreatedAt", "ExpiresAt")
            VALUES
                ('upload-2', 'run-2', 'check', 'check.2', 'PLANS/REVIEW.md', 'file', 'pending/upload-2',
                 '2026-09-22T00:00:00+00:00', '2026-09-23T00:00:00+00:00');
            """);

    private static Task RenameAsShortLivedBaselineAsync(SqliteConnection connection)
        => ExecuteAsync(connection, """
            DROP INDEX "IX_WorkflowArtifacts_WorkflowRunId_TaskRunId_RecordedAt";
            DROP INDEX "UX_WorkflowArtifactPendingUploads_IdempotencyKey";
            ALTER TABLE "WorkflowArtifacts" RENAME COLUMN "TaskRunId" TO "ActionAttemptId";
            ALTER TABLE "WorkflowArtifactPendingUploads" RENAME COLUMN "TaskRunId" TO "ActionAttemptId";
            CREATE INDEX "IX_WorkflowArtifacts_WorkflowRunId_ActionAttemptId_RecordedAt"
                ON "WorkflowArtifacts" ("WorkflowRunId", "ActionAttemptId", "RecordedAt");
            CREATE UNIQUE INDEX "UX_WorkflowArtifactPendingUploads_IdempotencyKey"
                ON "WorkflowArtifactPendingUploads" ("WorkflowRunId", "WorkId", "ActionAttemptId", "Path");
            """);

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $column LIMIT 1;";
        command.Parameters.AddWithValue("$column", column);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<IReadOnlyList<string>> ReadIndexColumnsAsync(
        SqliteConnection connection,
        string index)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info(\"{index}\");";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(2));
        }

        return columns;
    }

    private static Task<bool> MigrationAppliedAsync(SqliteConnection connection)
        => HistoryContainsAsync(connection, RenameWorkflowArtifactActionAttempts.MigrationId);

    private static async Task<bool> HistoryContainsAsync(SqliteConnection connection, string migrationId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = $migration LIMIT 1;
            """;
        command.Parameters.AddWithValue("$migration", migrationId);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
