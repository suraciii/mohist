using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public sealed class AgentPathAmplificationSpecs
{
    private static readonly string[] AmplificationFields =
        ["candidates", "databaseCalls", "downstreamCalls", "processed", "transcriptRecords"];
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentPathAmplificationSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task Status_reports_filtered_candidates_with_truthful_off_state_counts()
    {
        var project = await CreateProjectAsync("status-filter");
        await InsertSessionsAsync(project.Id, count: 1, activeCount: 1);
        var small = (await GetDataAsync($"/api/projects/{project.Id}/agent/status"))
            .GetProperty("amplification");
        await InsertSessionsAsync(project.Id, count: 20, activeCount: 0);

        var status = await GetDataAsync($"/api/projects/{project.Id}/agent/status");
        var amplification = status.GetProperty("amplification");

        AssertAmplificationShape(amplification);
        Assert.Equal(21, amplification.GetProperty("candidates").GetInt64());
        Assert.Equal(1, amplification.GetProperty("processed").GetInt64());
        Assert.Equal(0, amplification.GetProperty("transcriptRecords").GetInt64());
        Assert.Equal(
            small.GetProperty("databaseCalls").GetInt64(),
            amplification.GetProperty("databaseCalls").GetInt64());
        Assert.Equal(
            small.GetProperty("downstreamCalls").GetInt64(),
            amplification.GetProperty("downstreamCalls").GetInt64());
    }

    [Fact]
    public async Task Status_without_current_agents_keeps_explicit_amplification()
    {
        var project = await CreateProjectAsync("status-empty");

        var status = await GetDataAsync($"/api/projects/{project.Id}/agent/status");
        var amplification = status.GetProperty("amplification");

        AssertAmplificationShape(amplification);
        Assert.Equal(0, amplification.GetProperty("candidates").GetInt64());
        Assert.Equal(0, amplification.GetProperty("processed").GetInt64());
        Assert.Equal(0, amplification.GetProperty("transcriptRecords").GetInt64());
    }

    [Fact]
    public async Task Activity_counts_each_repeated_transcript_materialization()
    {
        var project = await CreateProjectAsync("activity-transcript");
        var sessionId = (await InsertSessionsAsync(project.Id, count: 1, activeCount: 1)).Single();
        await InsertTranscriptPartsAsync(sessionId, 2);

        var activity = await GetDataAsync($"/api/projects/{project.Id}/agent/activity");
        var amplification = activity.GetProperty("amplification");

        AssertAmplificationShape(amplification);
        Assert.Equal(1, amplification.GetProperty("candidates").GetInt64());
        Assert.Equal(1, amplification.GetProperty("processed").GetInt64());
        Assert.Equal(4, amplification.GetProperty("transcriptRecords").GetInt64());
        Assert.True(amplification.GetProperty("databaseCalls").GetInt64() > 0);
        Assert.True(amplification.GetProperty("downstreamCalls").GetInt64() > 0);
    }

    [Fact]
    public async Task Activity_limits_candidates_and_cards_to_two_hundred()
    {
        var project = await CreateProjectAsync("activity-limit");
        await InsertSessionsAsync(project.Id, count: 1, activeCount: 1);
        var small = (await GetDataAsync($"/api/projects/{project.Id}/agent/activity?limit=10000"))
            .GetProperty("amplification");
        await InsertSessionsAsync(project.Id, count: 204, activeCount: 204);

        var activity = await GetDataAsync($"/api/projects/{project.Id}/agent/activity?limit=10000");
        var amplification = activity.GetProperty("amplification");

        Assert.Equal(200, activity.GetProperty("sessions").GetArrayLength());
        Assert.Equal(200, amplification.GetProperty("candidates").GetInt64());
        Assert.Equal(200, amplification.GetProperty("processed").GetInt64());
        Assert.Equal(
            small.GetProperty("databaseCalls").GetInt64(),
            amplification.GetProperty("databaseCalls").GetInt64());
        Assert.Equal(
            small.GetProperty("downstreamCalls").GetInt64(),
            amplification.GetProperty("downstreamCalls").GetInt64());
        AssertAmplificationShape(amplification);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("activity")]
    public async Task Alias_without_nonblank_selector_returns_no_active_project(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/agent/{path}?projectId=%20%20");
        request.Headers.Add("X-Mohist-Project", "  ");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("No active project", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("status", true)]
    [InlineData("activity", false)]
    [InlineData("activity", true)]
    public async Task Alias_accepts_trimmed_project_id_or_name(string path, bool useName)
    {
        var project = await CreateProjectAsync($"alias-{path}-{useName}");
        await InsertSessionsAsync(project.Id, count: 1, activeCount: 1);
        var selector = useName ? project.Name : project.Id;

        var data = await GetDataAsync($"/api/agent/{path}?projectId={Uri.EscapeDataString($"  {selector}  ")}");

        Assert.Equal(1, data.GetProperty("amplification").GetProperty("candidates").GetInt64());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("activity")]
    public async Task Blank_query_falls_back_to_trimmed_header(string path)
    {
        var project = await CreateProjectAsync($"header-{path}");
        await InsertSessionsAsync(project.Id, count: 1, activeCount: 1);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/agent/{path}?projectId=%20");
        request.Headers.Add("X-Mohist-Project", $"  {project.Name}  ");

        using var response = await _client.SendAsync(request);
        var data = await ReadDataAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, data.GetProperty("amplification").GetProperty("candidates").GetInt64());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("activity")]
    public async Task First_nonblank_query_wins_conflicting_header(string path)
    {
        var selected = await CreateProjectAsync($"query-{path}");
        var header = await CreateProjectAsync($"header-conflict-{path}");
        await InsertSessionsAsync(selected.Id, count: 1, activeCount: 1);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/agent/{path}?projectId=%20&projectId={Uri.EscapeDataString(selected.Id)}");
        request.Headers.Add("X-Mohist-Project", header.Id);

        using var response = await _client.SendAsync(request);
        var data = await ReadDataAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, data.GetProperty("amplification").GetProperty("candidates").GetInt64());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("activity")]
    public async Task Alias_and_canonical_routes_use_the_same_handler(string path)
    {
        var project = await CreateProjectAsync($"parity-{path}");
        await InsertSessionsAsync(project.Id, count: 1, activeCount: 1);

        var canonical = await GetDataAsync($"/api/projects/{project.Id}/agent/{path}");
        var alias = await GetDataAsync($"/api/agent/{path}?projectId={Uri.EscapeDataString(project.Id)}");

        Assert.True(JsonElement.DeepEquals(canonical, alias));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("activity")]
    public async Task Alias_unknown_project_returns_not_found(string path)
    {
        using var response = await _client.GetAsync($"/api/agent/{path}?projectId=proj_unknown_{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static void AssertAmplificationShape(JsonElement amplification)
    {
        var properties = amplification.EnumerateObject().ToArray();
        Assert.Equal(AmplificationFields, properties.Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.All(properties, property =>
        {
            Assert.Equal(JsonValueKind.Number, property.Value.ValueKind);
            Assert.True(property.Value.GetInt64() >= 0);
        });
    }

    private async Task<ProjectDto> CreateProjectAsync(string suffix) =>
        await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"amp-{suffix}-{Guid.NewGuid():N}");

    private async Task<IReadOnlyList<string>> InsertSessionsAsync(string projectId, int count, int activeCount)
    {
        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        var ids = Enumerable.Range(0, count).Select(_ => $"session-{Guid.NewGuid():N}").ToArray();
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();

        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index];
            var session = new AgentSession
            {
                Id = id,
                Runtime = new AgentSessionRuntime("runner-amplification", null),
                Settings = new AgentSessionSettings("test-model"),
                Status = new AgentSessionStatusSnapshot(
                    CreatedAt: now,
                    BoundAt: now,
                    LastDataAt: index < activeCount ? now : null,
                    AgentRuntimeSessionId: id,
                    Activity: index < activeCount ? AgentSessionActivity.Active : AgentSessionActivity.Idle),
                Metadata = new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                    [GenericAgentSessionMetadata.AgentId] = $"agent-{index}",
                    [GenericAgentSessionMetadata.AgentName] = $"Agent {index}",
                }),
            };
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = id,
                State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
                CreatedAt = now.AddTicks(index),
                Status = "bound",
                AgentSessionId = id,
                RunnerId = "runner-amplification",
            });
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task InsertTranscriptPartsAsync(string sessionId, int count)
    {
        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var turn = new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            Sequence = 1,
            StartedAt = now,
            UpdatedAt = now,
        };
        db.AgentSessionTranscriptTurns.Add(turn);
        await db.SaveChangesAsync();

        for (var index = 0; index < count; index++)
        {
            db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
            {
                TurnId = turn.Id,
                Sequence = index + 1,
                Type = "message.delta",
                CorrelationKey = $"part-{Guid.NewGuid():N}",
                PayloadJson = $"{{\"text\":\"message-{index}\"}}",
                LastSeenAt = now.AddTicks(index),
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> GetDataAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadDataAsync(response);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var envelope = await JsonSerializer.DeserializeAsync<JsonElement>(await response.Content.ReadAsStreamAsync());
        return envelope.GetProperty("data");
    }

    private sealed record ProjectDto(string Id, string Name);
}
