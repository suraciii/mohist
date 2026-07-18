using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Issue-130 T-001 migration specs: assert the STORED computed columns and
/// indexes added in <c>AddAgentLaunchLabelComputedColumns</c> are created
/// on <c>AgentSessions</c> via the EF Core migrator against an in-memory
/// SQLite fixture (matches the established <c>InboxItemsMigrationSpecs</c>
/// pattern). Verifies the SQLite table rebuild path EF takes when adding
/// STORED computed columns works through the EF migrator path.
/// </summary>
public class AgentLaunchLabelComputedColumnsMigrationSpecs
{
    private const string MigrationName = "20260629112745_AddAgentLaunchLabelComputedColumns";

    [Fact]
    public async Task Up_CreatesSixStoredComputedColumnsDerivedFromStateJson()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationName);

        var columns = await ReadColumnNamesAsync(context, "AgentSessions");

        // Each new label column must be present on AgentSessions. The STORED
        // generated columns appear in pragma_table_xinfo alongside the
        // regular columns (hidden > 0); EF surfaces them as TEXT-shaped
        // values, but in SQLite the declared type for a generated column
        // is empty so we don't assert on type.
        foreach (var name in new[]
        {
            "LabelAgentId",
            "LabelAgentName",
            "LabelAgentLaunchIssueNumber",
            "LabelAgentLaunchEpicNumber",
            "LabelAgentLaunchRepository",
            "LabelAgentLaunchWorkspacePath",
        })
        {
            Assert.Contains(name, columns);
        }
    }

    [Fact]
    public async Task Up_IndexesMatchExpectedShape()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationName);

        var indexes = await ReadIndexesAsync(context, "AgentSessions");

        // Composite index for the agent-scoped recency list.
        Assert.Contains("IX_AgentSessions_LabelAgentId_LabelProjectId_CreatedAt", indexes.Keys);
        Assert.Equal(
            new[] { "LabelAgentId", "LabelProjectId", "CreatedAt" },
            indexes["IX_AgentSessions_LabelAgentId_LabelProjectId_CreatedAt"]);

        // Single-column indexes for the issue/epic association reads.
        Assert.Contains("IX_AgentSessions_LabelAgentLaunchIssueNumber", indexes.Keys);
        Assert.Equal(
            new[] { "LabelAgentLaunchIssueNumber" },
            indexes["IX_AgentSessions_LabelAgentLaunchIssueNumber"]);

        Assert.Contains("IX_AgentSessions_LabelAgentLaunchEpicNumber", indexes.Keys);
        Assert.Equal(
            new[] { "LabelAgentLaunchEpicNumber" },
            indexes["IX_AgentSessions_LabelAgentLaunchEpicNumber"]);
    }

    [Fact]
    public async Task DatabaseMigrate_AppliesAgentLaunchComputedColumnsMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        await context.Database.MigrateAsync();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == MigrationName);
        // After Migrate, all six new columns must exist on AgentSessions —
        // confirms the add-then-AlterColumn table-rebuild path also applies
        // when integrated with the full migration chain.
        var columns = await ReadColumnNamesAsync(context, "AgentSessions");
        foreach (var name in new[]
        {
            "LabelAgentId",
            "LabelAgentName",
            "LabelAgentLaunchIssueNumber",
            "LabelAgentLaunchEpicNumber",
            "LabelAgentLaunchRepository",
            "LabelAgentLaunchWorkspacePath",
        })
        {
            Assert.Contains(name, columns);
        }
    }

    [Fact]
    public async Task DatabaseMigrate_PopulatesComputedColumnsWithoutBackfill()
    {
        // After the full migration chain applies and SQLite rebuilds the
        // table, every STORED computed column derives values from the State
        // JSON — no explicit backfill in any Up. Insert a session with labels
        // only in State JSON and read back the columns through EF.
        await using var database = CreateDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
        }

        await using var ctx = database.CreateDbContext();
        var stateJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "s_no_backfill",
            metadata = new
            {
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["mohist.io/agent-id"] = "agent_bf",
                    ["mohist.io/agent-name"] = "agent-bf",
                    ["mohist.io/source-kind"] = "agent-launch",
                    ["mohist.io/agent-launch/issue-number"] = "11",
                    ["mohist.io/agent-launch/epic-number"] = "22",
                    ["mohist.io/agent-launch/repository"] = "mohist/x",
                    ["mohist.io/agent-launch/workspace-path"] = "/work/bf",
                    ["mohist.io/project-id"] = "proj_bf",
                }
            },
            runtime = new { runnerId = "r-bf", workDir = (string?)null },
            settings = new { },
            status = new { createdAt = TestTime.UtcDateTime },
        }, Mohist.Server.Infrastructure.JSON.Options);

        // Insert via raw SQL so the row can be added against the schema that
        // exists at AddAgentLaunchLabelComputedColumns without requiring the
        // newer trigger-label columns added in issue-391 T-003.
        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO AgentSessions (Id, State, Status, CreatedAt) VALUES ({0}, {1}, {2}, {3})",
            "s_no_backfill", stateJson, "opened", TestTime.UtcDateTime);

        await using var read = database.CreateDbContext();
        var row = await read.AgentSessions.AsNoTracking().SingleAsync(r => r.Id == "s_no_backfill");
        // Without any backfill, the computed columns must evaluate from
        // State JSON purely through the json_extract expressions.
        Assert.Equal("agent_bf", row.LabelAgentId);
        Assert.Equal("agent-bf", row.LabelAgentName);
        Assert.Equal("11", row.LabelAgentLaunchIssueNumber);
        Assert.Equal("22", row.LabelAgentLaunchEpicNumber);
        Assert.Equal("mohist/x", row.LabelAgentLaunchRepository);
        Assert.Equal("/work/bf", row.LabelAgentLaunchWorkspacePath);
        // issue-391 T-003: trigger correlation columns exist but are null when
        // the State JSON carries no trigger labels.
        Assert.Null(row.LabelTriggerEventId);
        Assert.Null(row.LabelTriggerRuleId);
    }

    private static async Task<ISet<string>> ReadColumnNamesAsync(
        MohistDbContext context, string tableName)
    {
        // pragma_table_info hides STORED generated columns (they appear as
        // hidden=2/3); pragma_table_xinfo includes every column so the
        // STORED computed label columns are visible alongside the regular
        // ones.
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_xinfo('{tableName}')";

        await using var reader = await command.ExecuteReaderAsync();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static async Task<IDictionary<string, string[]>> ReadIndexesAsync(
        MohistDbContext context, string tableName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"name\", \"seq\" FROM pragma_index_list('{tableName}') " +
            "WHERE \"origin\" != 'pk' AND \"name\" NOT LIKE 'sqlite_%' " +
            "ORDER BY \"seq\"";

        var ordered = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                ordered.Add(reader.GetString(0));
            }
        }

        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var indexName in ordered)
        {
            await using var inner = connection.CreateCommand();
            inner.CommandText = $"SELECT \"name\" FROM pragma_index_info('{indexName}') ORDER BY \"seqno\"";
            var columns = new List<string>();
            await using var colReader = await inner.ExecuteReaderAsync();
            while (await colReader.ReadAsync())
            {
                columns.Add(colReader.GetString(0));
            }
            result[indexName] = columns.ToArray();
        }
        return result;
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        return new TestDatabase(connection, new TestDbContextFactory(options));
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDatabase(SqliteConnection connection, TestDbContextFactory factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public TestDbContextFactory Factory { get; }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        {
            Options = options;
        }

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }
}
