using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Inbox;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Inbox;

public class InboxStoreSpecs
{
    [Fact]
    public async Task InsertAsync_GeneratesIdAndPersistsRow()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);

        var result = await store.InsertAsync(new InboxItemDraft(
            ProjectId: "proj_a",
            IssueNumber: 42,
            IssueTitle: "Hello",
            NotificationKind: NotificationKinds.WorkflowFailed,
            SourceEventSource: "/mohist/issues/issue_42",
            SourceEventId: "evt-1"));

        Assert.False(result.AlreadyExisted);
        Assert.False(string.IsNullOrEmpty(result.Id));
        Assert.StartsWith("inb_", result.Id);

        await using var db = database.CreateDbContext();
        var row = Assert.Single(db.InboxItems);
        Assert.Equal(result.Id, row.Id);
        Assert.Equal("proj_a", row.ProjectId);
        Assert.Equal(42, row.IssueNumber);
        Assert.Equal("Hello", row.IssueTitle);
        Assert.Equal(NotificationKinds.WorkflowFailed, row.NotificationKind);
        Assert.Equal("/mohist/issues/issue_42", row.SourceEventSource);
        Assert.Equal("evt-1", row.SourceEventId);
        Assert.Null(row.ReadAt);
        Assert.Null(row.ArchivedAt);
    }

    [Fact]
    public async Task InsertAsync_RepeatedSourceEventId_IsIdempotent()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);

        var first = await store.InsertAsync(new InboxItemDraft(
            ProjectId: "proj_a",
            IssueNumber: 42,
            IssueTitle: "Hello",
            NotificationKind: NotificationKinds.WorkflowFailed,
            SourceEventSource: "/mohist/issues/issue_42",
            SourceEventId: "evt-dup"));

        var second = await store.InsertAsync(new InboxItemDraft(
            ProjectId: "proj_a",
            IssueNumber: 42,
            IssueTitle: "Hello (replayed)",
            NotificationKind: NotificationKinds.WorkflowFailed,
            SourceEventSource: "/mohist/issues/issue_42",
            SourceEventId: "evt-dup"));

        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(first.Id, second.Id);

        await using var db = database.CreateDbContext();
        var row = Assert.Single(db.InboxItems);
        Assert.Equal(first.Id, row.Id);
        Assert.Equal("Hello", row.IssueTitle);
    }

    [Fact]
    public async Task InsertAsync_SameSourceEventIdAcrossDifferentSources_CreatesDistinctItems()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);

        var first = await store.InsertAsync(new InboxItemDraft(
            ProjectId: "proj_a",
            IssueNumber: 42,
            IssueTitle: "Hello",
            NotificationKind: NotificationKinds.WorkflowFailed,
            SourceEventSource: "/mohist/issues/issue_42",
            SourceEventId: "evt-cross-project"));

        var second = await store.InsertAsync(new InboxItemDraft(
            ProjectId: "proj_b",
            IssueNumber: 99,
            IssueTitle: "Other",
            NotificationKind: NotificationKinds.ApprovalRequested,
            SourceEventSource: "/mohist/issues/issue_99",
            SourceEventId: "evt-cross-project"));

        Assert.False(first.AlreadyExisted);
        Assert.False(second.AlreadyExisted);
        Assert.NotEqual(first.Id, second.Id);

        await using var db = database.CreateDbContext();
        Assert.Equal(2, db.InboxItems.Count());
    }

    [Fact]
    public async Task InsertAsync_InvalidNotificationKind_ThrowsAndDoesNotPersist()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);

        await Assert.ThrowsAsync<ArgumentException>(() => store.InsertAsync(new InboxItemDraft(
            ProjectId: "proj_a",
            IssueNumber: 42,
            IssueTitle: "Hello",
            NotificationKind: "workflow.paused",
            SourceEventSource: "/mohist/issues/issue_42",
            SourceEventId: "evt-invalid")));

        await using var db = database.CreateDbContext();
        Assert.Empty(db.InboxItems);
    }

    [Fact]
    public async Task MarkReadAsync_SetsReadAtOnMatchingItem()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);
        var result = await store.InsertAsync(Draft("proj_a", "issue_1", 1, "evt-1"));
        await store.InsertAsync(Draft("proj_a", "issue_2", 2, "evt-2"));

        var affected = await store.MarkReadAsync("proj_a", result.Id);

        Assert.Equal(1, affected);
        await using var db = database.CreateDbContext();
        var rows = db.InboxItems.OrderBy(r => r.IssueNumber).ToList();
        Assert.NotNull(rows[0].ReadAt);
        Assert.Null(rows[1].ReadAt);
    }

    [Fact]
    public async Task MarkReadAsync_DoesNotMatchItemInOtherProject()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);
        var inA = await store.InsertAsync(Draft("proj_a", "issue_1", 1, "evt-a-1"));
        var inB = await store.InsertAsync(Draft("proj_b", "issue_1", 1, "evt-b-1"));

        // Marking the project-A item from the project-B context must
        // affect zero rows — callers translate this to 404.
        var affected = await store.MarkReadAsync("proj_b", inA.Id);

        Assert.Equal(0, affected);
        await using var db = database.CreateDbContext();
        Assert.Null(db.InboxItems.Single(r => r.Id == inA.Id).ReadAt);
        Assert.Null(db.InboxItems.Single(r => r.Id == inB.Id).ReadAt);
    }

    [Fact]
    public async Task MarkReadAsync_DoesNotTouchArchivedItems()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);
        var archived = await store.InsertAsync(Draft("proj_a", "issue_1", 1, "evt-arch"));
        await store.ArchiveAsync("proj_a", archived.Id);

        var affected = await store.MarkReadAsync("proj_a", archived.Id);

        Assert.Equal(0, affected);
    }

    [Fact]
    public async Task MarkAllReadAsync_OnlyTouchesTargetProject()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);
        await store.InsertAsync(Draft("proj_a", "issue_1", 1, "evt-a-1"));
        await store.InsertAsync(Draft("proj_a", "issue_2", 2, "evt-a-2"));
        await store.InsertAsync(Draft("proj_b", "issue_1", 1, "evt-b-1"));

        var affected = await store.MarkAllReadAsync("proj_a");

        Assert.Equal(2, affected);
        await using var db = database.CreateDbContext();
        var aRows = db.InboxItems.Where(r => r.ProjectId == "proj_a").ToList();
        var bRows = db.InboxItems.Where(r => r.ProjectId == "proj_b").ToList();
        Assert.All(aRows, r => Assert.NotNull(r.ReadAt));
        Assert.All(bRows, r => Assert.Null(r.ReadAt));
    }

    [Fact]
    public async Task MarkAllReadAsync_SkipsAlreadyReadItems()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);
        var already = await store.InsertAsync(Draft("proj_a", "issue_1", 1, "evt-1"));
        await store.MarkReadAsync("proj_a", already.Id);
        await store.InsertAsync(Draft("proj_a", "issue_2", 2, "evt-2"));

        var affected = await store.MarkAllReadAsync("proj_a");

        // Only the second item transitions to read in this call.
        Assert.Equal(1, affected);
    }

    [Fact]
    public async Task MarkAllReadAsync_SkipsArchivedItems()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);
        await store.InsertAsync(Draft("proj_a", "issue_1", 1, "evt-1"));
        var archived = await store.InsertAsync(Draft("proj_a", "issue_2", 2, "evt-2"));
        await store.ArchiveAsync("proj_a", archived.Id);

        var affected = await store.MarkAllReadAsync("proj_a");

        Assert.Equal(1, affected);
        await using var db = database.CreateDbContext();
        Assert.Null(db.InboxItems.Single(r => r.Id == archived.Id).ReadAt);
    }

    [Fact]
    public async Task ArchiveAsync_SetsArchivedAt()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);
        var first = await store.InsertAsync(Draft("proj_a", "issue_1", 1, "evt-1"));
        await store.InsertAsync(Draft("proj_a", "issue_2", 2, "evt-2"));

        var affected = await store.ArchiveAsync("proj_a", first.Id);

        Assert.Equal(1, affected);
        await using var db = database.CreateDbContext();
        var rows = db.InboxItems.OrderBy(r => r.IssueNumber).ToList();
        Assert.NotNull(rows[0].ArchivedAt);
        Assert.Null(rows[1].ArchivedAt);
    }

    [Fact]
    public async Task ArchiveAsync_DoesNotMatchItemInOtherProject()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);
        var inA = await store.InsertAsync(Draft("proj_a", "issue_1", 1, "evt-a-1"));
        await store.InsertAsync(Draft("proj_b", "issue_1", 1, "evt-b-1"));

        var affected = await store.ArchiveAsync("proj_b", inA.Id);

        Assert.Equal(0, affected);
        await using var db = database.CreateDbContext();
        Assert.Null(db.InboxItems.Single(r => r.Id == inA.Id).ArchivedAt);
    }

    [Fact]
    public async Task ArchiveAsync_IsIdempotent()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);
        var item = await store.InsertAsync(Draft("proj_a", "issue_1", 1, "evt-1"));

        var first = await store.ArchiveAsync("proj_a", item.Id);
        var second = await store.ArchiveAsync("proj_a", item.Id);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task MarkReadAsync_AfterArchive_DoesNothing()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(database.Factory);
        var item = await store.InsertAsync(Draft("proj_a", "issue_1", 1, "evt-1"));
        await store.ArchiveAsync("proj_a", item.Id);

        var affected = await store.MarkReadAsync("proj_a", item.Id);

        Assert.Equal(0, affected);
    }

    private static InboxItemDraft Draft(string projectId, string issueId, int number, string sourceEventId) =>
        new(
            ProjectId: projectId,
            IssueNumber: number,
            IssueTitle: $"Issue {number}",
            NotificationKind: NotificationKinds.WorkflowFailed,
            SourceEventSource: $"/mohist/issues/{issueId}",
            SourceEventId: sourceEventId);

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
}
