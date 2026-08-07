using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Issue-467 T-001: assert the VIRTUAL <c>Activity</c> projection and
/// the matching <c>IX_AgentSessions_StatusProject_SourceKind_Activity_CreatedAt</c>
/// composite index from
/// <c>20260726111353_AddAgentSessionStatusActivityProjection</c> land
/// on <c>AgentSessions</c> via the EF Core migrator against an
/// in-memory SQLite fixture. The projection powers the direct-session
/// branch of <c>AgentSessionQuery.ListStatusCandidatesAsync</c>; without
/// it the candidate predicate would either deserialize every historical
/// direct Session or rely on a <c>json_extract</c> predicate over the
/// full table — both of which are explicitly out of scope per the
/// design.
/// </summary>
public class AgentSessionStatusActivityProjectionMigrationSpecs
{
    private const string MigrationName = "20260726111353_AddAgentSessionStatusActivityProjection";

    [Fact]
    public async Task Up_AddsVirtualActivityComputedColumnDerivedFromStateJson()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationName);

        var columns = await ReadColumnNamesAsync(context, "AgentSessions");
        Assert.Contains("Activity", columns);
        Assert.Equal(2, await ReadColumnHiddenFlagAsync(context, "AgentSessions", "Activity"));
    }

    [Fact]
    public async Task Up_AddsCompositeStatusCandidateIndex()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationName);

        var indexes = await ReadIndexesAsync(context, "AgentSessions");
        Assert.Contains("IX_AgentSessions_StatusProject_SourceKind_Activity_CreatedAt", indexes.Keys);
        Assert.Equal(
            new[] { "LabelProjectId", "LabelSourceKind", "Activity", "CreatedAt" },
            indexes["IX_AgentSessions_StatusProject_SourceKind_Activity_CreatedAt"]);
    }

    [Fact]
    public async Task DatabaseMigrate_AppliesAgentSessionStatusActivityProjectionMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        await context.Database.MigrateAsync();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == MigrationName);
        var columns = await ReadColumnNamesAsync(context, "AgentSessions");
        Assert.Contains("Activity", columns);
    }

    [Fact]
    public async Task DatabaseMigrate_ActivityProjectionDerivesValueFromStateJson()
    {
        await using var database = CreateDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
        }

        await using var ctx = database.CreateDbContext();
        var stateJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "s_status_activity",
            metadata = new
            {
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["mohist.io/source-kind"] = "agent-launch",
                    ["mohist.io/project-id"] = "proj_status_activity",
                }
            },
            runtime = new { runnerId = "r-status", workDir = (string?)null },
            settings = new { },
            status = new { createdAt = TestTime.UtcDateTime, activity = "Active" },
        }, Mohist.Server.Infrastructure.JSON.Options);

        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO AgentSessions (Id, State, Status, CreatedAt) VALUES ({0}, {1}, {2}, {3})",
            "s_status_activity", stateJson, "opened", TestTime.UtcDateTime);

        await using var read = database.CreateDbContext();
        var row = await read.AgentSessions.AsNoTracking().SingleAsync(r => r.Id == "s_status_activity");
        // VIRTUAL projection LOWERs the activity value, matching the
        // existing Status column convention.
        Assert.Equal("active", row.Activity);
    }

    private static async Task<ISet<string>> ReadColumnNamesAsync(
        MohistDbContext context, string tableName)
    {
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

    private static async Task<int> ReadColumnHiddenFlagAsync(
        MohistDbContext context, string tableName, string columnName)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT hidden FROM pragma_table_xinfo('{tableName}') WHERE name = $column";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$column";
        parameter.Value = columnName;
        command.Parameters.Add(parameter);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
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
