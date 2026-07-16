using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Migrations;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

public class CanonicalEpicReferenceMigrationSpecs
{
    private const string ReferenceColumns = "20260716150000_AddCanonicalEpicReferenceColumns";
    private const string TargetMigration = "20260716160000_BackfillCanonicalEpicReferences";
    private static readonly DateTimeOffset SeedTime = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_BackfillsEveryCurrentOwnerWithoutDeletingOrCrossingProjects()
    {
        await using var database = CreateDatabase(ReferenceColumns);
        await using var context = database.CreateDbContext();
        await SeedOwnerGraphAsync(context);
        var countsBefore = await CountOwnersAsync(context);
        var historicalBefore = await ReadHistoricalEventAsync(context);

        await context.GetService<IMigrator>().MigrateAsync(TargetMigration);

        await using var verify = database.CreateDbContext();
        Assert.Equal(countsBefore, await CountOwnersAsync(verify));
        Assert.Equal(historicalBefore, await ReadHistoricalEventAsync(verify));

        var epics = await verify.Epics.AsNoTracking().OrderBy(row => row.ProjectId).ToListAsync();
        Assert.Collection(
            epics,
            row => Assert.Equal(("proj_alpha", 7), (row.ProjectId, row.Number)),
            row => Assert.Equal(("proj_beta", 7), (row.ProjectId, row.Number)));

        var links = await verify.EpicIssues.AsNoTracking().OrderBy(row => row.ProjectId).ToListAsync();
        Assert.Collection(
            links,
            row => Assert.Equal(("proj_alpha", 7, 42), (row.ProjectId, row.EpicNumber, row.IssueNumber)),
            row => Assert.Equal(("proj_beta", 7, 42), (row.ProjectId, row.EpicNumber, row.IssueNumber)));

        var active = await verify.EpicActiveIssues.AsNoTracking().OrderBy(row => row.ProjectId).ToListAsync();
        Assert.Collection(
            active,
            row => Assert.Equal(("proj_alpha", 7, 42), (row.ProjectId, row.EpicNumber, row.IssueNumber)),
            row => Assert.Equal(("proj_beta", 7, 42), (row.ProjectId, row.EpicNumber, row.IssueNumber)));

        var issues = await verify.Issues.AsNoTracking().OrderBy(row => row.ProjectId).ToListAsync();
        Assert.All(issues, row => Assert.Equal(7, row.EpicNumber));

        var runs = await verify.WorkflowRuns.AsNoTracking().OrderBy(row => row.MetadataProjectId).ToListAsync();
        Assert.Collection(
            runs,
            row => Assert.Equal(("proj_alpha", 7), (row.MetadataProjectId, row.EpicNumber)),
            row => Assert.Equal(("proj_beta", 7), (row.MetadataProjectId, row.EpicNumber)));

        var sessions = await verify.AgentSessions.AsNoTracking().OrderBy(row => row.LabelProjectId).ToListAsync();
        Assert.Collection(
            sessions,
            row => Assert.Equal(("proj_alpha", "7"), (row.LabelProjectId, row.LabelAgentLaunchEpicNumber)),
            row => Assert.Equal(("proj_beta", "7"), (row.LabelProjectId, row.LabelAgentLaunchEpicNumber)));
    }

    [Fact]
    public async Task Reconciliation_IsIdempotentAfterTheRealMigration()
    {
        await using var database = CreateDatabase(ReferenceColumns);
        await using var context = database.CreateDbContext();
        await SeedOwnerGraphAsync(context);
        await context.GetService<IMigrator>().MigrateAsync(TargetMigration);

        await CanonicalEpicReferenceReconciliation.ApplyAsync(context);
        var once = await ReadCanonicalSnapshotAsync(context);
        await CanonicalEpicReferenceReconciliation.ApplyAsync(context);
        var twice = await ReadCanonicalSnapshotAsync(context);

        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("epic", "CK_CanonicalEpicReference_Epics")]
    [InlineData("link", "CK_CanonicalEpicReference_EpicIssues")]
    [InlineData("active", "CK_CanonicalEpicReference_EpicActiveIssues")]
    [InlineData("issue", "CK_CanonicalEpicReference_Issues")]
    [InlineData("workflow", "CK_CanonicalEpicReference_WorkflowRuns")]
    [InlineData("session", "CK_CanonicalEpicReference_Sessions")]
    public async Task Reconciliation_FailsExplicitlyForUnresolvedOrMismatchedOwner(
        string owner,
        string expectedConstraint)
    {
        await using var database = CreateDatabase(ReferenceColumns);
        await using var context = database.CreateDbContext();
        await SeedBaseAsync(context);

        switch (owner)
        {
            case "epic":
                await InsertEpicAsync(context, "epic_duplicate_7", "proj_alpha", 7);
                break;
            case "link":
                await InsertLinkAsync(context, "epic_missing", "issue_alpha_42", active: false);
                break;
            case "active":
                await InsertLinkAsync(context, "epic_missing", "issue_alpha_42", active: true);
                break;
            case "issue":
                await SetIssueEpicAsync(context, "issue_alpha_42", "epic_missing");
                break;
            case "workflow":
                await InsertWorkflowAsync(context, "run_bad", "proj_alpha", "issue_alpha_42", "epic_missing");
                break;
            case "session":
                await InsertSessionAsync(context, "session_bad", "proj_alpha", "8");
                break;
        }

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => CanonicalEpicReferenceReconciliation.ApplyAsync(context));
        Assert.Contains(expectedConstraint, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_RequiresCanonicalRootsAndRelationsAndCreatesScopedIndexes()
    {
        await using var database = CreateDatabase(TargetMigration);
        await using var context = database.CreateDbContext();

        var requiredColumns = await context.Database.SqlQueryRaw<string>("""
            SELECT 'Epics.' || "name" AS "Value" FROM pragma_table_info('Epics')
            WHERE "name" = 'Number' AND "notnull" = 1
            UNION ALL
            SELECT 'EpicIssues.' || "name" AS "Value" FROM pragma_table_info('EpicIssues')
            WHERE "name" = 'EpicNumber' AND "notnull" = 1
            UNION ALL
            SELECT 'EpicActiveIssues.' || "name" AS "Value" FROM pragma_table_info('EpicActiveIssues')
            WHERE "name" = 'EpicNumber' AND "notnull" = 1
            ORDER BY "Value"
            """).ToListAsync();
        Assert.Equal(
            new[] { "EpicActiveIssues.EpicNumber", "EpicIssues.EpicNumber", "Epics.Number" },
            requiredColumns);

        var indexes = await context.Database.SqlQueryRaw<string>("""
            SELECT "name" AS "Value"
            FROM sqlite_master
            WHERE type = 'index'
            ORDER BY "name"
            """).ToListAsync();
        Assert.Contains("IX_Epics_ProjectId_Number", indexes);
        Assert.Contains("IX_EpicIssues_ProjectId_EpicNumber_IssueNumber", indexes);
        Assert.Contains("IX_EpicActiveIssues_ProjectId_EpicNumber_IssueNumber", indexes);
        Assert.Contains("IX_Issues_ProjectId_EpicNumber_Number", indexes);
        Assert.Contains("IX_WorkflowRuns_ProjectId_EpicNumber", indexes);
        Assert.Contains("IX_AgentSessions_LabelProjectId_LabelAgentLaunchEpicNumber_CreatedAt", indexes);
    }

    [Fact]
    public async Task CurrentStateWritersPersistCanonicalEpicNumber()
    {
        await using var database = CreateDatabase(TargetMigration);
        await using (var seed = database.CreateDbContext())
        {
            await InsertProjectAsync(seed, "proj_alpha", "alpha");
            await InsertEpicAsync(seed, "epic_alpha_7", "proj_alpha", 7);
        }

        var issue = DomainIssue.Create(
            "issue_writer",
            "proj_alpha",
            42,
            "writer",
            now: SeedTime.UtcDateTime);
        issue.SetEpicId("epic_alpha_7", SeedTime.UtcDateTime);
        var issueStore = new IssueStore(
            database.Factory,
            null!,
            null!,
            NullLogger<IssueStore>.Instance);
        await issueStore.SaveAsync(issue.Id, issue);

        var run = new WorkflowRun
        {
            Id = "run_writer",
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: SeedTime,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = "proj_alpha",
                    ["issueId"] = issue.Id,
                    ["issueNumber"] = "42",
                    ["epicId"] = "epic_alpha_7",
                }),
            Stages = [],
        };
        var workflowStore = new WorkflowRunStore(
            database.Factory,
            null!,
            null!,
            NullLogger<WorkflowRunStore>.Instance);
        await workflowStore.SaveAsync(run);

        await using var verify = database.CreateDbContext();
        Assert.Equal(7, (await verify.Issues.SingleAsync(row => row.IssueId == issue.Id)).EpicNumber);
        Assert.Equal(7, (await verify.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == run.Id)).EpicNumber);
    }

    private static async Task SeedOwnerGraphAsync(MohistDbContext context)
    {
        await InsertProjectAsync(context, "proj_alpha", "alpha");
        await InsertProjectAsync(context, "proj_beta", "beta");
        await InsertEpicAsync(context, "epic_alpha_7", "proj_alpha", 7);
        await InsertEpicAsync(context, "epic_beta_7", "proj_beta", 7);
        await InsertIssueAsync(context, "issue_alpha_42", "proj_alpha", 42, "epic_alpha_7");
        await InsertIssueAsync(context, "issue_beta_42", "proj_beta", 42, "epic_beta_7");
        await InsertLinkAsync(context, "epic_alpha_7", "issue_alpha_42", active: false, projectId: "proj_alpha");
        await InsertLinkAsync(context, "epic_beta_7", "issue_beta_42", active: false, projectId: "proj_beta");
        await InsertLinkAsync(context, "epic_alpha_7", "issue_alpha_42", active: true, projectId: "proj_alpha");
        await InsertLinkAsync(context, "epic_beta_7", "issue_beta_42", active: true, projectId: "proj_beta");
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
        await InsertEpicAsync(context, "epic_alpha_7", "proj_alpha", 7);
        await InsertIssueAsync(context, "issue_alpha_42", "proj_alpha", 42, null);
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

    private static async Task InsertIssueAsync(
        MohistDbContext context,
        string issueId,
        string projectId,
        int issueNumber,
        string? epicId)
    {
        var state = IssueStore.Serialize(new DomainIssue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = issueId,
            Priority = "p2",
            Status = IssueStatus.Backlog,
            EpicId = epicId,
            CreatedAt = SeedTime.UtcDateTime,
            UpdatedAt = SeedTime.UtcDateTime,
        });
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Issues" ("IssueId", "State", "Risk", "EpicId")
            VALUES ({issueId}, {state}, NULL, {epicId})
            """);
    }

    private static async Task SetIssueEpicAsync(
        MohistDbContext context,
        string issueId,
        string epicId)
    {
        var row = await context.Issues.AsNoTracking().SingleAsync(item => item.IssueId == issueId);
        var issue = IssueStore.Deserialize(row.State)!;
        issue.SetEpicId(epicId, SeedTime.UtcDateTime);
        var state = IssueStore.Serialize(issue);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Issues" SET "State" = {state}, "EpicId" = {epicId}
            WHERE "IssueId" = {issueId}
            """);
    }

    private static Task InsertLinkAsync(
        MohistDbContext context,
        string epicId,
        string issueId,
        bool active,
        string projectId = "proj_alpha")
    {
        var sql = active
            ? """
              INSERT INTO "EpicActiveIssues" ("ProjectId", "IssueId", "EpicId", "IssueNumber", "CreatedAt")
              VALUES ({0}, {1}, {2}, 42, {3})
              """
            : """
              INSERT INTO "EpicIssues" ("ProjectId", "IssueId", "EpicId", "IssueNumber", "CreatedAt")
              VALUES ({0}, {1}, {2}, 42, {3})
              """;
        return context.Database.ExecuteSqlRawAsync(sql, projectId, issueId, epicId, SeedTime);
    }

    private static async Task InsertWorkflowAsync(
        MohistDbContext context,
        string runId,
        string projectId,
        string issueId,
        string epicId)
    {
        var state = JSON.Serialize(new
        {
            id = runId,
            status = "running",
            metadata = new
            {
                createdAt = SeedTime,
                annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = projectId,
                    ["issueId"] = issueId,
                    ["issueNumber"] = "42",
                    ["epicId"] = epicId,
                },
            },
            stages = Array.Empty<object>(),
        });
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "EpicId", "ETag")
            VALUES ({runId}, {state}, {epicId}, 1)
            """);
    }

    private static async Task InsertSessionAsync(
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
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AgentSessions" ("Id", "State", "Status", "CreatedAt")
            VALUES ({sessionId}, {state}, 'opened', {SeedTime.UtcDateTime})
            """);
    }

    private static async Task<OwnerCounts> CountOwnersAsync(MohistDbContext context) => new(
        await context.Epics.CountAsync(),
        await context.EpicIssues.CountAsync(),
        await context.EpicActiveIssues.CountAsync(),
        await context.Issues.CountAsync(),
        await context.WorkflowRuns.CountAsync(),
        await context.AgentSessions.CountAsync(),
        await context.EpicEvents.CountAsync());

    private static async Task<HistoricalEvent> ReadHistoricalEventAsync(MohistDbContext context)
    {
        var row = await context.EpicEvents.AsNoTracking().SingleAsync(item => item.EventId == "event_legacy");
        return new HistoricalEvent(row.Source, row.Data.GetRawText(), row.ExtensionsJson);
    }

    private static async Task<CanonicalSnapshot> ReadCanonicalSnapshotAsync(MohistDbContext context)
    {
        var link = await context.EpicIssues.AsNoTracking().OrderBy(row => row.ProjectId).FirstAsync();
        var active = await context.EpicActiveIssues.AsNoTracking().OrderBy(row => row.ProjectId).FirstAsync();
        var issue = await context.Issues.AsNoTracking().OrderBy(row => row.ProjectId).FirstAsync();
        var run = await context.WorkflowRuns.AsNoTracking().OrderBy(row => row.MetadataProjectId).FirstAsync();
        return new CanonicalSnapshot(link.EpicNumber, active.EpicNumber, issue.EpicNumber, run.EpicNumber);
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

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<MohistDbContext> _options;

        public TestDatabase(SqliteConnection connection, DbContextOptions<MohistDbContext> options)
        {
            _connection = connection;
            _options = options;
            Factory = new DbContextFactory(options);
        }

        public IDbContextFactory<MohistDbContext> Factory { get; }
        public MohistDbContext CreateDbContext() => new(_options);
        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed class DbContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }

    private sealed record OwnerCounts(
        int Epics,
        int Links,
        int Active,
        int Issues,
        int Runs,
        int Sessions,
        int Events);

    private sealed record HistoricalEvent(string Source, string Data, string Extensions);
    private sealed record CanonicalSnapshot(int Link, int Active, int? Issue, int? Workflow);
}
