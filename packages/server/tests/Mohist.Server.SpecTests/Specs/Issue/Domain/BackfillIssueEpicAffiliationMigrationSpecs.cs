using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Domain;

public class BackfillIssueEpicAffiliationMigrationSpecs
{
    private static readonly DateTimeOffset FirstLinkTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private const string AtomicSnapshotMigration = "20260715123000_ReconcileLineageSnapshotsFromMembership";

    [Fact]
    public async Task Migration_BackfillsActiveAndTerminalMembershipsWithDeterministicFallback()
    {
        await using var database = CreateDatabase("20260714120000_AddProjectEventReadKeys");
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        await SeedIssueAsync(context, NewIssue("issue_active", 1));
        await SeedIssueAsync(context, NewIssue("issue_done", 2));
        await SeedIssueAsync(context, NewIssue("issue_closed", 3));
        await SeedIssueAsync(context, NewIssue("issue_tied", 4));
        context.EpicIssues.AddRange(
            NewLink("epic_active", "issue_active", 1, FirstLinkTime),
            NewLink("epic_done", "issue_done", 2, FirstLinkTime),
            NewLink("epic_closed", "issue_closed", 3, FirstLinkTime),
            NewLink("epic_z", "issue_tied", 4, FirstLinkTime),
            NewLink("epic_a", "issue_tied", 4, FirstLinkTime));
        context.EpicActiveIssues.Add(new EpicActiveIssueRow
        {
            ProjectId = "project_1",
            IssueId = "issue_active",
            EpicId = "epic_active",
            IssueNumber = 1,
            CreatedAt = FirstLinkTime,
        });
        await context.SaveChangesAsync();

        await migrator.MigrateAsync(AtomicSnapshotMigration);

        var states = await context.Issues.AsNoTracking()
            .OrderBy(row => row.IssueId)
            .ToDictionaryAsync(row => row.IssueId, row => IssueStore.Deserialize(row.State)!);
        Assert.Equal("epic_active", states["issue_active"].EpicId);
        Assert.Equal("epic_done", states["issue_done"].EpicId);
        Assert.Equal("epic_closed", states["issue_closed"].EpicId);
        Assert.Equal("epic_a", states["issue_tied"].EpicId);
        var snapshots = await context.Issues.AsNoTracking()
            .OrderBy(row => row.IssueId)
            .ToDictionaryAsync(row => row.IssueId, row => row.EpicId);
        Assert.Equal("epic_active", snapshots["issue_active"]);
        Assert.Equal("epic_done", snapshots["issue_done"]);
        Assert.Equal("epic_closed", snapshots["issue_closed"]);
        Assert.Equal("epic_a", snapshots["issue_tied"]);

        var eventStore = new EventStore(database.Factory, NullLogger<EventStore>.Instance);
        var issueStore = new IssueStore(database.Factory, eventStore, null!, NullLogger<IssueStore>.Instance);
        foreach (var (issueId, expectedEpicId) in new Dictionary<string, string>(StringComparer.Ordinal)
                 {
                     ["issue_active"] = "epic_active",
                     ["issue_done"] = "epic_done",
                     ["issue_closed"] = "epic_closed",
                 })
        {
            await issueStore.SaveAsync(
                issueId,
                states[issueId],
                [new IssuePriorityChanged("p2", "p1")]);

            var persisted = Assert.Single(await eventStore.ListIssueEventsAsync(issueId));
            Assert.Equal(expectedEpicId, persisted.Envelope.Extensions[EventCatalog.Lineage.EpicId]);
        }
    }

    [Fact]
    public async Task Migration_ReplacesStaleJsonAffiliationWithCurrentMembershipAndPreservesHistoricalExtensions()
    {
        await using var database = CreateDatabase("20260714120000_AddProjectEventReadKeys");
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();
        var liveIssue = NewIssue("issue_live", 1);
        var liveState = IssueStore.Deserialize(liveIssue.State)!;
        liveState.SetEpicId("epic_live", FirstLinkTime.UtcDateTime);
        liveIssue.State = IssueStore.Serialize(liveState);
        const string historicalExtensions = "{\"issueno\":\"1\",\"custom\":\"preserve\"}";

        await SeedIssueAsync(context, liveIssue);
        context.EpicIssues.Add(NewLink("epic_backfill", "issue_live", 1, FirstLinkTime));
        context.IssueEvents.Add(new IssueEventRow
        {
            Id = 1,
            Source = IssueEventPersistence.IssueSource("issue_live"),
            EventId = "evt_historical",
            Type = EventCatalog.ReverseDns.IssueCreated,
            Time = FirstLinkTime,
            SpecVersion = "1.0",
            DataContentType = "application/json",
            Data = JsonSerializer.SerializeToElement(new { }, CloudEvent.JsonOptions),
            ExtensionsJson = historicalExtensions,
        });
        await context.SaveChangesAsync();

        await migrator.MigrateAsync(AtomicSnapshotMigration);
        await migrator.MigrateAsync(AtomicSnapshotMigration);

        var persisted = await context.Issues.AsNoTracking().SingleAsync(row => row.IssueId == "issue_live");
        Assert.Equal("epic_backfill", IssueStore.Deserialize(persisted.State)!.EpicId);
        Assert.Equal("epic_backfill", persisted.EpicId);
        var historical = await context.IssueEvents.AsNoTracking().SingleAsync(row => row.EventId == "evt_historical");
        Assert.Equal(historicalExtensions, historical.ExtensionsJson);
    }

    [Fact]
    public async Task Migration_CopiesIssueAndWorkflowEpicSnapshotsFromCurrentState()
    {
        await using var database = CreateDatabase("20260715000000_BackfillIssueEpicAffiliation");
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();
        var issue = NewIssue("issue_snapshot", 1);
        var issueState = IssueStore.Deserialize(issue.State)!;
        issueState.SetEpicId("epic_snapshot", FirstLinkTime.UtcDateTime);
        issue.State = IssueStore.Serialize(issueState);
        await SeedIssueAsync(context, issue);
        context.EpicIssues.Add(NewLink("epic_snapshot", issue.IssueId, 1, FirstLinkTime));

        var workflow = new Mohist.Server.Workflow.Domain.Run.WorkflowRun
        {
            Id = "wr_snapshot",
            Metadata = new Mohist.Server.Workflow.Domain.Run.WorkflowRunMetadata(
                Name: null,
                CreatedAt: FirstLinkTime,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = "project_1",
                    ["issueId"] = issue.IssueId,
                    ["epicId"] = "epic_snapshot",
                }),
            Stages = [],
        };
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "ETag")
            VALUES ({workflow.Id}, {JSON.Serialize(workflow)}, 1)
            """);
        await context.SaveChangesAsync();

        await migrator.MigrateAsync(AtomicSnapshotMigration);
        context.ChangeTracker.Clear();

        var persistedIssue = await context.Issues.SingleAsync(row => row.IssueId == issue.IssueId);
        var persistedWorkflow = await context.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == workflow.Id);
        Assert.Equal("epic_snapshot", persistedIssue.EpicId);
        Assert.Equal("epic_snapshot", persistedWorkflow.EpicId);
    }

    [Fact]
    public async Task Migration_CopiesLinkedIssueSnapshotForLegacyWorkflowWithoutEpicAnnotation()
    {
        await using var database = CreateDatabase("20260715000000_BackfillIssueEpicAffiliation");
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();
        var issue = NewIssue("issue_legacy_workflow", 1);
        var issueState = IssueStore.Deserialize(issue.State)!;
        issueState.SetEpicId("epic_linked", FirstLinkTime.UtcDateTime);
        issue.State = IssueStore.Serialize(issueState);
        await SeedIssueAsync(context, issue);
        context.EpicIssues.Add(NewLink("epic_linked", issue.IssueId, 1, FirstLinkTime));

        var workflow = new Mohist.Server.Workflow.Domain.Run.WorkflowRun
        {
            Id = "wr_legacy_workflow",
            Metadata = new Mohist.Server.Workflow.Domain.Run.WorkflowRunMetadata(
                Name: null,
                CreatedAt: FirstLinkTime,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = "project_1",
                    ["issueId"] = issue.IssueId,
                }),
            Stages = [],
        };
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "ETag")
            VALUES ({workflow.Id}, {JSON.Serialize(workflow)}, 1)
            """);
        await context.SaveChangesAsync();

        await migrator.MigrateAsync(AtomicSnapshotMigration);
        context.ChangeTracker.Clear();

        var persistedWorkflow = await context.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == workflow.Id);
        Assert.Equal("epic_linked", persistedWorkflow.EpicId);
    }

    [Fact]
    public async Task Migration_PrefersCurrentIssueSnapshotOverStaleWorkflowAnnotation()
    {
        await using var database = CreateDatabase("20260715000000_BackfillIssueEpicAffiliation");
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();
        var linked = NewIssue("issue_current_link", 1);
        var linkedState = IssueStore.Deserialize(linked.State)!;
        linkedState.SetEpicId("epic_current", FirstLinkTime.UtcDateTime);
        linked.State = IssueStore.Serialize(linkedState);
        var unlinked = NewIssue("issue_current_none", 2);
        await SeedIssueAsync(context, linked);
        await SeedIssueAsync(context, unlinked);
        context.EpicIssues.Add(NewLink("epic_current", linked.IssueId, 1, FirstLinkTime));

        await SeedWorkflowAsync(context, "wr_current_link", linked.IssueId, "epic_stale");
        await SeedWorkflowAsync(context, "wr_current_none", unlinked.IssueId, "epic_stale");
        await SeedWorkflowAsync(context, "wr_unmatched_issue", "issue_missing", "epic_annotation");
        await context.SaveChangesAsync();

        await migrator.MigrateAsync(AtomicSnapshotMigration);
        context.ChangeTracker.Clear();

        Assert.Equal("epic_current", (await context.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == "wr_current_link")).EpicId);
        Assert.Null((await context.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == "wr_current_none")).EpicId);
        Assert.Equal("epic_annotation", (await context.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == "wr_unmatched_issue")).EpicId);

        var querier = new WorkflowRunQuerier(database.Factory);
        Assert.Equal("epic_current", (await querier.LoadAsync("wr_current_link"))!.Metadata.Annotations!["epicId"]);
        Assert.False((await querier.LoadAsync("wr_current_none"))!.Metadata.Annotations!.ContainsKey("epicId"));
    }

    private static Task SeedWorkflowAsync(MohistDbContext context, string workflowId, string issueId, string epicId)
    {
        var workflow = new Mohist.Server.Workflow.Domain.Run.WorkflowRun
        {
            Id = workflowId,
            Metadata = new Mohist.Server.Workflow.Domain.Run.WorkflowRunMetadata(
                Name: null,
                CreatedAt: FirstLinkTime,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = "project_1",
                    ["issueId"] = issueId,
                    ["epicId"] = epicId,
                }),
            Stages = [],
        };
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "WorkflowRuns" ("WorkflowRunId", "State", "ETag")
            VALUES ({workflow.Id}, {JSON.Serialize(workflow)}, 1)
            """);
    }

    private static IssueRow NewIssue(string issueId, int number)
    {
        var issue = new DomainIssue
        {
            Id = issueId,
            ProjectId = "project_1",
            Number = number,
            Title = issueId,
            Priority = "p2",
        };
        return new IssueRow
        {
            IssueId = issueId,
            ProjectId = "project_1",
            Number = number,
            State = IssueStore.Serialize(issue),
        };
    }

    private static Task SeedIssueAsync(MohistDbContext context, IssueRow issue) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Issues" ("IssueId", "State", "Risk")
            VALUES ({issue.IssueId}, {issue.State}, {issue.Risk})
            """);

    private static EpicIssueRow NewLink(string epicId, string issueId, int issueNumber, DateTimeOffset createdAt) => new()
    {
        EpicId = epicId,
        ProjectId = "project_1",
        IssueId = issueId,
        IssueNumber = issueNumber,
        CreatedAt = createdAt,
    };

    private static TestDatabase CreateDatabase(string migratedTo)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        MigratedSqliteTemplate.CopyTo(connection, migratedTo);
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
        private readonly DbContextOptions<MohistDbContext> _options;

        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }
}
