using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Domain;

/// <summary>
/// issue-417 T-002 / Design D2/D3 migration specs: assert the
/// <c>AddIssueRepositoryProjection</c> migration adds the expected
/// <c>IssueRow.RepositoryName</c> STORED computed column, creates the
/// <c>(ProjectId, RepositoryName, Status)</c> composite index, and adds
/// the two <c>ProjectRow</c> coordination columns (<c>RepositoryRevision</c>,
/// <c>LastRepositoryCommandJson</c>) without a backfill. Mirrors the
/// <see cref="Sessions.AgentLaunchLabelComputedColumnsMigrationSpecs"/> and
/// <see cref="Inbox.InboxItemsMigrationSpecs"/> patterns: each test runs
/// against an in-memory SQLite fixture using the EF migrator so the same
/// provider code path runs in CI as in production.
/// </summary>
public class AddIssueRepositoryProjectionMigrationSpecs
{
    private const string PreviousMigrationId = "20260714120000_AddProjectEventReadKeys";
    private const string MigrationId = "20260717000000_AddIssueRepositoryProjection";

    [Fact]
    public async Task Up_AddsRepositoryNameColumnToIssues()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationId);

        var columns = await ReadColumnNamesAsync(context, "Issues");
        // STORED generated columns are visible through pragma_table_xinfo
        // alongside the regular ones (hidden > 0).
        Assert.Contains("RepositoryName", columns);
    }

    [Fact]
    public async Task Up_RepositoryNameIsStoredGeneratedFromStateJson()
    {
        // After the full migration chain applies and SQLite rebuilds the
        // Issues table, RepositoryName must derive from State JSON through
        // the json_extract expression with no backfill in Up.
        await using var database = CreateDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
        }

        // State JSON carrying the camelCase repositoryRef property — the
        // path the canonical Issue serialized state uses. Insert via raw
        // command so the row matches the pre-coordinator schema (no
        // HasWorkflowStarted, no receipt fields) without depending on
        // T-005's domain changes.
        var stateJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            projectId = "proj_t002_a",
            number = 1001,
            title = "issue with repo",
            status = "backlog",
            priority = "p2",
            repositoryRef = "web",
        });
        await InsertIssueAsync(database, "proj_t002_a", 1001, stateJson);

        await using var read = database.CreateDbContext();
        var row = await read.Set<IssueRow>().AsNoTracking().SingleAsync(r => r.ProjectId == "proj_t002_a" && r.Number == 1001);
        Assert.Equal("web", row.RepositoryName);
    }

    [Fact]
    public async Task Up_RepositoryName_NullWhenStateJsonHasNoRepositoryRef()
    {
        // Pre-417 state (no repositoryRef) must read back null on the
        // generated column — the projection is additive and never throws
        // for legacy rows.
        await using var database = CreateDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
        }

        var stateJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            projectId = "proj_t002_legacy",
            number = 1,
            title = "legacy issue",
            status = "done",
            priority = "p3",
        });
        await InsertIssueAsync(database, "proj_t002_legacy", 1, stateJson);

        await using var read = database.CreateDbContext();
        var row = await read.Set<IssueRow>().AsNoTracking().SingleAsync(r => r.ProjectId == "proj_t002_legacy" && r.Number == 1);
        Assert.Null(row.RepositoryName);
    }

    [Fact]
    public async Task Up_CreatesIssuesRepositoryNameStatusIndex()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationId);

        var indexes = await ReadIndexesAsync(context, "Issues");
        Assert.Contains("IX_Issues_ProjectId_RepositoryName_Status", indexes.Keys);
        Assert.Equal(
            new[] { "ProjectId", "RepositoryName", "Status" },
            indexes["IX_Issues_ProjectId_RepositoryName_Status"]);
    }

    [Fact]
    public async Task Up_AddsRepositoryRevisionColumnToProjects()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationId);

        var columns = await ReadColumnNamesAsync(context, "Projects");
        Assert.Contains("RepositoryRevision", columns);
        Assert.Contains("LastRepositoryCommandJson", columns);
    }

    [Fact]
    public async Task Up_ProjectsColumnsAreWiredAndQueryableThroughEf()
    {
        // Round-trip a ProjectRow through EF to confirm the new columns
        // are mapped correctly and accept long + nullable string values.
        await using var database = CreateDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await setup.Database.MigrateAsync();
        }

        await using (var ctx = database.CreateDbContext())
        {
            ctx.Projects.Add(new ProjectRow
            {
                Id = "proj_t002_roundtrip",
                Name = "proj-t002",
                RepositoriesJson = "[]",
                RepositoryRevision = 7L,
                LastRepositoryCommandJson = """{"commandId":"abc","kind":"remove"}""",
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = database.CreateDbContext();
        var row = await read.Set<ProjectRow>().AsNoTracking().SingleAsync(r => r.Id == "proj_t002_roundtrip");
        Assert.Equal(7L, row.RepositoryRevision);
        Assert.Equal("""{"commandId":"abc","kind":"remove"}""", row.LastRepositoryCommandJson);
    }

    [Fact]
    public async Task DatabaseMigrate_AppliesAddIssueRepositoryProjectionMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();

        await context.Database.MigrateAsync();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(MigrationId, applied);
    }

    [Fact]
    public async Task Migration_AppliesOnTopOfPreviousMigration()
    {
        await using var database = CreateDatabase();
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigrationId);

        await migrator.MigrateAsync(MigrationId);

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(MigrationId, applied);
        var columns = await ReadColumnNamesAsync(context, "Issues");
        Assert.Contains("RepositoryName", columns);
    }

    [Fact]
    public async Task MigratedSqliteTemplate_AlreadyContainsRepositoryProjection()
    {
        // MigratedSqliteTemplate clones the full-migration schema once per
        // process. This spec proves the new migration is part of that
        // snapshot — failing here means a future spec that clones the
        // template would not see the projection.
        await using var database = CreateDatabase();
        MigratedSqliteTemplate.CopyTo(database.Connection);

        var columns = await ReadColumnNamesAsync(database.CreateDbContext(), "Issues");
        Assert.Contains("RepositoryName", columns);
        var projectColumns = await ReadColumnNamesAsync(database.CreateDbContext(), "Projects");
        Assert.Contains("RepositoryRevision", projectColumns);
        Assert.Contains("LastRepositoryCommandJson", projectColumns);
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
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new TestDatabase(connection, options);
    }

    private static async Task InsertIssueAsync(TestDatabase database, string projectId, int number, string stateJson)
    {
        await using var ctx = database.CreateDbContext();
        var connection = ctx.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Issues (ProjectId, Number, State)
            VALUES ($projectId, $number, $state);
            """;
        var projectIdParam = command.CreateParameter();
        projectIdParam.ParameterName = "$projectId";
        projectIdParam.Value = projectId;
        command.Parameters.Add(projectIdParam);
        var numberParam = command.CreateParameter();
        numberParam.ParameterName = "$number";
        numberParam.Value = number;
        command.Parameters.Add(numberParam);
        var stateParam = command.CreateParameter();
        stateParam.ParameterName = "$state";
        stateParam.Value = stateJson;
        command.Parameters.Add(stateParam);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public TestDatabase(SqliteConnection connection, DbContextOptions<MohistDbContext> options)
        {
            Connection = connection;
            _options = options;
        }

        public SqliteConnection Connection { get; }

        public MohistDbContext CreateDbContext() => new(_options);

        public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
    }
}
