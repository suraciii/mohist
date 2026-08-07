using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Storage;

public sealed class AgentSubagentTreeMigrationSpecs
{
    private const string BeforeMigration = "20260804110000_AddSlackManagerToolExecutionFence";
    private const string Migration = "20260805120000_AddAgentSubagentTreeContracts";

    [Fact]
    public async Task UpgradeDowngradeAndReupgrade_PreservesLegacyRows()
    {
        await using var database = TestSqliteDatabase.CreateEmpty();
        MigratedSqliteTemplate.CopyTo(database.Keeper, BeforeMigration);

        await using (var seed = database.CreateContext())
        {
            await seed.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AgentJobs" ("JobKey", "State")
                VALUES ('migration-job', json_object('status', 'pending'));
                INSERT INTO "AgentSessions" ("Id", "State", "RunnerId", "AgentSessionId", "Status", "CreatedAt")
                VALUES ('migration-session', json_object(), 'runner-migration', 'agent-session-migration', 'opened', '2026-01-01T00:00:00.0000000');
                """);
        }

        await using var db = database.CreateContext();
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync(Migration);
        Assert.Contains("LaunchVisibility", await ReadColumnNamesAsync(db, "AgentJobs"));
        Assert.Contains("ParentSessionId", await ReadColumnNamesAsync(db, "AgentSessions"));

        await migrator.MigrateAsync(BeforeMigration);
        Assert.DoesNotContain("LaunchVisibility", await ReadColumnNamesAsync(db, "AgentJobs"));
        Assert.DoesNotContain("ParentSessionId", await ReadColumnNamesAsync(db, "AgentSessions"));
        Assert.Equal("{\"status\":\"pending\"}", await ReadScalarAsync(db, "SELECT \"State\" FROM \"AgentJobs\" WHERE \"JobKey\" = 'migration-job'"));
        Assert.Equal("{}", await ReadScalarAsync(db, "SELECT \"State\" FROM \"AgentSessions\" WHERE \"Id\" = 'migration-session'"));

        await migrator.MigrateAsync(Migration);
        Assert.Contains("LaunchVisibility", await ReadColumnNamesAsync(db, "AgentJobs"));
        Assert.Contains("ParentSessionId", await ReadColumnNamesAsync(db, "AgentSessions"));
        Assert.Equal("{\"status\":\"pending\"}", await ReadScalarAsync(db, "SELECT \"State\" FROM \"AgentJobs\" WHERE \"JobKey\" = 'migration-job'"));
        Assert.Equal("{}", await ReadScalarAsync(db, "SELECT \"State\" FROM \"AgentSessions\" WHERE \"Id\" = 'migration-session'"));
    }

    [Fact]
    public async Task LatestMigration_MetadataSnapshotAndSqliteSchemaAgree()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        await using var db = database.CreateContext();

        var migrationType = typeof(AddAgentSubagentTreeContracts);
        Assert.Equal(
            "20260805120000_AddAgentSubagentTreeContracts",
            migrationType.GetCustomAttribute<MigrationAttribute>()?.Id);
        Assert.Equal(
            typeof(MohistDbContext),
            migrationType.GetCustomAttribute<DbContextAttribute>()?.ContextType);
        Assert.Null(migrationType.GetMethod(
            "BuildTargetModel",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));

        var model = db.Model;
        AssertModelProperties<AgentJobRow>(
            model,
            "PinnedRunnerId",
            "LaunchVisibility");
        AssertModelProperties<AgentSessionRow>(
            model,
            "ChildLaunchJobId",
            "LaunchVisibility",
            "ParentAgentId",
            "ParentLinkAttachedAt",
            "ParentLinkAttachedRevision",
            "ParentLinkDetachedAt",
            "ParentLinkDetachedRevision",
            "ParentLinkEdgeId",
            "ParentLinkState",
            "ParentSessionId");

        var agentJobColumns = await ReadTableColumnsAsync(db, "AgentJobs");
        var agentSessionColumns = await ReadTableColumnsAsync(db, "AgentSessions");
        Assert.Contains("PinnedRunnerId", agentJobColumns.Keys);
        Assert.Contains("LaunchVisibility", agentJobColumns.Keys);
        Assert.Equal(1, agentJobColumns["LaunchVisibility"].NotNull);
        Assert.Equal("'visible'", agentJobColumns["LaunchVisibility"].DefaultValue);
        Assert.Equal(
            new[]
            {
                "ChildLaunchJobId",
                "LaunchVisibility",
                "ParentAgentId",
                "ParentLinkAttachedAt",
                "ParentLinkAttachedRevision",
                "ParentLinkDetachedAt",
                "ParentLinkDetachedRevision",
                "ParentLinkEdgeId",
                "ParentLinkState",
                "ParentSessionId",
            },
            agentSessionColumns.Keys.Where(name => name is
                "ChildLaunchJobId"
                or "LaunchVisibility"
                or "ParentAgentId"
                or "ParentLinkAttachedAt"
                or "ParentLinkAttachedRevision"
                or "ParentLinkDetachedAt"
                or "ParentLinkDetachedRevision"
                or "ParentLinkEdgeId"
                or "ParentLinkState"
                or "ParentSessionId").ToArray());
        Assert.Equal(1, agentSessionColumns["LaunchVisibility"].NotNull);
        Assert.Equal("'visible'", agentSessionColumns["LaunchVisibility"].DefaultValue);

        var expectedIndexes = new Dictionary<string, (string Table, string[] Columns)>
        {
            ["IX_AgentJobs_PinnedRunner_Status_ReadySince"] =
                ("AgentJobs", ["PinnedRunnerId", "Status", "ReadySince"]),
            ["IX_AgentJobs_LaunchVisibility_Status_ReadySince"] =
                ("AgentJobs", ["LaunchVisibility", "Status", "ReadySince"]),
            ["IX_AgentSessions_TreeParent_AttachedRevision_Edge"] =
                ("AgentSessions", ["LabelProjectId", "ParentSessionId", "ParentLinkState", "ParentLinkAttachedRevision", "ParentLinkEdgeId"]),
            ["IX_AgentSessions_TreeVisibleParent_AttachedRevision_Edge"] =
                ("AgentSessions", ["LabelProjectId", "LaunchVisibility", "ParentSessionId", "ParentLinkAttachedRevision", "ParentLinkEdgeId"]),
        };

        foreach (var expected in expectedIndexes)
        {
            var entity = model.FindEntityType(expected.Value.Table == "AgentJobs"
                ? typeof(AgentJobRow)
                : typeof(AgentSessionRow));
            Assert.NotNull(entity);
            var index = entity!.GetIndexes().SingleOrDefault(item =>
                string.Equals(item.GetDatabaseName(), expected.Key, StringComparison.Ordinal));
            Assert.NotNull(index);
            Assert.Equal(expected.Value.Columns, index!.Properties.Select(property => property.Name).ToArray());

            var actualColumns = await ReadIndexColumnsAsync(
                db,
                expected.Value.Table,
                expected.Key);
            Assert.Equal(expected.Value.Columns, actualColumns);
        }
    }

    private static void AssertModelProperties<TEntity>(
        IModel model,
        params string[] names)
    {
        var entity = model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entity);
        foreach (var name in names)
            Assert.NotNull(entity!.FindProperty(name));
    }

    private static async Task<string[]> ReadIndexColumnsAsync(
        MohistDbContext db,
        string table,
        string index)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM pragma_index_info($index) ORDER BY seqno";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$index";
        parameter.Value = index;
        command.Parameters.Add(parameter);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        await db.Database.CloseConnectionAsync();
        return columns.ToArray();
    }

    private static async Task<Dictionary<string, (int NotNull, string? DefaultValue)>> ReadTableColumnsAsync(
        MohistDbContext db,
        string table)
    {
        await db.Database.OpenConnectionAsync();
        var columns = new Dictionary<string, (int NotNull, string? DefaultValue)>(StringComparer.Ordinal);
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info(\"{table}\")";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns[reader.GetString(1)] = (reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4));
        }
        await db.Database.CloseConnectionAsync();
        return columns;
    }

    private static async Task<string[]> ReadColumnNamesAsync(MohistDbContext db, string table)
    {
        var columns = await ReadTableColumnsAsync(db, table);
        return columns.Keys.ToArray();
    }

    private static async Task<string?> ReadScalarAsync(MohistDbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        await db.Database.CloseConnectionAsync();
        return value as string;
    }
}
