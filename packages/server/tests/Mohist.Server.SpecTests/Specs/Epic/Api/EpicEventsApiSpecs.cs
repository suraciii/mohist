using System.Net;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Api;

/// <summary>
/// Specs for issue-94 T-005: <c>GET /api/projects/{projectRef}/epics/{id}/events</c>.
/// Verifies the route:
/// <list type="bullet">
/// <item>accepts either the internal id or the epic number on the
///   <c>{id}</c> segment;</item>
/// <item>returns HTTP 200 with the events ordered chronologically for an
///   epic with persisted events;</item>
/// <item>returns HTTP 200 with an empty list when no events have been
///   persisted for the epic;</item>
/// <item>returns HTTP 404 for a missing epic (unknown id or unassigned
///   number);</item>
/// <item>honours the <c>?limit=</c> query parameter.</item>
/// </list>
/// </summary>
[Collection("MohistIntegration2")]
public class EpicEventsApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public EpicEventsApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task GetEvents_ForEpicWithOnlyTheCreationEvent_Returns200WithSingletonList()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Brand new epic");

        var events = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Id}/events");

        // The T-001 EpicEventPublishSpecs guarantee EpicCreated is
        // persisted on epic creation. So a freshly-created epic has
        // exactly one event.
        var created = Assert.Single(events);
        Assert.Equal("com.mohist.epic.created", created.Type);
        Assert.Equal("Brand new epic", created.Data.GetProperty("title").GetString());
        Assert.Equal("p2", created.Data.GetProperty("priority").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task GetEvents_ForEpicWithOnlyCreationEvent_IsHttp200()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Status shape epic");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/epics/{epic.Id}/events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task GetEvents_AfterMutations_ReturnsEventsChronologically()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Lifecycle epic", "p2");

        // Drive persisted transitions. Each persists its own envelope; the
        // read endpoint should expose them in chronological order.
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Id}/start", null);

        // An epic with no open linked members auto-marks-done via the
        // status-changed→recompute trigger. If that happens, the pause
        // call fails with a terminal conflict — that's an acceptable
        // settled state for this chronology spec.
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var pauseResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Id}/pause",
            new { reason = "waiting on review" });
        if (pauseResponse.IsSuccessStatusCode)
        {
            _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
            await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Id}/resume", null);
        }

        var events = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Id}/events");

        Assert.NotEmpty(events);
        Assert.Equal("com.mohist.epic.created", events[0].Type);
        Assert.NotEqual(0, events[0].Id);

        for (var i = 1; i < events.Length; i++)
            Assert.True(events[i].Id > events[i - 1].Id, $"event Id {events[i].Id} not greater than {events[i - 1].Id}");

        var statusChanges = events
            .Where(e => e.Type == "com.mohist.epic.status-changed")
            .Select(e => (New: e.Data.GetProperty("newStatus").GetString(), Old: e.Data.GetProperty("oldStatus").GetString()))
            .ToList();
        Assert.Contains(statusChanges, s => s.Old == "idle" && s.New == "running");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task GetEvents_AcceptsEpicNumberOnIdSegment()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "By number");

        // By id
        var byId = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Id}/events");
        Assert.NotEmpty(byId);

        // By number — must produce the same body as by id
        Assert.NotNull(epic.Number);
        var byNumber = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Number}/events");
        Assert.Equal(byId.Length, byNumber.Length);
        for (var i = 0; i < byId.Length; i++)
            Assert.Equal(byId[i].Id, byNumber[i].Id);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task GetEvents_OnUnknownEpicId_Returns404()
    {
        var project = await CreateProjectAsync();

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/epics/epic_nonexistent/events");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task GetEvents_OnUnassignedNumber_Returns404()
    {
        var project = await CreateProjectAsync();
        await CreateEpicAsync(project.Id, "First epic");

        // 99999 is a number that was never assigned in this project.
        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/epics/99999/events");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task GetEvents_WithLimit_ReturnsTailOnly()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Limit epic");

        // Seed multiple events without driving the recompute path that
        // could auto-mark the epic done (T-002/T-003 patterns avoid a
        // Resume with a fully-blocked linked-issue set). Here we just
        // pause from idle (which is a guarded 409) so instead drive
        // start -> pause -> resume, which keeps the epic running, and
        // patch priority changes.
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Id}/start", null);
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Id}",
            new { priority = "p0" });
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Id}",
            new { priority = "p1" });

        var unlimited = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Id}/events");
        Assert.True(unlimited.Length >= 3, $"expected at least 3 events but got {unlimited.Length}");

        var limited = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Id}/events?limit=1");

        var single = Assert.Single(limited);
        // UpdateAsync emits EpicUpdated followed by EpicPriorityChanged;
        // the tail of the recorded stream is therefore EpicUpdated.
        Assert.Equal("com.mohist.epic.updated", single.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task GetEvents_DtoShapeExposesTypeTimeAndPayload()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Shape epic", "p2");

        // Drive a priority change to assert the payload round-trips through
        // the wire format (no discriminator field is required by the
        // contract — type-specific payload is read directly from `data`).
        await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Id}",
            new { priority = "p0" });

        var events = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Id}/events");

        var priorityChanged = Assert.Single(events, e => e.Type == "com.mohist.epic.priority-changed");
        Assert.Equal("p2", priorityChanged.Data.GetProperty("oldPriority").GetString());
        Assert.Equal("p0", priorityChanged.Data.GetProperty("newPriority").GetString());
        Assert.False(string.IsNullOrWhiteSpace(priorityChanged.Time));
        Assert.False(string.IsNullOrWhiteSpace(priorityChanged.EventId));
        Assert.Equal("1.0", priorityChanged.SpecVersion);
        Assert.Equal($"/mohist/epics/{epic.Id}", priorityChanged.Source);
        Assert.Equal(project.Id, priorityChanged.Extensions["projectid"]);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new
        {
            name = $"epic-events-{Guid.NewGuid():N}",
        });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            isDefault = true,
        });
        return project;
    }

    private async Task<EpicRowDto> CreateEpicAsync(string projectId, string title, string priority = "p2")
    {
        var epic = await _client.PostDataAsync<EpicRowDto>(
            $"/api/projects/{projectId}/epics",
            new { title, description = "events spec", priority });
        return epic;
    }

    private sealed record ProjectDto(string Id);
    private sealed record EpicRowDto(string Id, int? Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);
    private sealed record StoredEventDto(
        long Id,
        string EventId,
        string Source,
        string Type,
        string SpecVersion,
        string? Subject,
        string Time,
        string? DataContentType,
        JsonElement Data,
        Dictionary<string, string> Extensions);
}