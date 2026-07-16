using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

public sealed class CanonicalEpicReferenceMigrationSpecs
{
    private const string BeforeMigration = "20260715123000_ReconcileLineageSnapshotsFromMembership";
    private const string TargetMigration = "20260716160000_BackfillCanonicalEpicReferences";
    private static readonly DateTimeOffset SeedTime = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_FromImmediatePreviousSchema_PreservesScopedEpicReferencesAndHistory()
    {
        await using var database = CreateDatabase(BeforeMigration);
        await using var context = database.CreateDbContext();
        await SeedOwnerGraphAsync(context);
        var before = await CountOwnersAsync(context);
        var historicalBefore = await ReadHistoricalEventAsync(context);

        await context.GetService<IMigrator>().MigrateAsync(TargetMigration);

        await using var verify = database.CreateDbContext();
        Assert.Equal(before, await CountOwnersAsync(verify));
        Assert.Equal(historicalBefore, await ReadHistoricalEventAsync(verify));

        Assert.Equal(
            new[] { "proj_alpha:7", "proj_beta:7" },
            await ReadStringsAsync(verify, "SELECT \"ProjectId\" || ':' || \"Number\" AS \"Value\" FROM \"Epics\" ORDER BY \"ProjectId\""));
        Assert.Equal(
            new[] { "proj_alpha:7:42", "proj_beta:7:42" },
            await ReadStringsAsync(verify, "SELECT \"ProjectId\" || ':' || \"EpicNumber\" || ':' || \"IssueNumber\" AS \"Value\" FROM \"EpicIssues\" ORDER BY \"ProjectId\""));
        Assert.Equal(
            new[] { "proj_alpha:7:42", "proj_beta:7:42" },
            await ReadStringsAsync(verify, "SELECT \"ProjectId\" || ':' || \"EpicNumber\" || ':' || \"IssueNumber\" AS \"Value\" FROM \"EpicActiveIssues\" ORDER BY \"ProjectId\""));
        Assert.Equal(
            new[] { "proj_alpha:42:7", "proj_beta:42:7" },
            await ReadStringsAsync(verify, "SELECT \"ProjectId\" || ':' || \"Number\" || ':' || \"EpicNumber\" AS \"Value\" FROM \"Issues\" ORDER BY \"ProjectId\""));
        Assert.Equal(
            new[] { "proj_alpha:7", "proj_beta:7" },
            await ReadStringsAsync(verify, "SELECT \"MetadataProjectId\" || ':' || \"EpicNumber\" AS \"Value\" FROM \"WorkflowRuns\" ORDER BY \"MetadataProjectId\""));
        Assert.Equal(
            new[] { "proj_alpha:7", "proj_beta:7" },
            await ReadStringsAsync(verify, "SELECT \"LabelProjectId\" || ':' || \"LabelAgentLaunchEpicNumber\" AS \"Value\" FROM \"AgentSessions\" ORDER BY \"LabelProjectId\""));
    }

    [Fact]
    public async Task Reconciliation_AfterTheRealMigration_IsIdempotent()
    {
        await using var database = CreateDatabase(BeforeMigration);
        await using var context = database.CreateDbContext();
        await SeedOwnerGraphAsync(context);
        await context.GetService<IMigrator>().MigrateAsync(TargetMigration);

        await CanonicalEpicReferenceReconciliation.ApplyAsync(context);
        var once = await ReadCanonicalSnapshotAsync(context);
        await CanonicalEpicReferenceReconciliation.ApplyAsync(context);
        var twice = await ReadCanonicalSnapshotAsync(context);

        Assert.Equal(once.Links, twice.Links);
        Assert.Equal(once.Active, twice.Active);
        Assert.Equal(once.Issues, twice.Issues);
        Assert.Equal(once.Runs, twice.Runs);
    }

    [Fact]
    public async Task Migration_MakesTemporaryRelationEpicNumbersRequiredAndCreatesScopedIndexes()
    {
        await using var database = CreateDatabase(TargetMigration);
        await using var context = database.CreateDbContext();

        var required = await ReadStringsAsync(context, """
            SELECT 'EpicIssues.' || "name" AS "Value"
            FROM pragma_table_info('EpicIssues')
            WHERE "name" = 'EpicNumber' AND "notnull" = 1
            UNION ALL
            SELECT 'EpicActiveIssues.' || "name" AS "Value"
            FROM pragma_table_info('EpicActiveIssues')
            WHERE "name" = 'EpicNumber' AND "notnull" = 1
            ORDER BY "Value"
            """);
        Assert.Equal(new[] { "EpicActiveIssues.EpicNumber", "EpicIssues.EpicNumber" }, required);

        var indexes = await ReadStringsAsync(context, """
            SELECT "name" AS "Value"
            FROM sqlite_master
            WHERE type = 'index'
            ORDER BY "name"
            """);
        Assert.Contains("IX_Epics_ProjectId_Number", indexes);
        Assert.Contains("IX_EpicIssues_ProjectId_EpicNumber_IssueNumber", indexes);
        Assert.Contains("IX_EpicActiveIssues_ProjectId_EpicNumber_IssueNumber", indexes);
        Assert.Contains("IX_Issues_ProjectId_EpicNumber_Number", indexes);
        Assert.Contains("IX_WorkflowRuns_ProjectId_EpicNumber", indexes);
        Assert.Contains("IX_AgentSessions_LabelProjectId_LabelAgentLaunchEpicNumber_CreatedAt", indexes);
    }

    [Theory]
    [InlineData("epic")]
    [InlineData("link")]
    [InlineData("active")]
    [InlineData("issue")]
    [InlineData("workflow")]
    [InlineData("session")]
    public async Task Migration_UnresolvableOwnerFailsWithoutDeletingRows(string owner)
    {
        await using var database = CreateDatabase(BeforeMigration);
        await using var context = database.CreateDbContext();
        await SeedBaseAsync(context);

        switch (owner)
        {
            case "epic":
                await InsertEpicAsync(context, "epic_alpha_duplicate", "proj_alpha", 7);
                break;
            case "link":
                await InsertLinkAsync(context, "epic_missing", "issue_alpha_42", active: false, "proj_alpha");
                break;
            case "active":
                const string missingActiveEpicId = "epic_missing";
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"EpicActiveIssues\" SET \"EpicId\" = {missingActiveEpicId} WHERE \"ProjectId\" = 'proj_alpha' AND \"IssueId\" = 'issue_alpha_42'");
                break;
            case "issue":
                const string missingEpicId = "epic_missing";
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Issues\" SET \"EpicId\" = {missingEpicId} WHERE \"IssueId\" = 'issue_alpha_42'");
                break;
            case "workflow":
                await InsertWorkflowAsync(context, "run_bad", "proj_alpha", "issue_alpha_42", "epic_missing");
                break;
            case "session":
                await InsertSessionAsync(context, "session_bad", "proj_alpha", "8");
                break;
        }

        var before = await CountOwnersAsync(context);
        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => context.GetService<IMigrator>().MigrateAsync(TargetMigration));

        Assert.Contains("CHECK constraint failed", exception.Message, StringComparison.Ordinal);
        await using var verify = database.CreateDbContext();
        Assert.Equal(before, await CountOwnersAsync(verify));
    }

    private static async Task SeedOwnerGraphAsync(MohistDbContext context)
    {
        await SeedBaseAsync(context);
        await InsertEpicAsync(context, "epic_beta_7", "proj_beta", 7);
        await InsertIssueAsync(context, "issue_beta_42", "proj_beta", 42, "epic_beta_7");
        await InsertLinkAsync(context, "epic_beta_7", "issue_beta_42", active: false, "proj_beta");
        await InsertLinkAsync(context, "epic_beta_7", "issue_beta_42", active: true, "proj_beta");
        await InsertWorkflowAsync(context, "run_alpha", "proj_alpha", "issue_alpha_42", "epic_alpha_7");
        await InsertWorkflowAsync(context, "run_beta", "proj_beta", "issue_beta_42", "epic_beta_7");
        await InsertSessionAsync(context, "session_alpha", "proj_alpha", "7");
        await InsertSessionAsync(context, "session_beta", "proj_beta", "7");

        const string data = "{\"legacy\":true}";
        const string extensions = "{\"epicid\":\"epic_alpha_7\",\"custom\":\"preserve\"}";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "EpicEvents" (
                "Source", "Id", "EventId", "Type", "SpecVersion", "Subject",
                "DataContentType", "Data", "ExtensionsJson", "Time", "DispatchedAt")
            VALUES ('/mohist/epics/epic_alpha_7', 1, 'event_legacy', 'com.mohist.epic.created',
                    '1.0', '7', 'application/json', {data}, {extensions}, {SeedTime}, NULL)
            """);
    }

    private static async Task SeedBaseAsync(MohistDbContext context)
    {
        await InsertProjectAsync(context, "proj_alpha", "alpha");
        await InsertProjectAsync(context, "proj_beta", "beta");
        await InsertEpicAsync(context, "epic_alpha_7", "proj_alpha", 7);
        await InsertIssueAsync(context, "issue_alpha_42", "proj_alpha", 42, "epic_alpha_7");
        await InsertLinkAsync(context, "epic_alpha_7", "issue_alpha_42", active: false, "proj_alpha");
        await InsertLinkAsync(context, "epic_alpha_7", "issue_alpha_42", active: true, "proj_alpha");
    }

    private static Task InsertProjectAsync(MohistDbContext context, string projectId, string name) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Projects" ("Id", "Name", "RepositoriesJson", "CreatedAt", "UpdatedAt")
            VALUES ({projectId}, {name}, '[]', {SeedTime}, {SeedTime})
            """);

    private static Task InsertEpicAsync(
        MohistDbContext context,
        string epicId,
        string projectId,
        int number) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Epics" (
                "Id", "ProjectId", "Number", "Title", "Description", "Priority", "Status", "CreatedAt", "UpdatedAt")
            VALUES ({epicId}, {projectId}, {number}, {epicId}, '', 'p2', 'running', {SeedTime}, {SeedTime})
            """);

    private static Task InsertIssueAsync(
        MohistDbContext context,
        string issueId,
        string projectId,
        int issueNumber,
        string epicId) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Issues" ("IssueId", "State", "Risk", "EpicId", "LineageVersion")
            VALUES ({issueId}, {IssueState(issueId, projectId, issueNumber, epicId)}, NULL, {epicId}, 1)
            """);

    private static string IssueState(string issueId, string projectId, int issueNumber, string epicId) =>
        JSON.Serialize(new
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = issueId,
            Priority = "p2",
            Status = "backlog",
            EpicId = epicId,
        });

    private static Task InsertLinkAsync(
        MohistDbContext context,
        string epicId,
        string issueId,
        bool active,
        string projectId) =>
        active
            ? context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "EpicActiveIssues" ("ProjectId", "IssueId", "EpicId", "IssueNumber", "CreatedAt")
                VALUES ({projectId}, {issueId}, {epicId}, 42, {SeedTime})
                """)
            : context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "EpicIssues" ("ProjectId", "IssueId", "EpicId", "IssueNumber", "CreatedAt")
                VALUES ({projectId}, {issueId}, {epicId}, 42, {SeedTime})
                """);

    private static async Task InsertWorkflowAsync(
        MohistDbContext context,
        string runId,
        string projectId,
        string issueId,
        string epicId)
    {
        var state = JSON.Serialize(new
        {
            Id = runId,
            Status = "running",
            Metadata = new
            {
                CreatedAt = SeedTime,
                Annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = projectId,
                    ["issueId"] = issueId,
                    ["issueNumber"] = "42",
                    ["epicId"] = epicId,
                },
            },
            Stages = Array.Empty<object>(),
        });
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "EpicId", "ETag")
            VALUES ({runId}, {state}, {epicId}, 1)
            """);
    }

    private static Task InsertSessionAsync(
        MohistDbContext context,
        string sessionId,
        string projectId,
        string epicNumber)
    {
        var state = JSON.Serialize(new
        {
            metadata = new
            {
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["mohist.io/project-id"] = projectId,
                    ["mohist.io/agent-launch/epic-number"] = epicNumber,
                },
            },
        });
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AgentSessions" ("Id", "State", "Status", "CreatedAt")
            VALUES ({sessionId}, {state}, 'opened', {SeedTime.UtcDateTime})
            """);
    }

    private static async Task<OwnerCounts> CountOwnersAsync(MohistDbContext context) => new(
        await CountAsync(context, "Epics"),
        await CountAsync(context, "EpicIssues"),
        await CountAsync(context, "EpicActiveIssues"),
        await CountAsync(context, "Issues"),
        await CountAsync(context, "WorkflowRuns"),
        await CountAsync(context, "AgentSessions"),
        await CountAsync(context, "EpicEvents"));

    private static Task<long> CountAsync(MohistDbContext context, string table) => table switch
    {
        "Epics" => context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"Epics\"").SingleAsync(),
        "EpicIssues" => context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"EpicIssues\"").SingleAsync(),
        "EpicActiveIssues" => context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"EpicActiveIssues\"").SingleAsync(),
        "Issues" => context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"Issues\"").SingleAsync(),
        "WorkflowRuns" => context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"WorkflowRuns\"").SingleAsync(),
        "AgentSessions" => context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"AgentSessions\"").SingleAsync(),
        "EpicEvents" => context.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"EpicEvents\"").SingleAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Unknown owner table."),
    };

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(MohistDbContext context, string sql) =>
        await context.Database.SqlQueryRaw<string>(sql).ToListAsync();

    private static async Task<HistoricalEvent> ReadHistoricalEventAsync(MohistDbContext context)
    {
        var source = await context.Database.SqlQueryRaw<string>("""
            SELECT "Source" AS "Value"
            FROM "EpicEvents"
            WHERE "EventId" = 'event_legacy'
            """).SingleAsync();
        var data = await context.Database.SqlQueryRaw<string>("""
            SELECT "Data" AS "Value"
            FROM "EpicEvents"
            WHERE "EventId" = 'event_legacy'
            """).SingleAsync();
        var extensions = await context.Database.SqlQueryRaw<string>("""
            SELECT "ExtensionsJson" AS "Value"
            FROM "EpicEvents"
            WHERE "EventId" = 'event_legacy'
            """).SingleAsync();
        return new HistoricalEvent(source, data, extensions);
    }

    private static async Task<CanonicalSnapshot> ReadCanonicalSnapshotAsync(MohistDbContext context)
    {
        var links = await ReadStringsAsync(context, "SELECT \"ProjectId\" || ':' || \"EpicNumber\" || ':' || \"IssueNumber\" AS \"Value\" FROM \"EpicIssues\" ORDER BY \"ProjectId\"");
        var active = await ReadStringsAsync(context, "SELECT \"ProjectId\" || ':' || \"EpicNumber\" || ':' || \"IssueNumber\" AS \"Value\" FROM \"EpicActiveIssues\" ORDER BY \"ProjectId\"");
        var issues = await ReadStringsAsync(context, "SELECT \"ProjectId\" || ':' || \"Number\" || ':' || \"EpicNumber\" AS \"Value\" FROM \"Issues\" ORDER BY \"ProjectId\"");
        var runs = await ReadStringsAsync(context, "SELECT \"MetadataProjectId\" || ':' || \"EpicNumber\" AS \"Value\" FROM \"WorkflowRuns\" ORDER BY \"MetadataProjectId\"");
        return new CanonicalSnapshot(links, active, issues, runs);
    }

    private static TestDatabase CreateDatabase(string targetMigration)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyTo(connection, targetMigration);
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        return new TestDatabase(connection, options);
    }

    private sealed class TestDatabase(SqliteConnection connection, DbContextOptions<MohistDbContext> options)
        : IAsyncDisposable
    {
        public MohistDbContext CreateDbContext() => new(options);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed record OwnerCounts(long Epics, long Links, long Active, long Issues, long Runs, long Sessions, long Events);
    private sealed record HistoricalEvent(string Source, string Data, string ExtensionsJson);
    private sealed record CanonicalSnapshot(
        IReadOnlyList<string> Links,
        IReadOnlyList<string> Active,
        IReadOnlyList<string> Issues,
        IReadOnlyList<string> Runs);
}
