using Microsoft.EntityFrameworkCore;
using Mohist.Server.Inbox;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Inbox;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Inbox;

public class InboxQuerierSpecs
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListAsync_ReturnsNonArchivedItemsMostRecentFirst()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(new TestDbContextFactory(database.Options));
        await SeedAsync(store, "proj_a", "evt-1", FixedNow.AddMinutes(-30), 1, "Issue 1");
        await SeedAsync(store, "proj_a", "evt-2", FixedNow.AddMinutes(-10), 2, "Issue 2");
        await SeedAsync(store, "proj_a", "evt-3", FixedNow.AddMinutes(-20), 3, "Issue 3");
        var querier = new InboxQuerier(new TestDbContextFactory(database.Options));

        var items = await querier.ListAsync("proj_a");

        Assert.Equal(3, items.Count);
        // Most-recent-first: evt-2 (-10m), evt-3 (-20m), evt-1 (-30m)
        Assert.Equal("evt-2", items[0].SourceEventId);
        Assert.Equal("evt-3", items[1].SourceEventId);
        Assert.Equal("evt-1", items[2].SourceEventId);
        Assert.Equal(2, items[0].IssueNumber);
        Assert.Equal(3, items[1].IssueNumber);
        Assert.Equal(1, items[2].IssueNumber);
    }

    [Fact]
    public async Task ListAsync_ExcludesArchivedItems()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(new TestDbContextFactory(database.Options));
        var keep = await SeedAsync(store, "proj_a", "evt-keep", FixedNow, 1, "Keep me");
        var drop = await SeedAsync(store, "proj_a", "evt-drop", FixedNow, 2, "Drop me");
        await store.ArchiveAsync("proj_a", drop.Id);
        var querier = new InboxQuerier(new TestDbContextFactory(database.Options));

        var items = await querier.ListAsync("proj_a");

        var only = Assert.Single(items);
        Assert.Equal(keep.Id, only.Id);
        Assert.False(only.IsArchived);
    }

    [Fact]
    public async Task ListAsync_OnlyReturnsItemsInRequestedProject()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(new TestDbContextFactory(database.Options));
        await SeedAsync(store, "proj_a", "evt-a-1", FixedNow, 1, "A1");
        await SeedAsync(store, "proj_a", "evt-a-2", FixedNow, 2, "A2");
        await SeedAsync(store, "proj_b", "evt-b-1", FixedNow, 1, "B1");
        var querier = new InboxQuerier(new TestDbContextFactory(database.Options));

        var aItems = await querier.ListAsync("proj_a");
        var bItems = await querier.ListAsync("proj_b");

        Assert.Equal(2, aItems.Count);
        Assert.All(aItems, item => Assert.Equal("proj_a", item.ProjectId));
        Assert.Single(bItems);
        Assert.Equal("proj_b", bItems[0].ProjectId);
    }

    [Fact]
    public async Task ListAsync_ReturnsEmptyForUnknownProject()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(new TestDbContextFactory(database.Options));
        await SeedAsync(store, "proj_a", "evt-1", FixedNow, 1, "A1");
        var querier = new InboxQuerier(new TestDbContextFactory(database.Options));

        var items = await querier.ListAsync("proj_zzz");

        Assert.Empty(items);
    }

    [Fact]
    public async Task ListAsync_ReflectsReadStateFromStore()
    {
        await using var database = CreateDatabase();
        var store = new InboxStore(new TestDbContextFactory(database.Options));
        var read = await SeedAsync(store, "proj_a", "evt-read", FixedNow, 1, "Read");
        await store.MarkReadAsync("proj_a", read.Id);
        await SeedAsync(store, "proj_a", "evt-unread", FixedNow.AddMinutes(1), 2, "Unread");
        var querier = new InboxQuerier(new TestDbContextFactory(database.Options));

        var items = await querier.ListAsync("proj_a");

        Assert.Equal(2, items.Count);
        // most-recent-first: unread first
        Assert.False(items[0].IsRead);
        Assert.Null(items[0].ReadAt);
        Assert.True(items[1].IsRead);
        Assert.NotNull(items[1].ReadAt);
    }

    [Fact]
    public async Task ListAsync_CarriesStructuredFieldsForProductFacingText()
    {
        // The querier returns the structured fields the Web client
        // templates from. No pre-rendered text is stored.
        await using var database = CreateDatabase();
        var store = new InboxStore(new TestDbContextFactory(database.Options));
        await store.InsertAsync(new InboxItemDraft(
            ProjectId: "proj_a",
            IssueNumber: 42,
            IssueTitle: "Render me client-side",
            NotificationKind: NotificationKinds.ApprovalRequested,
            SourceEventSource: "/mohist/issues/issue_42",
            SourceEventId: "evt-1"));
        var querier = new InboxQuerier(new TestDbContextFactory(database.Options));

        var items = await querier.ListAsync("proj_a");

        var item = Assert.Single(items);
        Assert.Equal(NotificationKinds.ApprovalRequested, item.NotificationKind);
        Assert.Equal(42, item.IssueNumber);
        Assert.Equal("Render me client-side", item.IssueTitle);
    }

    [Fact]
    public async Task ListAsync_TieBreaksItemsWithIdenticalCreatedAtById()
    {
        // Two items in the same millisecond must come back in a
        // stable order. We seed both rows directly with the same
        // CreatedAt to exercise the secondary sort key.
        var same = FixedNow;
        await using var database = CreateDatabase();
        await using (var db = database.CreateContext())
        {
            db.InboxItems.Add(new InboxItemRow
            {
                Id = "inb_first",
                ProjectId = "proj_a",
                IssueNumber = 1,
                IssueTitle = "First",
                NotificationKind = NotificationKinds.WorkflowFailed,
                SourceEventSource = "/mohist/issues/issue_1",
                SourceEventId = "evt-1",
                CreatedAt = same,
            });
            db.InboxItems.Add(new InboxItemRow
            {
                Id = "inb_second",
                ProjectId = "proj_a",
                IssueNumber = 2,
                IssueTitle = "Second",
                NotificationKind = NotificationKinds.WorkflowFailed,
                SourceEventSource = "/mohist/issues/issue_2",
                SourceEventId = "evt-2",
                CreatedAt = same,
            });
            await db.SaveChangesAsync();
        }
        var querier = new InboxQuerier(new TestDbContextFactory(database.Options));

        var items = await querier.ListAsync("proj_a");

        Assert.Equal(2, items.Count);
        // Id is a Guid-derived `inb_...`; we just need a stable order
        // across runs — assert that the order matches the first/second
        // ordering captured by the inserted Ids.
        Assert.Equal(new[] { "inb_first", "inb_second" }, items.Select(i => i.Id).ToArray());
    }

    private static async Task<InboxInsertResult> SeedAsync(
        InboxStore store,
        string projectId,
        string sourceEventId,
        DateTimeOffset createdAt,
        int issueNumber,
        string issueTitle)
    {
        return await store.InsertAsync(new InboxItemDraft(
            ProjectId: projectId,
            IssueNumber: issueNumber,
            IssueTitle: issueTitle,
            NotificationKind: NotificationKinds.WorkflowFailed,
            SourceEventSource: $"/mohist/issues/issue_{issueNumber}",
            SourceEventId: sourceEventId,
            CreatedAt: createdAt));
    }

    private static TestSqliteDatabase CreateDatabase() => TestSqliteDatabase.CreateMigrated();
}
