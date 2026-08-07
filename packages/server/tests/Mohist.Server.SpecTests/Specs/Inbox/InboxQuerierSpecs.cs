using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Inbox;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Inbox;

/// <summary>
/// Calculation specs for the project inbox read/mutate path behind
/// <c>/api/projects/&#123;projectRef&#125;/inbox</c>: the
/// <c>InboxQuerier</c> list (most-recent-first, archived excluded, empty
/// project) and the <c>InboxStore</c> mark-read / mark-all-read / archive
/// mutations. All run against MohistDbFixture without an HTTP round-trip.
/// The route contract (404 unknown item / unknown project) stays in
/// <c>InboxApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class InboxQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public InboxQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private InboxQuerier CreateQuerier() => _fixture.Services.GetRequiredService<InboxQuerier>();
    private InboxStore CreateStore() => _fixture.Services.GetRequiredService<InboxStore>();

    private static async Task<string> SeedAsync(InboxStore store, string projectId, int issueNumber, string title, string kind, DateTimeOffset createdAt, string sourceEventId)
    {
        var result = await store.InsertAsync(new InboxItemDraft(
            ProjectId: projectId,
            IssueNumber: issueNumber,
            IssueTitle: title,
            NotificationKind: kind,
            SourceEventSource: $"/mohist/projects/{projectId}/issues/{issueNumber}",
            SourceEventId: sourceEventId,
            CreatedAt: createdAt));
        return result.Id;
    }

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListAsync_EmptyProject_ReturnsEmpty()
    {
        var querier = CreateQuerier();

        var items = await querier.ListAsync($"proj-{Guid.NewGuid():N}");

        Assert.Empty(items);
    }

    [Fact]
    public async Task ListAsync_ReturnsMostRecentFirstAndExcludesArchived()
    {
        var store = CreateStore();
        var querier = CreateQuerier();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var firstId = await SeedAsync(store, projectId, 1, "First", NotificationKinds.WorkflowFailed, T0, "evt-1");
        var secondId = await SeedAsync(store, projectId, 2, "Second", NotificationKinds.ApprovalRequested, T0.AddDays(1), "evt-2");
        var archivedId = await SeedAsync(store, projectId, 3, "Archived", NotificationKinds.IssueStarted, T0.AddDays(2), "evt-3");
        await store.ArchiveAsync(projectId, archivedId);

        var items = await querier.ListAsync(projectId);

        Assert.Equal(2, items.Count);
        Assert.Equal(secondId, items[0].Id);
        Assert.Equal(firstId, items[1].Id);
        Assert.All(items, i => Assert.Null(i.ArchivedAt));
    }

    [Fact]
    public async Task MarkReadAsync_SetsOneItemRead_LeavesOthersUnread()
    {
        var store = CreateStore();
        var querier = CreateQuerier();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var first = await SeedAsync(store, projectId, 1, "First", NotificationKinds.WorkflowFailed, T0, "evt-1");
        var second = await SeedAsync(store, projectId, 2, "Second", NotificationKinds.ApprovalRequested, T0, "evt-2");

        var affected = await store.MarkReadAsync(projectId, first);
        Assert.Equal(1, affected);

        var items = await querier.ListAsync(projectId);
        var firstItem = Assert.Single(items, i => i.Id == first);
        var secondItem = Assert.Single(items, i => i.Id == second);
        Assert.NotNull(firstItem.ReadAt);
        Assert.Null(secondItem.ReadAt);
    }

    [Fact]
    public async Task MarkAllReadAsync_MarksAllNonArchivedItems()
    {
        var store = CreateStore();
        var querier = CreateQuerier();
        var projectId = $"proj-{Guid.NewGuid():N}";

        await SeedAsync(store, projectId, 1, "First", NotificationKinds.WorkflowFailed, T0, "evt-1");
        await SeedAsync(store, projectId, 2, "Second", NotificationKinds.ApprovalRequested, T0, "evt-2");
        var archived = await SeedAsync(store, projectId, 3, "Archived", NotificationKinds.IssueStarted, T0, "evt-3");
        await store.ArchiveAsync(projectId, archived);

        var marked = await store.MarkAllReadAsync(projectId);
        Assert.Equal(2, marked);

        var items = await querier.ListAsync(projectId);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.NotNull(i.ReadAt));
    }

    [Fact]
    public async Task ArchiveAsync_ExcludesItemFromDefaultList()
    {
        var store = CreateStore();
        var querier = CreateQuerier();
        var projectId = $"proj-{Guid.NewGuid():N}";

        var first = await SeedAsync(store, projectId, 1, "First", NotificationKinds.WorkflowFailed, T0, "evt-1");
        await SeedAsync(store, projectId, 2, "Second", NotificationKinds.ApprovalRequested, T0, "evt-2");

        await store.ArchiveAsync(projectId, first);

        var items = await querier.ListAsync(projectId);
        var surviving = Assert.Single(items);
        Assert.Equal(2, surviving.IssueNumber);
        Assert.Null(surviving.ArchivedAt);
    }
}
