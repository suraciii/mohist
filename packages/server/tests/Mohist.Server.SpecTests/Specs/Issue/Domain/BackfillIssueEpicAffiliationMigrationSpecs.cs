using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Domain;

public class BackfillIssueEpicAffiliationMigrationSpecs
{
    private static readonly DateTimeOffset FirstLinkTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Migration_BackfillsActiveAndTerminalMembershipsWithDeterministicFallback()
    {
        await using var database = CreateDatabase("20260714120000_AddProjectEventReadKeys");
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();

        context.Issues.AddRange(
            NewIssue("issue_active", 1),
            NewIssue("issue_done", 2),
            NewIssue("issue_closed", 3),
            NewIssue("issue_tied", 4));
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

        await migrator.MigrateAsync("20260715000000_BackfillIssueEpicAffiliation");

        var states = await context.Issues.AsNoTracking()
            .OrderBy(row => row.IssueId)
            .ToDictionaryAsync(row => row.IssueId, row => IssueStore.Deserialize(row.State)!);
        Assert.Equal("epic_active", states["issue_active"].EpicId);
        Assert.Equal("epic_done", states["issue_done"].EpicId);
        Assert.Equal("epic_closed", states["issue_closed"].EpicId);
        Assert.Equal("epic_a", states["issue_tied"].EpicId);

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
    public async Task Migration_IsIdempotentAndPreservesLiveAffiliationAndHistoricalExtensions()
    {
        await using var database = CreateDatabase("20260714120000_AddProjectEventReadKeys");
        await using var context = database.CreateDbContext();
        var migrator = context.GetService<IMigrator>();
        var liveIssue = NewIssue("issue_live", 1);
        var liveState = IssueStore.Deserialize(liveIssue.State)!;
        liveState.SetEpicId("epic_live", FirstLinkTime.UtcDateTime);
        liveIssue.State = IssueStore.Serialize(liveState);
        const string historicalExtensions = "{\"issueno\":\"1\",\"custom\":\"preserve\"}";

        context.Issues.Add(liveIssue);
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

        await migrator.MigrateAsync("20260715000000_BackfillIssueEpicAffiliation");
        await migrator.MigrateAsync("20260715000000_BackfillIssueEpicAffiliation");

        var persisted = await context.Issues.AsNoTracking().SingleAsync(row => row.IssueId == "issue_live");
        Assert.Equal("epic_live", IssueStore.Deserialize(persisted.State)!.EpicId);
        var historical = await context.IssueEvents.AsNoTracking().SingleAsync(row => row.EventId == "evt_historical");
        Assert.Equal(historicalExtensions, historical.ExtensionsJson);
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
