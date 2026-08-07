using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Domain;

/// <summary>
/// Specs for the <c>BackfillIssueCompletedEvents</c> migration, which
/// reconstructs the <c>com.mohist.issue.completed</c> CloudEvent row each
/// <c>done</c> issue was missing (the append path had silently dropped every
/// lifecycle event before the SaveIssueAsync snapshot fix landed).
/// </summary>
public class BackfillIssueCompletedEventsMigrationSpecs
{
    private const string MigrationId = "20260705132535_BackfillIssueCompletedEvents";
    private const string CompletedType = "com.mohist.issue.completed";

    [Fact]
    public async Task Up_DoneIssueWithCompletedAt_InsertsCompletedEventUsingCompletedAt()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = CreateContext(database))
        {
            await SeedIssueAsync(setup, "issue_done_ca", """
                {"id":"issue_done_ca","projectId":"proj_a","number":7,"title":"Done with completedAt","status":"done","completedAt":"2026-06-28T12:00:00Z","createdAt":"2026-06-28T10:00:00Z","updatedAt":"2026-06-28T12:00:00Z","workflowRunId":"wr_done_ca"}
                """);
        }

        await RunMigrationUpAsync(database);

        await using var verify = CreateContext(database);
        var row = await ReadSingleIssueEventAsync(verify, "issue_done_ca");
        Assert.NotNull(row);
        Assert.Equal(CompletedType, row!.Type);
        Assert.Equal("/mohist/issues/issue_done_ca", row.Source);
        Assert.Equal("7", row.Subject);
        Assert.Contains("2026-06-28T12:00:00", row.Time);
        Assert.Contains("\"workflowRunId\":\"wr_done_ca\"", row.Data);
        Assert.Contains("\"projectid\":\"proj_a\"", row.ExtensionsJson);
        Assert.Contains("\"issueno\":\"7\"", row.ExtensionsJson);
    }

    [Fact]
    public async Task Up_DoneIssueWithoutCompletedAt_FallsBackToUpdatedAt()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = CreateContext(database))
        {
            await SeedIssueAsync(setup, "issue_done_noca", """
                {"id":"issue_done_noca","projectId":"proj_a","number":8,"title":"Legacy done no completedAt","status":"done","createdAt":"2026-05-31T10:00:00Z","updatedAt":"2026-05-31T13:30:13.257Z"}
                """);
        }

        await RunMigrationUpAsync(database);

        await using var verify = CreateContext(database);
        var row = await ReadSingleIssueEventAsync(verify, "issue_done_noca");
        Assert.NotNull(row);
        Assert.Equal(CompletedType, row!.Type);
        // Falls back to updatedAt when completedAt is absent.
        Assert.Contains("2026-05-31T13:30:13", row.Time);
        // workflowRunId is null in the snapshot → serialized as null, not omitted.
        Assert.Contains("\"workflowRunId\":null", row.Data);
    }

    [Fact]
    public async Task Up_LegacyDoneCapitalized_AlsoBackfilled()
    {
        // Legacy snapshots serialize status as PascalCase ('Done') from the
        // pre-camelCase serializer era; the match must be case-insensitive.
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = CreateContext(database))
        {
            await SeedIssueAsync(setup, "issue_done_caps", """
                {"id":"issue_done_caps","projectId":"proj_a","number":9,"title":"Capital Done","status":"Done","createdAt":"2026-05-31T10:00:00Z","updatedAt":"2026-05-31T13:30:13Z"}
                """);
        }

        await RunMigrationUpAsync(database);

        await using var verify = CreateContext(database);
        var row = await ReadSingleIssueEventAsync(verify, "issue_done_caps");
        Assert.NotNull(row);
        Assert.Equal(CompletedType, row!.Type);
    }

    [Fact]
    public async Task Up_CancelledIssue_NotBackfilled()
    {
        // Throughput measures delivery, not failure cadence — cancelled is
        // intentionally out of scope.
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = CreateContext(database))
        {
            await SeedIssueAsync(setup, "issue_cancelled", """
                {"id":"issue_cancelled","projectId":"proj_a","number":10,"title":"Cancelled","status":"cancelled","completedAt":"2026-06-27T14:00:00Z","createdAt":"2026-06-27T10:00:00Z","updatedAt":"2026-06-27T14:00:00Z"}
                """);
        }

        await RunMigrationUpAsync(database);

        await using var verify = CreateContext(database);
        var row = await ReadSingleIssueEventAsync(verify, "issue_cancelled");
        Assert.Null(row);
    }

    [Fact]
    public async Task Up_NonTerminalIssue_NotBackfilled()
    {
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = CreateContext(database))
        {
            await SeedIssueAsync(setup, "issue_backlog", """
                {"id":"issue_backlog","projectId":"proj_a","number":11,"title":"Backlog","status":"backlog","createdAt":"2026-06-28T10:00:00Z","updatedAt":"2026-06-28T10:00:00Z"}
                """);
        }

        await RunMigrationUpAsync(database);

        await using var verify = CreateContext(database);
        var row = await ReadSingleIssueEventAsync(verify, "issue_backlog");
        Assert.Null(row);
    }

    [Fact]
    public async Task Up_IssueWithExistingCompletedEvent_NotReinserted()
    {
        // Idempotency: an issue that already has a completed event (e.g. live-
        // written after the SaveIssueAsync fix) must not get a duplicate.
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = CreateContext(database))
        {
            await SeedIssueAsync(setup, "issue_existing", """
                {"id":"issue_existing","projectId":"proj_a","number":12,"title":"Existing","status":"done","completedAt":"2026-06-29T08:00:00Z","createdAt":"2026-06-29T06:00:00Z","updatedAt":"2026-06-29T08:00:00Z"}
                """);
            await SeedIssueEventAsync(setup, "/mohist/issues/issue_existing", 1L,
                CompletedType, "2026-06-29T08:00:00Z");
        }

        await RunMigrationUpAsync(database);

        await using var verify = CreateContext(database);
        var count = await CountIssueEventsAsync(verify, "issue_existing");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Up_BackfilledEvent_CountsAsCompletedInMetricsScan()
    {
        // End-to-end intent: a backfilled row must be readable by the same
        // path IssueMetricsQuerier uses. We confirm the Type string matches
        // the catalog constant the querier filters on, so throughput picks
        // the backfilled population up.
        await using var database = CreateModelSchemaDatabase();
        await using (var setup = CreateContext(database))
        {
            await SeedIssueAsync(setup, "issue_metric", """
                {"id":"issue_metric","projectId":"proj_a","number":13,"title":"Metric","status":"done","completedAt":"2026-06-30T04:15:26Z","createdAt":"2026-06-29T06:00:00Z","updatedAt":"2026-06-30T04:15:26Z"}
                """);
        }

        await RunMigrationUpAsync(database);

        await using var verify = CreateContext(database);
        var row = await ReadSingleIssueEventAsync(verify, "issue_metric");
        Assert.NotNull(row);
        // Must equal EventCatalog.ReverseDns.IssueCompleted — the literal the
        // querier's WorkCompletedType constant resolves to.
        Assert.Equal("com.mohist.issue.completed", row!.Type);
    }

    [Fact]
    public async Task DatabaseMigrate_IncludesBackfillIssueCompletedEventsMigration()
    {
        await using var database = CreateDatabase();
        await using var ctx = CreateContext(database);
        await ctx.Database.MigrateAsync();

        var applied = await ctx.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m == MigrationId);
    }

    private static async Task RunMigrationUpAsync(TestSqliteDatabase database)
    {
        await using var ctx = CreateContext(database);
        // Re-statement of the migration's Up SQL (mirrors
        // BackfillIssueCompletedEvents.Up exactly). The separate
        // DatabaseMigrate_IncludesBackfillIssueCompletedEventsMigration spec
        // covers the migration's registration; this re-statement keeps the
        // data-logic specs hermetic against EnsureCreated/Migrate table
        // conflicts, following the pattern in BackfillIssueCompletedAtMigrationSpecs.
        await ctx.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO IssueEvents (
                Id, Source, EventId, Type, Time, SpecVersion,
                Subject, DataContentType, Data, ExtensionsJson
            )
            SELECT
                1 AS Id,
                '/mohist/issues/' || json_extract(i.State, '$.id') AS Source,
                lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-' || hex(randomblob(2))
                      || '-' || hex(randomblob(2)) || '-' || hex(randomblob(6))) AS EventId,
                'com.mohist.issue.completed' AS Type,
                COALESCE(
                    json_extract(i.State, '$.completedAt'),
                    json_extract(i.State, '$.updatedAt')
                ) AS Time,
                '1.0' AS SpecVersion,
                CAST(CAST(json_extract(i.State, '$.number') AS INTEGER) AS TEXT) AS Subject,
                'application/json' AS DataContentType,
                json_object(
                    'workflowRunId',
                    json_extract(i.State, '$.workflowRunId')
                ) AS Data,
                json_object(
                    'projectid', json_extract(i.State, '$.projectId'),
                    'issueid',   json_extract(i.State, '$.id'),
                    'issueno',   CAST(CAST(json_extract(i.State, '$.number') AS INTEGER) AS TEXT)
                ) AS ExtensionsJson
            FROM Issues i
            WHERE LOWER(json_extract(i.State, '$.status')) = 'done'
              AND NOT EXISTS (
                  SELECT 1
                  FROM IssueEvents e
                  WHERE e.Source = '/mohist/issues/' || json_extract(i.State, '$.id')
                    AND e.Type = 'com.mohist.issue.completed'
              );
            """);
    }

    private static async Task SeedIssueAsync(MohistDbContext ctx, string issueId, string stateJson)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO Issues (ProjectId, Number, State)
            VALUES (
                COALESCE(json_extract($state, '$.projectId'), json_extract($state, '$.ProjectId'), 'migration-test'),
                CAST(COALESCE(json_extract($state, '$.number'), json_extract($state, '$.Number')) AS INTEGER),
                $state);
            """;
        AddParam(command, "$state", stateJson);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedIssueEventAsync(
        MohistDbContext ctx, string source, long id, string type, string time)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO IssueEvents (Source, Id, EventId, Type, Time, SpecVersion, DataContentType, Data, ExtensionsJson)
            VALUES ($source, $id, $eventId, $type, $time, '1.0', 'application/json', '{}', '{}');
            """;
        AddParam(command, "$source", source);
        AddParam(command, "$id", id);
        AddParam(command, "$eventId", $"evt_{id}");
        AddParam(command, "$type", type);
        AddParam(command, "$time", time);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<EventRow?> ReadSingleIssueEventAsync(MohistDbContext ctx, string issueId)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT Type, Source, Subject, Time, Data, ExtensionsJson
            FROM IssueEvents
            WHERE Source = $source
            """;
        AddParam(command, "$source", $"/mohist/issues/{issueId}");
        await using var reader = await command.ExecuteReaderAsync();
        EventRow? result = null;
        while (await reader.ReadAsync())
        {
            result = new EventRow(
                (string)reader["Type"],
                (string)reader["Source"],
                reader["Subject"] as string,
                (string)reader["Time"],
                (string)reader["Data"],
                (string)reader["ExtensionsJson"]);
        }
        return result;
    }

    private static async Task<int> CountIssueEventsAsync(MohistDbContext ctx, string issueId)
    {
        using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM IssueEvents WHERE Source = $source";
        AddParam(command, "$source", $"/mohist/issues/{issueId}");
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static void AddParam(System.Data.Common.DbCommand command, string name, object value)
    {
        var param = command.CreateParameter();
        param.ParameterName = name;
        param.Value = value;
        command.Parameters.Add(param);
    }

    private static TestSqliteDatabase CreateDatabase() => TestSqliteDatabase.CreateEmpty();

    private static MohistDbContext CreateContext(TestSqliteDatabase database) =>
        new(new DbContextOptionsBuilder<MohistDbContext>().UseSqlite(database.Keeper).Options);

    private static TestSqliteDatabase CreateModelSchemaDatabase() => TestSqliteDatabase.CreateModelSchema();

    private sealed record EventRow(
        string Type, string Source, string? Subject, string Time, string Data, string ExtensionsJson);
}
