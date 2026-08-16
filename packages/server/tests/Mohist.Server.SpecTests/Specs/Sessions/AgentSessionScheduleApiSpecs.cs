using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

// Scheduled-input API entry shape (subagents.md 定时输入): the routes are
// project-scoped like follow-ups, the body accepts only text + dueAt, dueAt
// must be an offset RFC 3339 instant strictly after the server clock, and
// creation is idempotent per explicit Idempotency-Key. Delivery semantics
// are grain-level (AgentSessionScheduleGrainSpecs); this file proves the
// real HTTP surface rejects/accepts exactly the documented contract and
// never requires a binding or activity at creation time.
[Collection("IntegrationSessions")]
public sealed class AgentSessionScheduleApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentSessionScheduleApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task CreateSchedule_RejectsUndeclaredFields()
    {
        var (projectId, sessionId) = await CreateIdleSessionAsync("undeclared");

        using var response = await PostScheduleAsync(projectId, sessionId, new
        {
            text = "ping",
            dueAt = "2099-01-01T10:00:00Z",
            attachments = new[] { "a-1" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("unsupported_field", doc.RootElement.GetProperty("code").GetString());
        var fields = doc.RootElement.GetProperty("details").GetProperty("fields");
        Assert.Equal("attachments", fields[0].GetString());
    }

    [Fact]
    public async Task CreateSchedule_RejectsOffsetlessDueAt()
    {
        var (projectId, sessionId) = await CreateIdleSessionAsync("offsetless");

        using var response = await PostScheduleAsync(projectId, sessionId, new
        {
            text = "ping",
            dueAt = "2099-01-01T10:00:00",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("schedule_due_invalid", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateSchedule_RejectsPastDueAt()
    {
        var (projectId, sessionId) = await CreateIdleSessionAsync("past");

        using var response = await PostScheduleAsync(projectId, sessionId, new
        {
            text = "ping",
            dueAt = "2020-01-01T00:00:00Z",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("schedule_due_in_past", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateSchedule_RequiresVisibleText()
    {
        var (projectId, sessionId) = await CreateIdleSessionAsync("no-text");

        using var response = await PostScheduleAsync(projectId, sessionId, new
        {
            dueAt = "2099-01-01T10:00:00Z",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("schedule_text_required", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateSchedule_UnknownSession_ReturnsNotFound()
    {
        var (projectId, _) = await CreateIdleSessionAsync("unknown-session");

        using var response = await PostScheduleAsync(projectId, $"no-such-{Guid.NewGuid():N}", new
        {
            text = "ping",
            dueAt = "2099-01-01T10:00:00Z",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateSchedule_SessionFromAnotherProject_ReturnsNotFound()
    {
        var (projectId, sessionId) = await CreateIdleSessionAsync("owner-project");
        var otherProjectId = await CreateProjectAsync("other-project");

        using var response = await PostScheduleAsync(otherProjectId, sessionId, new
        {
            text = "ping",
            dueAt = "2099-01-01T10:00:00Z",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateSchedule_CreatesScheduledEntryWithoutBindingOrActivity()
    {
        // The session was only opened via the grain with no Runner binding
        // and no runtime events; creation must still succeed.
        var (projectId, sessionId) = await CreateIdleSessionAsync("happy");

        using var response = await PostScheduleAsync(
            projectId,
            sessionId,
            new { text = "  report progress  ", dueAt = "2099-01-01T18:00:00+08:00" },
            idempotencyKey: "sch-key-happy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var data = doc.RootElement.GetProperty("data");
        Assert.False(data.GetProperty("alreadyExists").GetBoolean());
        var scheduleId = data.GetProperty("scheduleId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(scheduleId));
        Assert.Equal("scheduled", data.GetProperty("status").GetString());
        Assert.Equal("  report progress  ", data.GetProperty("text").GetString());
        // The offset form is normalized to its UTC instant on the wire.
        Assert.Equal("2099-01-01T10:00:00Z", data.GetProperty("dueAt").GetString());
        Assert.Equal("sch-key-happy", data.GetProperty("idempotencyKey").GetString());
        Assert.False(data.TryGetProperty("inputId", out _));

        var persisted = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).ListSchedulesAsync();
        var schedule = Assert.Single(persisted);
        Assert.Equal(scheduleId, schedule.ScheduleId);
        Assert.Equal(SessionScheduleStatus.Scheduled, schedule.Status);
    }

    [Fact]
    public async Task CreateSchedule_SameKeySameBody_ReplaysOriginalSchedule()
    {
        var (projectId, sessionId) = await CreateIdleSessionAsync("replay-same");
        var first = await PostScheduleAsync(projectId, sessionId, new { text = "ping", dueAt = "2099-01-01T10:00:00Z" }, "replay-key");
        var firstData = (await ReadJsonAsync(first)).RootElement.GetProperty("data");

        var second = await PostScheduleAsync(projectId, sessionId, new { text = "  ping  ", dueAt = "2099-01-01T18:00:00+08:00" }, "replay-key");
        var secondData = (await ReadJsonAsync(second)).RootElement.GetProperty("data");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstData.GetProperty("scheduleId").GetString(), secondData.GetProperty("scheduleId").GetString());
        Assert.True(secondData.GetProperty("alreadyExists").GetBoolean());
        Assert.Equal("scheduled", secondData.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateSchedule_SameKeyDifferentText_Conflicts()
    {
        var (projectId, sessionId) = await CreateIdleSessionAsync("replay-conflict");
        using var first = await PostScheduleAsync(projectId, sessionId, new { text = "first", dueAt = "2099-01-01T10:00:00Z" }, "conflict-key");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await PostScheduleAsync(projectId, sessionId, new { text = "second", dueAt = "2099-01-01T10:00:00Z" }, "conflict-key");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var doc = await ReadJsonAsync(second);
        Assert.Equal("idempotency_conflict", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ListSchedules_ReturnsAllOrderedByDueAt()
    {
        var (projectId, sessionId) = await CreateIdleSessionAsync("list");
        await PostScheduleAsync(projectId, sessionId, new { text = "third", dueAt = "2099-06-01T00:00:00Z" }, "list-key-3");
        await PostScheduleAsync(projectId, sessionId, new { text = "first", dueAt = "2099-01-01T00:00:00Z" }, "list-key-1");
        await PostScheduleAsync(projectId, sessionId, new { text = "second", dueAt = "2099-03-01T00:00:00Z" }, "list-key-2");

        using var response = await _client.GetAsync($"/api/projects/{projectId}/agent-sessions/{sessionId}/schedules");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(3, data.GetArrayLength());
        Assert.Equal("first", data[0].GetProperty("text").GetString());
        Assert.Equal("second", data[1].GetProperty("text").GetString());
        Assert.Equal("third", data[2].GetProperty("text").GetString());
        Assert.Equal("2099-01-01T00:00:00Z", data[0].GetProperty("dueAt").GetString());
    }

    [Fact]
    public async Task CancelSchedule_ScheduledAdvancesToCancelledAndReplayIsIdempotent()
    {
        var (projectId, sessionId) = await CreateIdleSessionAsync("cancel");
        var created = await PostScheduleAsync(projectId, sessionId, new { text = "cancel me", dueAt = "2099-01-01T10:00:00Z" }, "cancel-key");
        var scheduleId = (await ReadJsonAsync(created)).RootElement.GetProperty("data").GetProperty("scheduleId").GetString();

        using var cancelled = await _client.PostAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/schedules/{scheduleId}/cancel",
            content: null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        using var cancelledDoc = await ReadJsonAsync(cancelled);
        var cancelledData = cancelledDoc.RootElement.GetProperty("data");
        Assert.Equal("cancelled", cancelledData.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(cancelledData.GetProperty("cancelledAt").GetString()));

        using var replay = await _client.PostAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/schedules/{scheduleId}/cancel",
            content: null);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayDoc = await ReadJsonAsync(replay);
        Assert.Equal("cancelled", replayDoc.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(
            cancelledData.GetProperty("cancelledAt").GetString(),
            replayDoc.RootElement.GetProperty("data").GetProperty("cancelledAt").GetString());
    }

    [Fact]
    public async Task CancelSchedule_UnknownSchedule_ReturnsNotFound()
    {
        var (projectId, sessionId) = await CreateIdleSessionAsync("cancel-unknown");

        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/schedules/no-such-schedule/cancel",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----- helpers -----

    private async Task<(string ProjectId, string SessionId)> CreateIdleSessionAsync(string name)
    {
        var projectId = await CreateProjectAsync(name);
        var sessionId = $"schedule-api-{name}-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: $"/workspaces/{projectId}",
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                projectId,
                $"agent-{Guid.NewGuid():N}",
                "schedule-agent"))));
        return (projectId, sessionId);
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        var projectName = $"schedule-api-{name}-{Guid.NewGuid():N}";
        if (projectName.Length > 63) projectName = projectName[..63];
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        return project.Id;
    }

    private Task<HttpResponseMessage> PostScheduleAsync(
        string projectId,
        string sessionId,
        object body,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agent-sessions/{sessionId}/schedules")
        {
            Content = JsonContent.Create(body),
        };
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return _client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private sealed record ProjectDto(string Id);
}
