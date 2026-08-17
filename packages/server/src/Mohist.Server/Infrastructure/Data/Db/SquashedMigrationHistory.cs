using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Mohist.Server.Infrastructure.Data.Db;

/// <summary>
/// Rewrites <c>__EFMigrationsHistory</c> for databases created before the
/// migration squash so the squashed baseline does not re-execute on them.
/// Databases whose newest applied migration predates <see cref="FloorId"/>
/// sit outside the supported upgrade window and fail fast — they must first
/// be upgraded with a build that still carries the pre-squash chain.
/// </summary>
public static class SquashedMigrationHistory
{
    public const string BaselineId = "20260906000000_SquashedBaseline";

    // The newest migration folded into the baseline. A database at or past
    // this id has every schema change and data backfill the baseline
    // replaces, so its history can be rewritten losslessly.
    public const string FloorId = "20260906000000_AddWorkflowProfileAgentActionOverrides";

    // Migration ids are zero-padded timestamps, so a prefix boundary is a
    // lexicographic boundary: everything below this was squashed.
    private const string FirstRetainedId = "20260907000000_AddCredentialProjectGrants";

    public static async Task RemapAsync(MohistDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            if (!await HistoryTableExistsAsync(connection, cancellationToken))
            {
                return;
            }

            var applied = await ReadAppliedMigrationsAsync(connection, cancellationToken);
            if (applied.Count == 0 || applied.Contains(BaselineId))
            {
                return;
            }

            var newest = applied.Max(StringComparer.Ordinal)!;
            if (string.CompareOrdinal(newest, FloorId) < 0)
            {
                throw new InvalidOperationException(
                    $"This database's newest applied migration is '{newest}', which predates the migration "
                    + $"squash floor '{FloorId}'. It cannot be upgraded by this build: run a build that still "
                    + "carries the pre-squash migration chain once (any revision whose newest migration is "
                    + $"'{FloorId}' or newer), then upgrade again.");
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                SELECT $baseline, (
                    SELECT "ProductVersion" FROM "__EFMigrationsHistory"
                    ORDER BY "MigrationId" DESC LIMIT 1)
                WHERE NOT EXISTS (
                    SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = $baseline);
                """,
                cancellationToken,
                ("$baseline", BaselineId));
            await ExecuteAsync(
                connection,
                transaction,
                """
                DELETE FROM "__EFMigrationsHistory"
                WHERE "MigrationId" < $firstRetained AND "MigrationId" <> $baseline;
                """,
                cancellationToken,
                ("$firstRetained", FirstRetainedId),
                ("$baseline", BaselineId));
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

    private static async Task<bool> HistoryTableExistsAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM sqlite_master
            WHERE type = 'table' AND name = '__EFMigrationsHistory' LIMIT 1;
            """;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<HashSet<string>> ReadAppliedMigrationsAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "MigrationId" FROM "__EFMigrationsHistory";""";
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
