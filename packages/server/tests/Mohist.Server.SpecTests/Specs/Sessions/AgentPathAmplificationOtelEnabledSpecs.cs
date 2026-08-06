using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// End-to-end amplification contract under OTel=on. The off-state
/// truthfulness is exercised by <c>AgentPathAmplificationSpecs</c>;
/// this class asserts the same wire body is produced when OTel
/// collection is configured enabled, so handlers and middleware do
/// not diverge between states. The Meter-publication contract for the
/// enabled state is locked separately by
/// <c>Mohist.Server.UnitTests.Telemetry.RuntimeMetricCatalogTests</c>
/// and <c>RequestWorkScopeTests</c>; this spec only owns the
/// wire-level response parity.
/// </summary>
[Collection("OtelTracing")]
public sealed class AgentPathAmplificationOtelEnabledSpecs : IClassFixture<OtelIntegrationFixture>
{
    private static readonly string[] AmplificationFields =
        ["candidates", "databaseCalls", "downstreamCalls", "processed", "transcriptRecords"];

    private readonly OtelIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentPathAmplificationOtelEnabledSpecs(OtelIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task Status_returns_truthful_local_amplification_when_otel_is_enabled()
    {
        var project = await CreateProjectAsync("otel-status");
        await InsertSessionsAsync(project.Id, count: 1, activeCount: 1);

        var status = await GetDataAsync($"/api/projects/{project.Id}/agent/status");
        var amplification = status.GetProperty("amplification");

        AssertAmplificationShape(amplification);
        Assert.Equal(1, amplification.GetProperty("candidates").GetInt64());
        Assert.Equal(1, amplification.GetProperty("processed").GetInt64());
        Assert.Equal(0, amplification.GetProperty("transcriptRecords").GetInt64());
        Assert.True(amplification.GetProperty("databaseCalls").GetInt64() > 0);
    }

    [Fact]
    public async Task Activity_returns_truthful_local_amplification_when_otel_is_enabled()
    {
        // issue-468 T-002: amplification.transcriptRecords no longer counts
        // the duplicate summary-reduction pass; only the preview loader
        // contributes, so two transcript parts yield transcriptRecords == 2
        // (preview only) rather than the historical 4.
        var project = await CreateProjectAsync("otel-activity");
        var sessionId = (await InsertSessionsAsync(project.Id, count: 1, activeCount: 1)).Single();
        await InsertTranscriptPartsAsync(sessionId, 2);

        var activity = await GetDataAsync($"/api/projects/{project.Id}/agent/activity");
        var amplification = activity.GetProperty("amplification");

        AssertAmplificationShape(amplification);
        Assert.Equal(1, amplification.GetProperty("candidates").GetInt64());
        Assert.Equal(1, amplification.GetProperty("processed").GetInt64());
        Assert.Equal(2, amplification.GetProperty("transcriptRecords").GetInt64());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("activity")]
    public async Task Alias_resolves_one_project_when_otel_is_enabled(string path)
    {
        var project = await CreateProjectAsync($"otel-alias-{path}");
        await InsertSessionsAsync(project.Id, count: 1, activeCount: 1);

        var alias = await GetDataAsync($"/api/agent/{path}?projectId={Uri.EscapeDataString(project.Id)}");
        var canonical = await GetDataAsync($"/api/projects/{project.Id}/agent/{path}");

        Assert.True(JsonElement.DeepEquals(canonical, alias));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("activity")]
    public async Task Alias_without_selector_returns_bad_request_when_otel_is_enabled(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/agent/{path}?projectId=%20%20");
        request.Headers.Add("X-Mohist-Project", "  ");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
            $"amp-otel-{suffix}-{Guid.NewGuid():N}");

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
                Runtime = new AgentSessionRuntime("runner-amplification-otel", null),
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
                RunnerId = "runner-amplification-otel",
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
        var envelope = await JsonSerializer.DeserializeAsync<JsonElement>(await response.Content.ReadAsStreamAsync());
        return envelope.GetProperty("data");
    }

    private sealed record ProjectDto(string Id, string Name);
}