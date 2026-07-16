using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

public class EpicQuerierListAsyncSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_DoesNotInvokeIssueQuerier()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);
        await SeedIssueAsync(database, "proj_1", "issue_1", 1, IssueStatus.Done);
        await SeedLinkAsync(database, "epic_1", "issue_1", 1, "proj_1");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());

        var result = await querier.ListAsync("proj_1");

        var epic = Assert.Single(result);
        Assert.Equal(1, epic.Progress.DeliveredCount);
        Assert.Equal(1, epic.Progress.TotalIssueCount);
        Assert.True(epic.Progress.ReadyToMarkDone);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_IssuesSingleSelectRegardlessOfEpicCount()
    {
        var (database, commands) = CreateDatabaseWithCommandCounting();
        await using (database)
        {
            await SeedEpicAsync(database, "proj_1", "epic_1", 1);
            await SeedEpicAsync(database, "proj_1", "epic_2", 2);
            await SeedEpicAsync(database, "proj_1", "epic_3", 3);
            for (var i = 1; i <= 6; i++)
            {
                var status = i % 2 == 0 ? IssueStatus.Done : IssueStatus.Backlog;
                await SeedIssueAsync(database, "proj_1", $"issue_{i}", i, status);
                var epicId = i <= 2 ? "epic_1" : i <= 4 ? "epic_2" : "epic_3";
                await SeedLinkAsync(database, epicId, $"issue_{i}", i, "proj_1");
            }

            var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
            var result = await querier.ListAsync("proj_1");

            Assert.Equal(3, result.Count);
            var selectCommands = commands
                .Where(c => c.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var only = Assert.Single(selectCommands);
            Assert.DoesNotContain("\"WorkflowRuns\"", only);
            Assert.DoesNotContain("\"Comments\"", only);
            Assert.DoesNotContain("\"Attachments\"", only);
            Assert.Contains("\"Epics\"", only);
            Assert.Contains("\"EpicIssues\"", only);
            Assert.Contains("\"Issues\"", only);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_ProgressCountsDoneAndExcludesCancelled()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);
        await SeedIssueAsync(database, "proj_1", "issue_done", 1, IssueStatus.Done);
        await SeedIssueAsync(database, "proj_1", "issue_cancelled", 2, IssueStatus.Cancelled);
        await SeedIssueAsync(database, "proj_1", "issue_backlog", 3, IssueStatus.Backlog);
        await SeedLinkAsync(database, "epic_1", "issue_done", 1, "proj_1");
        await SeedLinkAsync(database, "epic_1", "issue_cancelled", 2, "proj_1");
        await SeedLinkAsync(database, "epic_1", "issue_backlog", 3, "proj_1");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
        var result = await querier.ListAsync("proj_1");

        var epic = Assert.Single(result);
        Assert.Equal(1, epic.Progress.DeliveredCount);
        Assert.Equal(3, epic.Progress.TotalIssueCount);
        // backlog is open → readiness false.
        Assert.False(epic.Progress.ReadyToMarkDone);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_MixedDoneAndCancelled_ReadyToMarkDone_DeliveredCountCountsOnlyDone()
    {
        // Epic #18: done + cancelled, all linked issues terminal, epic
        // is ready to Mark Done. deliveredCount counts only the done
        // issue.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);
        await SeedIssueAsync(database, "proj_1", "issue_done", 1, IssueStatus.Done);
        await SeedIssueAsync(database, "proj_1", "issue_cancelled", 2, IssueStatus.Cancelled);
        await SeedLinkAsync(database, "epic_1", "issue_done", 1, "proj_1");
        await SeedLinkAsync(database, "epic_1", "issue_cancelled", 2, "proj_1");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
        var result = await querier.ListAsync("proj_1");

        var epic = Assert.Single(result);
        Assert.True(epic.Progress.ReadyToMarkDone);
        Assert.Equal(1, epic.Progress.DeliveredCount);
        Assert.Equal(2, epic.Progress.TotalIssueCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_CancelledOnlyRemaining_ReadyToMarkDone()
    {
        // No delivered, but every linked issue is terminal; readyToMarkDone
        // is true (the new terminal/open rule).
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);
        await SeedIssueAsync(database, "proj_1", "issue_cancelled_a", 1, IssueStatus.Cancelled);
        await SeedIssueAsync(database, "proj_1", "issue_cancelled_b", 2, IssueStatus.Cancelled);
        await SeedLinkAsync(database, "epic_1", "issue_cancelled_a", 1, "proj_1");
        await SeedLinkAsync(database, "epic_1", "issue_cancelled_b", 2, "proj_1");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
        var result = await querier.ListAsync("proj_1");

        var epic = Assert.Single(result);
        Assert.True(epic.Progress.ReadyToMarkDone);
        Assert.Equal(0, epic.Progress.DeliveredCount);
        Assert.Equal(2, epic.Progress.TotalIssueCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_EmptyEpic_YieldsZeroCounts()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
        var result = await querier.ListAsync("proj_1");

        var epic = Assert.Single(result);
        Assert.Equal(0, epic.Progress.DeliveredCount);
        Assert.Equal(0, epic.Progress.TotalIssueCount);
        Assert.False(epic.Progress.ReadyToMarkDone);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_AllArchivedLinkedIssues_YieldsZeroCounts()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);
        await SeedIssueAsync(database, "proj_1", "issue_1", 1, IssueStatus.Done, archivedAt: TestTime.UtcDateTime);
        await SeedLinkAsync(database, "epic_1", "issue_1", 1, "proj_1");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
        var result = await querier.ListAsync("proj_1");

        var epic = Assert.Single(result);
        Assert.Equal(0, epic.Progress.DeliveredCount);
        Assert.Equal(0, epic.Progress.TotalIssueCount);
        Assert.False(epic.Progress.ReadyToMarkDone);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_NextIssue_SelectsHighestPriorityStartable()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);
        await SeedIssueAsync(database, "proj_1", "low_ready", 1, IssueStatus.Backlog, priority: "p2", isDraft: false);
        await SeedIssueAsync(database, "proj_1", "high_ready", 2, IssueStatus.Backlog, priority: "p0", isDraft: false);
        await SeedLinkAsync(database, "epic_1", "low_ready", 1, "proj_1");
        await SeedLinkAsync(database, "epic_1", "high_ready", 2, "proj_1");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
        var result = await querier.ListAsync("proj_1");

        var epic = Assert.Single(result);
        Assert.NotNull(epic.Progress.NextIssue);
        Assert.Equal(2, epic.Progress.NextIssue!.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_NextIssue_SerialSlotOccupiedByInProgress()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);
        await SeedIssueAsync(database, "proj_1", "in_progress", 3, IssueStatus.InProgress, priority: "p0", isDraft: false);
        await SeedIssueAsync(database, "proj_1", "ready", 1, IssueStatus.Backlog, priority: "p0", isDraft: false);
        await SeedLinkAsync(database, "epic_1", "in_progress", 3, "proj_1");
        await SeedLinkAsync(database, "epic_1", "ready", 1, "proj_1");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
        var result = await querier.ListAsync("proj_1");

        var epic = Assert.Single(result);
        Assert.Null(epic.Progress.NextIssue);
        Assert.Equal("Waiting for #3 to complete", epic.Progress.NextIssueReason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_UnmetPrerequisiteBlocksCanStart()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);
        await SeedIssueAsync(database, "proj_1", "prereq", 1, IssueStatus.Backlog, isDraft: false);
        await SeedIssueAsync(database, "proj_1", "dependent", 2, IssueStatus.Backlog, isDraft: false, prerequisiteNumbers: [1]);
        await SeedLinkAsync(database, "epic_1", "prereq", 1, "proj_1");
        await SeedLinkAsync(database, "epic_1", "dependent", 2, "proj_1");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
        var result = await querier.ListAsync("proj_1");

        var epic = Assert.Single(result);
        Assert.NotNull(epic.Progress.NextIssue);
        Assert.Equal(1, epic.Progress.NextIssue!.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_DraftIssueIsNotStartable()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);
        await SeedIssueAsync(database, "proj_1", "draft", 1, IssueStatus.Backlog, isDraft: true);
        await SeedIssueAsync(database, "proj_1", "ready", 2, IssueStatus.Backlog, isDraft: false);
        await SeedLinkAsync(database, "epic_1", "draft", 1, "proj_1");
        await SeedLinkAsync(database, "epic_1", "ready", 2, "proj_1");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
        var result = await querier.ListAsync("proj_1");

        var epic = Assert.Single(result);
        Assert.NotNull(epic.Progress.NextIssue);
        Assert.Equal(2, epic.Progress.NextIssue!.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_BlockedInProgress_ReportedAsActive()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, "proj_1", "epic_1", 1);
        await SeedIssueAsync(database, "proj_1", "blocked_in_progress", 1, IssueStatus.InProgress, isDraft: false);
        await SeedLinkAsync(database, "epic_1", "blocked_in_progress", 1, "proj_1");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
        var result = await querier.ListAsync("proj_1");

        var epic = Assert.Single(result);
        Assert.Single(epic.Progress.ActiveIssues);
        Assert.Empty(epic.Progress.BlockedIssues);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListAsync_OrdersByPriorityThenUpdatedAt()
    {
        await using var database = CreateDatabase();
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        await SeedEpicAsync(database, "proj_1", "epic_p2_old", 1, priority: "p2", updatedAt: now);
        await SeedEpicAsync(database, "proj_1", "epic_p2_new", 2, priority: "p2", updatedAt: now.AddMinutes(1));
        await SeedEpicAsync(database, "proj_1", "epic_p0", 3, priority: "p0");

        var querier = new EpicQuerier(database.Factory, new ThrowingIssueQuerier());
        var result = await querier.ListAsync("proj_1");

        Assert.Equal(3, result.Count);
        Assert.Equal("epic_p0", result[0].Id);
        Assert.Equal("epic_p2_new", result[1].Id);
        Assert.Equal("epic_p2_old", result[2].Id);
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        MigratedSqliteTemplate.CopyTo(connection);
        return new TestDatabase(connection, factory);
    }

    private static (TestDatabase Database, List<string> Commands) CreateDatabaseWithCommandCounting()
    {
        var commands = new List<string>();
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        MigratedSqliteTemplate.CopyTo(connection);

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .EnableSensitiveDataLogging()
            .LogTo(
                message => commands.Add(message),
                new[] { RelationalEventId.CommandExecuted },
                LogLevel.Information)
            .Options;
        var factory = new TestDbContextFactory(options);
        return (new TestDatabase(connection, factory), commands);
    }

    private static async Task SeedEpicAsync(TestDatabase database, string projectId, string epicId, int number, string priority = "p2", DateTimeOffset? updatedAt = null)
    {
        var now = updatedAt ?? TestTime.UtcNow;
        await using var db = database.CreateDbContext();
        db.Epics.Add(new EpicRow
        {
            Id = epicId,
            ProjectId = projectId,
            Number = number,
            Title = $"Epic {number}",
            Description = "",
            Priority = priority,
            Status = "idle",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedIssueAsync(
        TestDatabase database,
        string projectId,
        string issueId,
        int number,
        IssueStatus status,
        string priority = "p2",
        bool isDraft = false,
        int[]? prerequisiteNumbers = null,
        DateTime? archivedAt = null)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = number,
            Title = $"Issue {number}",
            Status = status,
            Priority = priority,
            IsDraft = isDraft,
            PrerequisiteNumbers = prerequisiteNumbers ?? [],
            ArchivedAt = archivedAt,
            CreatedAt = TestTime.UtcDateTime,
            UpdatedAt = TestTime.UtcDateTime,
        };
        var json = IssueStore.Serialize(issue);
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            State = json,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedLinkAsync(TestDatabase database, string epicId, string issueId, int issueNumber, string projectId)
    {
        await using var db = database.CreateDbContext();
        db.EpicIssues.Add(new EpicIssueRow
        {
            EpicId = epicId,
            ProjectId = projectId,
            IssueId = issueId,
            IssueNumber = issueNumber,
            CreatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
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
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    private sealed class ThrowingIssueQuerier : IssueQuerier
    {
        public ThrowingIssueQuerier()
            : base(null!, null!, null!, null!, null!, null!)
        {
        }

        public new Task<List<IssueReadModel>> ListAsync(
            string projectId,
            ProjectInfo? project = null,
            string? stage = null,
            string? label = null,
            string? priority = null,
            bool? archived = null,
            bool? all = null) =>
            throw new InvalidOperationException("IssueQuerier.ListAsync should not be invoked on the epic list path.");
    }
}
