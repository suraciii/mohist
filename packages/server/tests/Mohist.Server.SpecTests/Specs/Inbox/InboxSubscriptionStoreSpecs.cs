using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Inbox;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Inbox;

public class InboxSubscriptionStoreSpecs
{
    private static readonly DateTimeOffset StartTime = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetAsync_NoStoredRow_ReturnsAllEnabledDefault()
    {
        await using var database = CreateDatabase();
        var store = NewStore(database);

        var state = await store.GetAsync("proj_a");

        Assert.True(state.WorkflowFailedEnabled);
        Assert.True(state.ApprovalRequestedEnabled);
        Assert.True(state.IssueStartedEnabled);
        Assert.True(state.IssueCompletedEnabled);
    }

    [Fact]
    public async Task SetAsync_FirstWrite_PersistsNewRow()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);

        await store.SetAsync("proj_a", new InboxSubscriptionState(
            WorkflowFailedEnabled: false,
            ApprovalRequestedEnabled: true,
            IssueStartedEnabled: false,
            IssueCompletedEnabled: true));

        await using var db = database.CreateContext();
        var row = Assert.Single(db.InboxSubscriptions);
        Assert.Equal("proj_a", row.ProjectId);
        Assert.False(row.WorkflowFailedEnabled);
        Assert.True(row.ApprovalRequestedEnabled);
        Assert.False(row.IssueStartedEnabled);
        Assert.True(row.IssueCompletedEnabled);
    }

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsPersistedToggleStates()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);

        await store.SetAsync("proj_a", new InboxSubscriptionState(
            WorkflowFailedEnabled: false,
            ApprovalRequestedEnabled: false,
            IssueStartedEnabled: true,
            IssueCompletedEnabled: true));

        var state = await store.GetAsync("proj_a");

        Assert.False(state.WorkflowFailedEnabled);
        Assert.False(state.ApprovalRequestedEnabled);
        Assert.True(state.IssueStartedEnabled);
        Assert.True(state.IssueCompletedEnabled);
    }

    [Fact]
    public async Task SetAsync_SecondWrite_MutatesExistingRowInPlace()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);

        await store.SetAsync("proj_a", new InboxSubscriptionState(
            WorkflowFailedEnabled: false,
            ApprovalRequestedEnabled: false,
            IssueStartedEnabled: false,
            IssueCompletedEnabled: false));

        await store.SetAsync("proj_a", new InboxSubscriptionState(
            WorkflowFailedEnabled: true,
            ApprovalRequestedEnabled: true,
            IssueStartedEnabled: false,
            IssueCompletedEnabled: true));

        await using var db = database.CreateContext();
        Assert.Single(db.InboxSubscriptions);
        var row = db.InboxSubscriptions.Single();
        Assert.True(row.WorkflowFailedEnabled);
        Assert.True(row.ApprovalRequestedEnabled);
        Assert.False(row.IssueStartedEnabled);
        Assert.True(row.IssueCompletedEnabled);
    }

    [Fact]
    public async Task SetAsync_SecondWrite_UpdatesUpdatedAt()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var timeProvider = new FakeTimeProvider(StartTime);
        var store = NewStore(database, timeProvider);

        await store.SetAsync("proj_a", new InboxSubscriptionState());
        var firstUpdatedAt = (await ReadUpdatedAtAsync(database, "proj_a")).Ticks;

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await store.SetAsync("proj_a", new InboxSubscriptionState(WorkflowFailedEnabled: false));
        var secondUpdatedAt = (await ReadUpdatedAtAsync(database, "proj_a")).Ticks;

        Assert.True(secondUpdatedAt > firstUpdatedAt, "UpdatedAt should advance on second write");
    }

    [Fact]
    public async Task GetAsync_AfterDisablingAllKinds_ReturnsAllDisabled()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);

        await store.SetAsync("proj_a", new InboxSubscriptionState(
            WorkflowFailedEnabled: false,
            ApprovalRequestedEnabled: false,
            IssueStartedEnabled: false,
            IssueCompletedEnabled: false));

        var state = await store.GetAsync("proj_a");

        Assert.False(state.WorkflowFailedEnabled);
        Assert.False(state.ApprovalRequestedEnabled);
        Assert.False(state.IssueStartedEnabled);
        Assert.False(state.IssueCompletedEnabled);
    }

    [Fact]
    public async Task GetAsync_ProjectIsolation_ReturnsDefaultForOtherProject()
    {
        await using var database = CreateDatabase();
        await SeedProjectAsync(database, "proj_a");
        var store = NewStore(database);

        await store.SetAsync("proj_a", new InboxSubscriptionState(WorkflowFailedEnabled: false));

        var stateB = await store.GetAsync("proj_b");
        Assert.True(stateB.WorkflowFailedEnabled); // proj_b has no row → all-enabled
    }

    [Fact]
    public async Task SetAsync_MissingProject_FailsForeignKey()
    {
        await using var database = CreateDatabase();
        var store = NewStore(database);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            store.SetAsync("proj_missing", new InboxSubscriptionState()));
    }

    private static async Task<DateTimeOffset> ReadUpdatedAtAsync(TestSqliteDatabase database, string projectId)
    {
        await using var db = database.CreateContext();
        var row = await db.InboxSubscriptions.AsNoTracking()
            .FirstAsync(r => r.ProjectId == projectId);
        return row.UpdatedAt;
    }

    private static InboxSubscriptionStore NewStore(TestSqliteDatabase database, FakeTimeProvider? timeProvider = null) =>
        new(new TestDbContextFactory(database.Options), timeProvider ?? new FakeTimeProvider(StartTime));

    private static async Task SeedProjectAsync(TestSqliteDatabase database, string projectId)
    {
        await using var db = database.CreateContext();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId.Replace('_', '-'),
            RepositoriesJson = """[{"name":"test-repo","gitUrl":"git@example.com:test-repo.git","baseBranch":"main","isDefault":true}]""",
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static TestSqliteDatabase CreateDatabase() => TestSqliteDatabase.CreateMigrated();
}
