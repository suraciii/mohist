using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Inbox;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Inbox;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

/// <summary>
/// Integration specs for the project-scoped inbox HTTP routes. Each spec
/// seeds inbox rows directly via <see cref="InboxStore"/> (the projection
/// handler is exercised separately by <c>InboxProjectionHandlerSpecs</c>)
/// and drives the routes through the test HTTP client.
/// </summary>
[Collection("IntegrationApi")]
public class InboxApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public InboxApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task List_EmptyProject_ReturnsEmptyArray()
    {
        var projectId = await CreateProjectAsync("inbox-empty");

        var items = await _client.GetDataAsync<JsonElement[]>(
            $"/api/projects/{projectId}/inbox");

        Assert.Empty(items);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task List_WithItems_ReturnsFieldsOrderedMostRecentFirstAndExcludesArchived()
    {
        var projectId = await CreateProjectAsync("inbox-list");

        var firstId = await SeedAsync(projectId, 1, "First",
            NotificationKinds.WorkflowFailed, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "evt-1");
        var secondId = await SeedAsync(projectId, 2, "Second",
            NotificationKinds.ApprovalRequested, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "evt-2");
        await SeedAsync(projectId, 3, "Archived",
            NotificationKinds.IssueStarted, new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero), "evt-3");
        await ArchiveDirectAsync(projectId, "evt-3");

        var items = await _client.GetDataAsync<JsonElement[]>(
            $"/api/projects/{projectId}/inbox");

        // Archived item is excluded; remaining two are ordered most-recent-first.
        Assert.Equal(2, items.Length);

        var approval = items[0];
        Assert.Equal(secondId, approval.GetProperty("itemId").GetString());
        Assert.Equal(NotificationKinds.ApprovalRequested, approval.GetProperty("notificationKind").GetString());
        Assert.Equal(2, approval.GetProperty("issueNumber").GetInt32());
        Assert.Equal("Second", approval.GetProperty("issueTitle").GetString());
        Assert.True(approval.GetProperty("createdAt").GetDateTimeOffset() > new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.False(approval.GetProperty("isRead").GetBoolean());
        Assert.False(approval.GetProperty("isArchived").GetBoolean());
        // readAt/archivedAt are nullable timestamps; the JSON serializer is
        // configured to omit null fields, so a fresh unread item carries
        // neither property. Use TryGetProperty to assert "not set".
        Assert.False(approval.TryGetProperty("readAt", out _));
        Assert.False(approval.TryGetProperty("archivedAt", out _));

        var failed = items[1];
        Assert.Equal(firstId, failed.GetProperty("itemId").GetString());
        Assert.Equal(NotificationKinds.WorkflowFailed, failed.GetProperty("notificationKind").GetString());
        Assert.Equal(1, failed.GetProperty("issueNumber").GetInt32());
        Assert.Equal("First", failed.GetProperty("issueTitle").GetString());
        Assert.False(failed.GetProperty("isRead").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task MarkRead_SetsItemRead_LeavesOthersUnchanged()
    {
        var projectId = await CreateProjectAsync("inbox-mark-one");
        var first = await SeedAsync(projectId, 1, "First",
            NotificationKinds.WorkflowFailed, TestTime.UtcNow, "evt-mr-1");
        var second = await SeedAsync(projectId, 2, "Second",
            NotificationKinds.ApprovalRequested, TestTime.UtcNow, "evt-mr-2");

        await _client.PostOkAsync($"/api/projects/{projectId}/inbox/{first}/read");

        var items = await _client.GetDataAsync<JsonElement[]>($"/api/projects/{projectId}/inbox");
        Assert.Equal(2, items.Length);

        var firstItem = items.Single(i => i.GetProperty("itemId").GetString() == first);
        var secondItem = items.Single(i => i.GetProperty("itemId").GetString() == second);

        Assert.True(firstItem.GetProperty("isRead").GetBoolean());
        Assert.True(firstItem.TryGetProperty("readAt", out var firstReadAt));
        Assert.NotEqual(JsonValueKind.Null, firstReadAt.ValueKind);

        Assert.False(secondItem.GetProperty("isRead").GetBoolean());
        Assert.False(secondItem.TryGetProperty("readAt", out _));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task MarkRead_RepeatedCall_StaysOk()
    {
        var projectId = await CreateProjectAsync("inbox-mark-idem");
        var itemId = await SeedAsync(projectId, 1, "First",
            NotificationKinds.WorkflowFailed, TestTime.UtcNow, "evt-mri-1");

        await _client.PostOkAsync($"/api/projects/{projectId}/inbox/{itemId}/read");
        // A repeated "mark read" must not 404 — the user clicked the
        // button twice. The item stays read.
        await _client.PostOkAsync($"/api/projects/{projectId}/inbox/{itemId}/read");

        var items = await _client.GetDataAsync<JsonElement[]>(
            $"/api/projects/{projectId}/inbox");
        var item = Assert.Single(items);
        Assert.Equal(itemId, item.GetProperty("itemId").GetString());
        Assert.True(item.GetProperty("isRead").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task MarkRead_CrossProjectItemId_Returns404()
    {
        var projectA = await CreateProjectAsync("inbox-cross-a");
        var projectB = await CreateProjectAsync("inbox-cross-b");

        var itemInA = await SeedAsync(projectA, 1, "First",
            NotificationKinds.WorkflowFailed, TestTime.UtcNow, "evt-xcr-1");

        using var response = await _client.PostAsync(
            $"/api/projects/{projectB}/inbox/{itemInA}/read",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task MarkRead_UnknownItemId_Returns404()
    {
        var projectId = await CreateProjectAsync("inbox-unknown");

        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/inbox/inb_does_not_exist/read",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task MarkAllRead_MarksAllNonArchivedItemsInProject()
    {
        var projectId = await CreateProjectAsync("inbox-read-all");
        await SeedAsync(projectId, 1, "First",
            NotificationKinds.WorkflowFailed, TestTime.UtcNow, "evt-ra-1");
        await SeedAsync(projectId, 2, "Second",
            NotificationKinds.ApprovalRequested, TestTime.UtcNow, "evt-ra-2");
        var archived = await SeedAsync(projectId, 3, "Archived",
            NotificationKinds.IssueStarted, TestTime.UtcNow, "evt-ra-3");
        await ArchiveAsync(projectId, archived);

        var result = await _client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/inbox/read-all");

        Assert.Equal(2, result.GetProperty("marked").GetInt32());

        var items = await _client.GetDataAsync<JsonElement[]>(
            $"/api/projects/{projectId}/inbox");

        Assert.Equal(2, items.Length);
        Assert.All(items, i => Assert.True(i.GetProperty("isRead").GetBoolean()));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task MarkAllRead_DoesNotTouchOtherProjectItems()
    {
        var projectA = await CreateProjectAsync("inbox-readall-a");
        var projectB = await CreateProjectAsync("inbox-readall-b");

        await SeedAsync(projectA, 1, "A1",
            NotificationKinds.WorkflowFailed, TestTime.UtcNow, "evt-ra-x-1");
        await SeedAsync(projectA, 2, "A2",
            NotificationKinds.ApprovalRequested, TestTime.UtcNow, "evt-ra-x-2");
        var itemInB = await SeedAsync(projectB, 1, "B1",
            NotificationKinds.IssueStarted, TestTime.UtcNow, "evt-ra-x-b-1");

        var result = await _client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectA}/inbox/read-all");

        Assert.Equal(2, result.GetProperty("marked").GetInt32());

        var itemsB = await _client.GetDataAsync<JsonElement[]>(
            $"/api/projects/{projectB}/inbox");
        var b = Assert.Single(itemsB);
        Assert.Equal(itemInB, b.GetProperty("itemId").GetString());
        Assert.False(b.GetProperty("isRead").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Archive_ExcludesItemFromDefaultList()
    {
        var projectId = await CreateProjectAsync("inbox-archive");
        var first = await SeedAsync(projectId, 1, "First",
            NotificationKinds.WorkflowFailed, TestTime.UtcNow, "evt-ar-1");
        await SeedAsync(projectId, 2, "Second",
            NotificationKinds.ApprovalRequested, TestTime.UtcNow, "evt-ar-2");

        await _client.PostOkAsync($"/api/projects/{projectId}/inbox/{first}/archive");

        var items = await _client.GetDataAsync<JsonElement[]>(
            $"/api/projects/{projectId}/inbox");

        var surviving = Assert.Single(items);
        Assert.Equal(2, surviving.GetProperty("issueNumber").GetInt32());
        Assert.False(surviving.GetProperty("isArchived").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Archive_CrossProjectItemId_Returns404()
    {
        var projectA = await CreateProjectAsync("inbox-arc-a");
        var projectB = await CreateProjectAsync("inbox-arc-b");

        var itemInA = await SeedAsync(projectA, 1, "First",
            NotificationKinds.WorkflowFailed, TestTime.UtcNow, "evt-arc-x-1");

        using var response = await _client.PostAsync(
            $"/api/projects/{projectB}/inbox/{itemInA}/archive",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The cross-project attempt must not have touched the row.
        var itemsA = await _client.GetDataAsync<JsonElement[]>(
            $"/api/projects/{projectA}/inbox");
        var a = Assert.Single(itemsA);
        Assert.False(a.GetProperty("isArchived").GetBoolean());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Archive_UnknownItemId_Returns404()
    {
        var projectId = await CreateProjectAsync("inbox-arc-unknown");

        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/inbox/inb_does_not_exist/archive",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task List_UnknownProject_Returns404()
    {
        using var response = await _client.GetAsync(
            "/api/projects/proj_does_not_exist/inbox");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task WorkflowRunStoreEvent_PersistsRowAndDispatcherProjectsItToInbox()
    {
        // issue-362 T-003: WorkflowRunStore pokes the dispatcher grain
        // immediately after the transaction commits. The dispatcher picks
        // up the freshly-persisted workflow.run.failed event and
        // InboxProjectionHandler projects it into the project's inbox —
        // restoring the at-least-once delivery the dispatcher was wired
        // up to provide. The cross-project isolation rule still holds:
        // the other project's inbox remains empty.
        var projectId = await CreateProjectAsync("inbox-spec");
        await _client.PostOkAsync(
            $"/api/projects/{projectId}/repositories",
            new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<JsonElement>(
            $"/api/projects/{projectId}/issues",
            new
            {
                title = "Started from API",
                body = "project through production bus",
                labels = new Dictionary<string, string>(StringComparer.Ordinal),
                priority = "p1",
                projectId,
                isDraft = false,
            });
        var issueNumber = issue.GetProperty("number").GetInt32();

        var runId = $"wr_inbox_spec_{Guid.NewGuid():N}";
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var runStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
            await runStore.SaveAsync(new WorkflowRun
            {
                Id = runId,
                Metadata = new WorkflowRunMetadata(
                    Name: null,
                    CreatedAt: TestTime.UtcNow,
                    Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["projectId"] = projectId,
                        ["issueNumber"] = issueNumber.ToString(),
                    }),
                Stages = [],
            }, [new WorkflowRunFailed("failed")]);
        }

        await using (var verify = _fixture.Services.CreateAsyncScope())
        {
            var events = verify.ServiceProvider.GetRequiredService<Mohist.Server.Infrastructure.Events.IEventStore>();
            var stored = await events.ListAsync(runId);
            var storedFailed = Assert.Single(stored);
            Assert.Equal("com.mohist.workflow.run.failed", storedFailed.Envelope.Type);
        }

        var inboxItems = await TestWait.ForAsync(
            () => _client.GetDataAsync<JsonElement[]>($"/api/projects/{projectId}/inbox"),
            items => items.Length >= 1,
            timeout: TimeSpan.FromSeconds(10),
            step: TimeSpan.FromMilliseconds(100),
            description: "inbox row projected by dispatcher after WorkflowRunStore poke");
        var item = Assert.Single(inboxItems);
        Assert.Equal("workflow_failed", item.GetProperty("notificationKind").GetString());

        var otherProjectId = await CreateProjectAsync("inbox-spec-other");
        Assert.Empty(await _client.GetDataAsync<JsonElement[]>(
            $"/api/projects/{otherProjectId}/inbox"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Inbox)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task MarkRead_UnknownProject_Returns404()
    {
        using var response = await _client.PostAsync(
            "/api/projects/proj_does_not_exist/inbox/inb_anything/read",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<JsonElement>(
            "/api/projects",
            $"{prefix}-{Guid.NewGuid():N}");
        return project.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement[]> WaitForInboxItemsAsync(string projectId, int expectedCount)
    {
        return await TestWait.ForAsync(
            () => _client.GetDataAsync<JsonElement[]>($"/api/projects/{projectId}/inbox"),
            items => items.Length == expectedCount,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(20),
            $"project '{projectId}' inbox to contain {expectedCount} items");
    }

    private async Task<string> SeedAsync(
        string projectId,
        int issueNumber,
        string title,
        string notificationKind,
        DateTimeOffset createdAt,
        string sourceEventId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<InboxStore>();
        var result = await store.InsertAsync(new InboxItemDraft(
            ProjectId: projectId,
            IssueNumber: issueNumber,
            IssueTitle: title,
            NotificationKind: notificationKind,
            SourceEventSource: $"/mohist/projects/{projectId}/issues/{issueNumber}",
            SourceEventId: sourceEventId,
            CreatedAt: createdAt));
        return result.Id;
    }

    private async Task ArchiveAsync(string projectId, string itemId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<InboxStore>();
        await store.ArchiveAsync(projectId, itemId);
    }

    private async Task ArchiveDirectAsync(string projectId, string sourceEventId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.InboxItems.SingleAsync(r => r.ProjectId == projectId && r.SourceEventId == sourceEventId);
        await db.InboxItems
            .Where(r => r.Id == row.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ArchivedAt, TestTime.UtcNow));
    }
}
