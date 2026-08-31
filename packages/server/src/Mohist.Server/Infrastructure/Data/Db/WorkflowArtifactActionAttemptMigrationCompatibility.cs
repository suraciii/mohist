using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Migrations;

namespace Mohist.Server.Infrastructure.Data.Db;

/// <summary>
/// Recognizes databases created by the short-lived baseline that already used
/// ActionAttemptId, so the corrective retained-tail migration is not replayed.
/// </summary>
internal static class WorkflowArtifactActionAttemptMigrationCompatibility
{
    private const string ArtifactsTable = "WorkflowArtifacts";
    private const string PendingUploadsTable = "WorkflowArtifactPendingUploads";
    private const string OldArtifactIndex = "IX_WorkflowArtifacts_WorkflowRunId_TaskRunId_RecordedAt";
    private const string NewArtifactIndex = "IX_WorkflowArtifacts_WorkflowRunId_ActionAttemptId_RecordedAt";
    private const string PendingUploadIndex = "UX_WorkflowArtifactPendingUploads_IdempotencyKey";

    public static async Task NormalizeAsync(
        MohistDbContext db,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            if (!await TableExistsAsync(connection, "__EFMigrationsHistory", cancellationToken)
                || !await MigrationAppliedAsync(connection, SquashedMigrationHistory.BaselineId, cancellationToken))
            {
                return;
            }

            var artifactsExist = await TableExistsAsync(connection, ArtifactsTable, cancellationToken);
            var pendingUploadsExist = await TableExistsAsync(connection, PendingUploadsTable, cancellationToken);
            if (!artifactsExist || !pendingUploadsExist)
            {
                throw new InvalidOperationException(
                    "Workflow artifact migration history records the squashed baseline, but required "
                    + $"table '{(artifactsExist ? PendingUploadsTable : ArtifactsTable)}' is missing.");
            }

            var migrationApplied = await MigrationAppliedAsync(
                connection,
                RenameWorkflowArtifactActionAttempts.MigrationId,
                cancellationToken);
            var artifacts = await ReadTableShapeAsync(connection, ArtifactsTable, cancellationToken);
            var pending = await ReadTableShapeAsync(connection, PendingUploadsTable, cancellationToken);
            var artifactIndex = await ReadIndexShapeAsync(
                connection,
                ArtifactsTable,
                artifacts.HasActionAttemptId ? NewArtifactIndex : OldArtifactIndex,
                cancellationToken);
            var pendingIndex = await ReadIndexShapeAsync(
                connection,
                PendingUploadsTable,
                PendingUploadIndex,
                cancellationToken);

            if (IsLegacyShape(artifacts, pending, artifactIndex, pendingIndex) && !migrationApplied)
            {
                return;
            }

            if (!IsAlreadyRenamedShape(artifacts, pending, artifactIndex, pendingIndex))
            {
                throw new InvalidOperationException(
                    "Workflow artifact schema is neither the supported TaskRunId shape nor the "
                    + "complete ActionAttemptId shape. Refusing to guess an upgrade for tables "
                    + $"'{ArtifactsTable}' and '{PendingUploadsTable}'.");
            }

            if (migrationApplied)
            {
                return;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                SELECT $migration, (
                    SELECT "ProductVersion" FROM "__EFMigrationsHistory"
                    ORDER BY "MigrationId" DESC LIMIT 1)
                WHERE NOT EXISTS (
                    SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = $migration);
                """;
            AddParameter(command, "$migration", RenameWorkflowArtifactActionAttempts.MigrationId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static bool IsLegacyShape(
        TableShape artifacts,
        TableShape pending,
        IndexShape artifactIndex,
        IndexShape pendingIndex)
        => artifacts is { HasTaskRunId: true, HasActionAttemptId: false }
           && pending is { HasTaskRunId: true, HasActionAttemptId: false }
           && artifactIndex is { Exists: true, Unique: false }
           && artifactIndex.Columns.SequenceEqual(["WorkflowRunId", "TaskRunId", "RecordedAt"])
           && pendingIndex is { Exists: true, Unique: true }
           && pendingIndex.Columns.SequenceEqual(["WorkflowRunId", "WorkId", "TaskRunId", "Path"]);

    private static bool IsAlreadyRenamedShape(
        TableShape artifacts,
        TableShape pending,
        IndexShape artifactIndex,
        IndexShape pendingIndex)
        => artifacts is { HasTaskRunId: false, HasActionAttemptId: true }
           && pending is { HasTaskRunId: false, HasActionAttemptId: true }
           && artifactIndex is { Exists: true, Unique: false }
           && artifactIndex.Columns.SequenceEqual(["WorkflowRunId", "ActionAttemptId", "RecordedAt"])
           && pendingIndex is { Exists: true, Unique: true }
           && pendingIndex.Columns.SequenceEqual(["WorkflowRunId", "WorkId", "ActionAttemptId", "Path"]);

    private static async Task<TableShape> ReadTableShapeAsync(
        DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        var hasTaskRunId = false;
        var hasActionAttemptId = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(1);
            hasTaskRunId |= string.Equals(name, "TaskRunId", StringComparison.Ordinal);
            hasActionAttemptId |= string.Equals(name, "ActionAttemptId", StringComparison.Ordinal);
        }

        return new TableShape(hasTaskRunId, hasActionAttemptId);
    }

    private static async Task<IndexShape> ReadIndexShapeAsync(
        DbConnection connection,
        string table,
        string index,
        CancellationToken cancellationToken)
    {
        var exists = false;
        var unique = false;
        await using (var listCommand = connection.CreateCommand())
        {
            listCommand.CommandText = $"PRAGMA index_list(\"{table}\");";
            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!string.Equals(reader.GetString(1), index, StringComparison.Ordinal))
                {
                    continue;
                }

                exists = true;
                unique = reader.GetInt64(2) != 0;
                break;
            }
        }

        var columns = new List<string>();
        if (exists)
        {
            await using var infoCommand = connection.CreateCommand();
            infoCommand.CommandText = $"PRAGMA index_info(\"{index}\");";
            await using var reader = await infoCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(2));
            }
        }

        return new IndexShape(exists, unique, columns);
    }

    private static async Task<bool> TableExistsAsync(
        DbConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM sqlite_master
            WHERE type = 'table' AND name = $table LIMIT 1;
            """;
        AddParameter(command, "$table", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> MigrationAppliedAsync(
        DbConnection connection,
        string migrationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = $migration LIMIT 1;
            """;
        AddParameter(command, "$migration", migrationId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record TableShape(bool HasTaskRunId, bool HasActionAttemptId);

    private sealed record IndexShape(bool Exists, bool Unique, IReadOnlyList<string> Columns);
}
