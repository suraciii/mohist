using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Domain;

public class BackfillIssueCompletedAtMigrationSpecs
{
    private const string MigrationId = "20260629120000_BackfillIssueCompletedAt";

    [Fact]
    public async Task Up_BackfillsDoneIssue_FromWorkCompletedEvent()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueAsync(setup, "issue_done_1", "done",
                """
                {"id":"issue_done_1","projectId":"proj_a","number":1,"title":"Done issue","status":"done","createdAt":"2026-06-28T10:00:00Z","updatedAt":"2026-06-28T12:00:00Z"}
                """);
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_done_1", 1,
                "com.mohist.issue.work-completed", "2026-06-28T12:00:00Z");
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var state = await ReadStateAsync(verify, "issue_done_1");
        Assert.Contains("\"completedAt\"", state);
        Assert.Contains("2026-06-28T12:00:00", state);
    }

    [Fact]
    public async Task Up_BackfillsCancelledIssue_FromClosedEvent()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueAsync(setup, "issue_cancelled_1", "cancelled",
                """
                {"id":"issue_cancelled_1","projectId":"proj_a","number":2,"title":"Cancelled issue","status":"cancelled","createdAt":"2026-06-27T10:00:00Z","updatedAt":"2026-06-27T14:00:00Z"}
                """);
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_cancelled_1", 1,
                "com.mohist.issue.closed", "2026-06-27T14:00:00Z");
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var state = await ReadStateAsync(verify, "issue_cancelled_1");
        Assert.Contains("\"completedAt\"", state);
        Assert.Contains("2026-06-27T14:00:00", state);
    }

    [Fact]
    public async Task Up_SecondRun_IsIdempotent()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueAsync(setup, "issue_done_2", "done",
                """
                {"id":"issue_done_2","projectId":"proj_a","number":3,"title":"Done issue 2","status":"done","createdAt":"2026-06-25T10:00:00Z","updatedAt":"2026-06-25T13:00:00Z"}
                """);
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_done_2", 1,
                "com.mohist.issue.work-completed", "2026-06-25T13:00:00Z");
        }

        await RunMigrationUpAsync(database);

        string firstState;
        await using (var firstRead = database.CreateDbContext())
        {
            firstState = await ReadStateAsync(firstRead, "issue_done_2");
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var secondState = await ReadStateAsync(verify, "issue_done_2");
        Assert.Equal(firstState, secondState);
    }

    [Fact]
    public async Task Up_DoesNotClobber_AlreadySetCompletedAt()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            var existingCompletedAt = "2026-06-26T09:00:00Z";
            await SeedIssueAsync(setup, "issue_done_3", "done",
                $$"""
                {"id":"issue_done_3","projectId":"proj_a","number":4,"title":"Already completed","status":"done","completedAt":"{{existingCompletedAt}}","createdAt":"2026-06-25T10:00:00Z","updatedAt":"2026-06-26T09:00:00Z"}
                """);
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_done_3", 1,
                "com.mohist.issue.work-completed", "2026-06-28T12:00:00Z");
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var state = await ReadStateAsync(verify, "issue_done_3");
        Assert.Contains("\"completedAt\":\"2026-06-26T09:00:00Z\"", state);
        Assert.DoesNotContain("2026-06-28T12:00:00", state);
    }

    [Fact]
    public async Task Up_RoundTripsBackfilledValue_ThroughIssueStore()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueAsync(setup, "issue_rt_1", "done",
                """
                {"id":"issue_rt_1","projectId":"proj_a","number":5,"title":"Round-trip issue","status":"done","createdAt":"2026-06-28T10:00:00Z","updatedAt":"2026-06-28T12:30:00Z"}
                """);
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_rt_1", 1,
                "com.mohist.issue.work-completed", "2026-06-28T12:30:00Z");
        }

        await RunMigrationUpAsync(database);

        string state;
        await using (var reader = database.CreateDbContext())
        {
            state = await ReadStateAsync(reader, "issue_rt_1");
        }

        var deserialized = IssueStore.Deserialize(state);
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.CompletedAt);
        Assert.Equal(new DateTime(2026, 6, 28, 12, 30, 0, DateTimeKind.Utc), deserialized.CompletedAt);
    }

    [Fact]
    public async Task Up_NonTerminalIssue_RemainsNull()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = database.CreateDbContext())
        {
            await SeedIssueAsync(setup, "issue_backlog_1", "backlog",
                """
                {"id":"issue_backlog_1","projectId":"proj_a","number":6,"title":"Backlog issue","status":"backlog","createdAt":"2026-06-28T10:00:00Z","updatedAt":"2026-06-28T10:00:00Z"}
                """);
        }

        await RunMigrationUpAsync(database);

        await using var verify = database.CreateDbContext();
        var state = await ReadStateAsync(verify, "issue_backlog_1");
        Assert.DoesNotContain("\"completedAt\"", state);
    }

    [Fact]
    public async Task DatabaseMigrate_IncludesBackfillIssueCompletedAtMigration()
    {
        await using var database = CreateDatabase();
        await using var ctx = database.CreateDbContext();
        await ctx.Database.MigrateAsync();

        var applied = await ctx.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == MigrationId);
    }

    private static async Task RunMigrationUpAsync(TestDatabase database)
    {
        await using var ctx = database.CreateDbContext();
        await ctx.Database.ExecuteSqlRawAsync(
            """
            UPDATE Issues
            SET State = json_set(State, '$.completedAt', (
                SELECT MAX(e.Time) FROM IssueEvents e
                WHERE e.Source = '/mohist/issues/' || json_extract(Issues.State, '$.id')
                  AND e.Type = 'com.mohist.issue.work-completed'
            ))
            WHERE json_extract(State, '$.completedAt') IS NULL
              AND COALESCE(json_extract(State,'$.status'), json_extract(State,'$.Status')) = 'done';
            """);
        await ctx.Database.ExecuteSqlRawAsync(
            """
            UPDATE Issues
            SET State = json_set(State, '$.completedAt', (
                SELECT MAX(e.Time) FROM IssueEvents e
                WHERE e.Source = '/mohist/issues/' || json_extract(Issues.State, '$.id')
                  AND e.Type = 'com.mohist.issue.closed'
            ))
            WHERE json_extract(State, '$.completedAt') IS NULL
              AND COALESCE(json_extract(State,'$.status'), json_extract(State,'$.Status')) = 'cancelled';
            """);
    }

    private static async Task SeedIssueAsync(
        MohistDbContext ctx,
        string issueId,
        string status,
        string stateJson)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO Issues (ProjectId, Number, State)
            VALUES (
                COALESCE(json_extract($state, '$.projectId'), json_extract($state, '$.ProjectId'), 'migration-test'),
                CAST(COALESCE(json_extract($state, '$.number'), json_extract($state, '$.Number')) AS INTEGER),
                $state);
            """;
        var stateParam = command.CreateParameter();
        stateParam.ParameterName = "$state";
        stateParam.Value = stateJson;
        command.Parameters.Add(stateParam);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedIssueEventAsync(
        MohistDbContext ctx,
        string source,
        long id,
        string type,
        string time)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO IssueEvents (Source, Id, EventId, Type, Time, SpecVersion, DataContentType, Data, ExtensionsJson)
            VALUES ($source, $id, $eventId, $type, $time, '1.0', 'application/json', '{}', '{}');
            """;
        var sourceParam = command.CreateParameter();
        sourceParam.ParameterName = "$source";
        sourceParam.Value = source;
        command.Parameters.Add(sourceParam);
        var idParam = command.CreateParameter();
        idParam.ParameterName = "$id";
        idParam.Value = id;
        command.Parameters.Add(idParam);
        var eventIdParam = command.CreateParameter();
        eventIdParam.ParameterName = "$eventId";
        eventIdParam.Value = $"evt_{id}";
        command.Parameters.Add(eventIdParam);
        var typeParam = command.CreateParameter();
        typeParam.ParameterName = "$type";
        typeParam.Value = type;
        command.Parameters.Add(typeParam);
        var timeParam = command.CreateParameter();
        timeParam.ParameterName = "$time";
        timeParam.Value = time;
        command.Parameters.Add(timeParam);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadStateAsync(MohistDbContext ctx, string issueId)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT State FROM Issues WHERE json_extract(State, '$.id') = $id";
        var param = command.CreateParameter();
        param.ParameterName = "$id";
        param.Value = issueId;
        command.Parameters.Add(param);
        var result = await command.ExecuteScalarAsync();
        return (result as string)!;
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        return new TestDatabase(connection, factory);
    }

    private static TestDatabase CreateModelSchemaDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyModelSchemaTo(connection);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        return new TestDatabase(connection, factory);
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
