using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Storage;

public sealed class AgentInputAttachmentReservationMigrationSpecs
{
    private const string BeforeMigration = "20260807000000_AddSlackAgentAppInstallValidation";
    private const string Migration = "20260809000000_AddAgentInputAttachmentReservations";

    [Fact]
    public async Task UpCreatesReservationTableAndDownRemovesIt()
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(BeforeMigration);
        Assert.False(await TableExistsAsync(database.Keeper, "AgentInputAttachmentReservations"));

        await migrator.MigrateAsync(Migration);

        Assert.Equal(Migration, (await context.Database.GetAppliedMigrationsAsync()).Last());
        Assert.True(await TableExistsAsync(database.Keeper, "AgentInputAttachmentReservations"));
        Assert.Equal(
            new[] { "ReservationId", "AttachmentId", "ProjectId", "OwnerId", "Status", "CreatedAt", "ExpiresAt" },
            await ReadColumnNamesAsync(database.Keeper, "AgentInputAttachmentReservations"));
        Assert.Equal(
            32,
            context.Model.FindEntityType(typeof(AgentInputAttachmentReservationRow))
                ?.FindProperty(nameof(AgentInputAttachmentReservationRow.Status))
                ?.GetMaxLength());

        var indexes = await ReadIndexNamesAsync(database.Keeper, "AgentInputAttachmentReservations");
        Assert.Contains("IX_AgentInputAttachmentReservations_Attachment", indexes);
        Assert.Contains("IX_AgentInputAttachmentReservations_Reservation", indexes);
        Assert.Contains("IX_AgentInputAttachmentReservations_Expiry", indexes);

        await migrator.MigrateAsync(BeforeMigration);

        Assert.Equal(BeforeMigration, (await context.Database.GetAppliedMigrationsAsync()).Last());
        Assert.False(await TableExistsAsync(database.Keeper, "AgentInputAttachmentReservations"));
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<string[]> ReadColumnNamesAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table) ORDER BY cid;";
        command.Parameters.AddWithValue("$table", tableName);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        return columns.ToArray();
    }

    private static async Task<string[]> ReadIndexNamesAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_index_list($table) WHERE name NOT LIKE 'sqlite_%';";
        command.Parameters.AddWithValue("$table", tableName);

        var indexes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            indexes.Add(reader.GetString(0));
        return indexes.ToArray();
    }
}
