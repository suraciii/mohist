using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Storage;

public sealed class WorkflowRunProfileDefaultsMigrationSpecs
{
    private const string BeforeMigration = "20260728000000_TypedWorkflowRunLineage";
    private const string Migration = "20260729000000_DropWorkflowRunProfileDefaults";

    [Fact]
    public async Task UpDownAndReupgrade_PreserveExplicitProfileDataAndChangeOnlyTheDefaultsColumn()
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        MigratedSqliteTemplate.CopyTo(database.Keeper, BeforeMigration);
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "WorkflowRunProfiles" ("WorkflowRunId", "Variables", "DefaultVariables", "UpdatedAt", "ETag")
            VALUES ('wr_defaults_migration', '{{"vars":{{"archive":"/explicit"}}}}', '{{"defaultVars":{{"archive":""}}}}', '2026-01-01T00:00:00+00:00', 7);
            """);
        var originalColumns = await ReadColumnsAsync(db);
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync(Migration);

        Assert.Contains(Migration, await db.Database.GetAppliedMigrationsAsync());
        var migratedColumns = await ReadColumnsAsync(db);
        Assert.Equal(
            originalColumns.Where(column => column != "DefaultVariables").Order(),
            migratedColumns.Order());
        Assert.Equal("wr_defaults_migration", await ReadStringAsync(db, "WorkflowRunId"));
        Assert.Equal("{\"vars\":{\"archive\":\"/explicit\"}}", await ReadStringAsync(db, "Variables"));
        Assert.Equal("2026-01-01T00:00:00+00:00", await ReadStringAsync(db, "UpdatedAt"));
        Assert.Equal(7L, await ReadInt64Async(db, "ETag"));

        await migrator.MigrateAsync(BeforeMigration);

        Assert.Equal(originalColumns.Order(), (await ReadColumnsAsync(db)).Order());
        Assert.Equal("wr_defaults_migration", await ReadStringAsync(db, "WorkflowRunId"));
        Assert.Equal("{\"vars\":{\"archive\":\"/explicit\"}}", await ReadStringAsync(db, "Variables"));
        Assert.Equal("{}", await ReadStringAsync(db, "DefaultVariables"));
        Assert.Equal("2026-01-01T00:00:00+00:00", await ReadStringAsync(db, "UpdatedAt"));
        Assert.Equal(7L, await ReadInt64Async(db, "ETag"));
        await migrator.MigrateAsync(Migration);
        Assert.Contains(Migration, await db.Database.GetAppliedMigrationsAsync());
        Assert.DoesNotContain("DefaultVariables", await ReadColumnsAsync(db));
        Assert.Equal("wr_defaults_migration", await ReadStringAsync(db, "WorkflowRunId"));
        Assert.Equal("{\"vars\":{\"archive\":\"/explicit\"}}", await ReadStringAsync(db, "Variables"));
        Assert.Equal(7L, await ReadInt64Async(db, "ETag"));
    }

    private static async Task<IReadOnlyList<string>> ReadColumnsAsync(DbContext db)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('WorkflowRunProfiles') ORDER BY cid";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<string> ReadStringAsync(DbContext db, string column) =>
        Assert.IsType<string>(await ReadValueAsync(db, column));

    private static async Task<long> ReadInt64Async(DbContext db, string column) =>
        Assert.IsType<long>(await ReadValueAsync(db, column));

    private static async Task<object?> ReadValueAsync(DbContext db, string column)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT \"{column}\" FROM \"WorkflowRunProfiles\" WHERE \"WorkflowRunId\" = 'wr_defaults_migration'";
        return await command.ExecuteScalarAsync();
    }
}
