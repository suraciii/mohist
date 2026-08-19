using System.Net;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Api;

/// <summary>
/// Specs for issue-94 T-005: <c>GET /api/projects/{projectRef}/epics/{number}/events</c>.
/// Verifies the route:
/// <list type="bullet">
/// <item>accepts the project-scoped epic number on the route segment;</item>
/// <item>returns HTTP 200 with the events ordered chronologically for an
///   epic with persisted events;</item>
/// <item>returns HTTP 200 with an empty list when no events have been
///   persisted for the epic;</item>
/// <item>returns HTTP 404 for an unassigned number;</item>
/// <item>honours the <c>?limit=</c> query parameter.</item>
/// </list>
/// </summary>
public class EpicEventsApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public EpicEventsApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task GetEvents_ForEpicWithOnlyTheCreationEvent_Returns200WithSingletonList()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Brand new epic");

        var events = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Number}/events");

        // The T-001 EpicEventPublishSpecs guarantee EpicCreated is
        // persisted on epic creation. So a freshly-created epic has
        // exactly one event.
        var created = Assert.Single(events);
        Assert.Equal("com.mohist.epic.created", created.Type);
        Assert.Equal("Brand new epic", created.Data.GetProperty("title").GetString());
        Assert.Equal("p2", created.Data.GetProperty("priority").GetString());
    }

    [Fact]
    public async Task GetEvents_ForEpicWithOnlyCreationEvent_IsHttp200()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Status shape epic");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetEvents_AfterMutations_ReturnsEventsChronologically()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Lifecycle epic", "p2");

        // Drive persisted transitions. Each persists its own envelope; the
        // read endpoint should expose them in chronological order.
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Number}/start", null);

        // An epic with no open linked members auto-marks-done via the
        // status-changed→recompute trigger. If that happens, the pause
        // call fails with a terminal conflict — that's an acceptable
        // settled state for this chronology spec.
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var pauseResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}/pause",
            new { reason = "waiting on review" });
        if (pauseResponse.IsSuccessStatusCode)
        {
            _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
            await _client.PostOkAsync($"/api/projects/{project.Id}/epics/{epic.Number}/resume", null);
        }

        var events = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Number}/events");

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

    [Fact]
    public async Task GetEvents_AcceptsEpicNumber()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "By number");

        var events = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Number}/events");
        Assert.NotEmpty(events);
    }

    [Fact]
    public async Task GetEvents_OnUnknownEpicNumber_Returns404()
    {
        var project = await CreateProjectAsync();

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/epics/99999/events");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

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

    [Fact]
    public async Task GetEvents_WithLimit_ReturnsTailOnly()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Limit epic");

        // Seed a stable stream without starting asynchronous status handlers.
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}",
            new { priority = "p0" });
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}",
            new { priority = "p1" });

        var unlimited = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Number}/events");
        Assert.True(unlimited.Length >= 3, $"expected at least 3 events but got {unlimited.Length}");

        var limited = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Number}/events?limit=1");

        var single = Assert.Single(limited);
        Assert.Equal(unlimited[^1].Id, single.Id);
        Assert.Equal(unlimited[^1].Type, single.Type);
    }

    [Fact]
    public async Task GetEvents_DtoShapeExposesTypeTimeAndPayload()
    {
        var project = await CreateProjectAsync();
        var epic = await CreateEpicAsync(project.Id, "Shape epic", "p2");

        // Drive a priority change to assert the payload round-trips through
        // the wire format (no discriminator field is required by the
        // contract — type-specific payload is read directly from `data`).
        await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{epic.Number}",
            new { priority = "p0" });

        var events = await _client.GetDataAsync<StoredEventDto[]>(
            $"/api/projects/{project.Id}/epics/{epic.Number}/events");

        var priorityChanged = Assert.Single(events, e => e.Type == "com.mohist.epic.priority-changed");
        Assert.Equal("p2", priorityChanged.Data.GetProperty("oldPriority").GetString());
        Assert.Equal("p0", priorityChanged.Data.GetProperty("newPriority").GetString());
        Assert.False(string.IsNullOrWhiteSpace(priorityChanged.Time));
        Assert.False(string.IsNullOrWhiteSpace(priorityChanged.EventId));
        Assert.Equal("1.0", priorityChanged.SpecVersion);
        Assert.Equal($"/mohist/projects/{project.Id}/epics/{epic.Number}", priorityChanged.Source);
        Assert.Equal(project.Id, priorityChanged.Extensions["projectid"]);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-events-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
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
    private sealed record EpicRowDto(int Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);
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
