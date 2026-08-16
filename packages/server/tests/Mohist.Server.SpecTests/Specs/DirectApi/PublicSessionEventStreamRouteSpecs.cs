using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.PublicApi;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.DirectApi;

/// <summary>
/// The direct Session event route is a projection-only, exclusive-after
/// reader. These specs exercise the durable journal page shape and the
/// cursor lifecycle without using the live event bus or UI timeline.
/// </summary>
[Collection("PublicSessionEventStream")]
public sealed class PublicSessionEventStreamRouteSpecs(MohistIntegrationFixture fixture)
{
    private static readonly string[] ExecutionKeys =
    [
        "projectId", "agentId", "jobId", "sessionId", "inputId", "turnId",
        "status", "jobStatus", "sessionActivity", "admission", "inputStatus",
        "turnStatus", "outcome", "reasonCode", "output", "error",
        "acceptedAt", "queuedAt", "startedAt", "terminalAt", "observedAt",
        "sequence",
    ];

    private static readonly string[] SessionKeys =
    ["projectId", "agentId", "sessionId", "sessionActivity", "admission", "reasonCode"];

    [Fact]
    public async Task PagesAreExclusiveAscendingAndEmptyPagesStayAtHighWater()
    {
        var seeded = await SeedStreamAsync();
        using var client = await CreateReaderAsync(seeded.ProjectId);

        using var first = await GetAsync(client, seeded.ProjectId, seeded.SessionId, "limit=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstEvent = firstJson.RootElement.GetProperty("events")[0];
        Assert.Equal(1, firstEvent.GetProperty("sequence").GetInt64());
        var cursor = firstJson.RootElement.GetProperty("nextCursor").GetString();

        using var second = await GetAsync(
            client,
            seeded.ProjectId,
            seeded.SessionId,
            $"after={Uri.EscapeDataString(cursor!)}&limit=100");
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondEvents = secondJson.RootElement.GetProperty("events");
        Assert.NotEmpty(secondEvents.EnumerateArray());
        Assert.All(secondEvents.EnumerateArray(), item =>
            Assert.True(item.GetProperty("sequence").GetInt64() > 1));
        Assert.Equal(
            secondEvents[secondEvents.GetArrayLength() - 1].GetProperty("sequence").GetInt64(),
            DecodeCursorPosition(seeded.ProjectId, seeded.SessionId, 1, secondJson.RootElement.GetProperty("nextCursor").GetString()!));

        var highWater = secondJson.RootElement.GetProperty("highWaterSequence").GetInt64();
        var highWaterCursor = await EncodeCursorAsync(seeded.ProjectId, seeded.SessionId, 1, highWater);
        using var empty = await GetAsync(
            client,
            seeded.ProjectId,
            seeded.SessionId,
            $"after={Uri.EscapeDataString(highWaterCursor)}");
        using var emptyJson = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
        Assert.Empty(emptyJson.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal(highWater, emptyJson.RootElement.GetProperty("highWaterSequence").GetInt64());
        Assert.Equal(
            highWater,
            DecodeCursorPosition(seeded.ProjectId, seeded.SessionId, 1, emptyJson.RootElement.GetProperty("nextCursor").GetString()!));
    }

    [Fact]
    public async Task LimitIsCappedAndPayloadsStayAllowlisted()
    {
        var seeded = await SeedStreamAsync(additionalEvents: 102, addContextReset: true);
        using var client = await CreateReaderAsync(seeded.ProjectId);

        using var response = await GetAsync(client, seeded.ProjectId, seeded.SessionId, "limit=500");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var events = body.RootElement.GetProperty("events");
        Assert.Equal(100, events.GetArrayLength());

        foreach (var item in events.EnumerateArray())
        {
            var names = item.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(
                new[] { "sequence", "cursor", "type", "occurredAt", "execution" },
                names);
            Assert.Equal(ExecutionKeys, item.GetProperty("execution").EnumerateObject().Select(property => property.Name));
        }

        var contextReset = events.EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("type").GetString() == PublicSessionEventTypes.ContextReset);
        Assert.Equal(JsonValueKind.Undefined, contextReset.ValueKind);

        using var all = await ReadAllEventsAsync(client, seeded);
        var reset = all.RootElement.GetProperty("events").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == PublicSessionEventTypes.ContextReset);
        Assert.Equal(
            SessionKeys,
            reset.GetProperty("session").EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("execution", reset.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("jobId", reset.GetProperty("session").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task TamperedCrossBoundAndWrongGenerationCursorsReturnCursorInvalid()
    {
        var seeded = await SeedStreamAsync();
        var other = await SeedStreamAsync();
        using var client = await CreateReaderAsync(seeded.ProjectId);
        var valid = await EncodeCursorAsync(seeded.ProjectId, seeded.SessionId, 1, 1);
        var tampered = valid[..^1] + (valid[^1] == 'A' ? 'B' : 'A');

        foreach (var cursor in new[] { tampered, await EncodeCursorAsync(seeded.ProjectId, other.SessionId, 1, 1) })
        {
            using var response = await GetAsync(
                client,
                seeded.ProjectId,
                seeded.SessionId,
                $"after={Uri.EscapeDataString(cursor)}");
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, DirectApiErrorCodes.CursorInvalid);
        }

        await UpdateStreamAsync(seeded.SessionId, state => state.ActiveGeneration = 2);
        using var oldGeneration = await GetAsync(
            client,
            seeded.ProjectId,
            seeded.SessionId,
            $"after={Uri.EscapeDataString(valid)}");
        await AssertErrorAsync(oldGeneration, HttpStatusCode.BadRequest, DirectApiErrorCodes.CursorInvalid);
    }

    [Fact]
    public async Task ExpiredAndClosedStreamsExposeOnlySafeBounds()
    {
        var seeded = await SeedStreamAsync(additionalEvents: 2);
        using var client = await CreateReaderAsync(seeded.ProjectId);
        await UpdateStreamAsync(seeded.SessionId, state => state.EarliestSequence = 3);
        var expiredCursor = await EncodeCursorAsync(seeded.ProjectId, seeded.SessionId, 1, 2);

        using var expired = await GetAsync(
            client,
            seeded.ProjectId,
            seeded.SessionId,
            $"after={Uri.EscapeDataString(expiredCursor)}");
        await AssertErrorAsync(expired, HttpStatusCode.Gone, DirectApiErrorCodes.CursorExpired);
        using var expiredBody = JsonDocument.Parse(await expired.Content.ReadAsStringAsync());
        Assert.Equal(3, expiredBody.RootElement.GetProperty("earliestSequence").GetInt64());
        Assert.Equal(4, expiredBody.RootElement.GetProperty("latestSequence").GetInt64());

        var currentCursor = await EncodeCursorAsync(seeded.ProjectId, seeded.SessionId, 1, 4);
        await UpdateStreamAsync(seeded.SessionId, state => state.Closed = true);
        using var noCursor = await GetAsync(client, seeded.ProjectId, seeded.SessionId);
        await AssertErrorAsync(noCursor, HttpStatusCode.NotFound, DirectApiErrorCodes.SessionNotFound);

        using var tombstone = await GetAsync(
            client,
            seeded.ProjectId,
            seeded.SessionId,
            $"after={Uri.EscapeDataString(currentCursor)}");
        await AssertErrorAsync(tombstone, HttpStatusCode.Gone, DirectApiErrorCodes.CursorExpired);
        using var tombstoneBody = JsonDocument.Parse(await tombstone.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, tombstoneBody.RootElement.GetProperty("earliestSequence").ValueKind);
        Assert.Equal(4, tombstoneBody.RootElement.GetProperty("latestSequence").GetInt64());

        await PurgeStreamAsync(seeded.SessionId);
        using var purged = await GetAsync(
            client,
            seeded.ProjectId,
            seeded.SessionId,
            $"after={Uri.EscapeDataString(currentCursor)}");
        await AssertErrorAsync(purged, HttpStatusCode.BadRequest, DirectApiErrorCodes.CursorInvalid);
    }

    [Fact]
    public async Task ProjectionLagReturns503WithoutServingTheJournal()
    {
        var seeded = await SeedStreamAsync();
        using var client = await CreateReaderAsync(seeded.ProjectId);
        await RewindSessionCheckpointAsync(seeded.SessionId);

        using var response = await GetAsync(client, seeded.ProjectId, seeded.SessionId);
        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, DirectApiErrorCodes.ProjectionLag);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"events\"", body, StringComparison.Ordinal);
    }

    private async Task<SeededStream> SeedStreamAsync(
        int additionalEvents = 0,
        bool addContextReset = false)
    {
        var projectId = $"direct-events-{Guid.NewGuid():N}";
        var sessionId = $"session-events-{Guid.NewGuid():N}";
        var agentId = $"agent-events-{Guid.NewGuid():N}";
        var now = fixture.TimeProvider.GetUtcNow().UtcDateTime;
        var observedAt = fixture.TimeProvider.GetUtcNow();
        var session = AgentSession.Create(
            sessionId,
            "runner-events",
            "/mohist-tests/work",
            new AgentSessionMetadata(Labels: new Dictionary<string, string>
            {
                ["mohist.io/project-id"] = projectId,
                ["mohist.io/source-kind"] = "agent-launch",
                ["mohist.io/agent-id"] = agentId,
            }),
            now,
            runtime: "opencode");
        session.Status = session.Status with
        {
            Activity = AgentSessionActivity.Active,
            Inputs = [new AgentSessionInputRecord(
                "input-events-1",
                1,
                "event test input",
                "direct-test",
                AgentSessionInputAcceptance.Accepted,
                now)],
            Turns = [new AgentTurnRecord(
                "turn-events-1",
                1,
                ["input-events-1"],
                AgentTurnStatus.Executing,
                null,
                null,
                now,
                now.AddSeconds(1))],
        };

        var totalEvents = 2 + additionalEvents + (addContextReset ? 1 : 0);
        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            RepositoriesJson = "[]",
            CreatedAt = observedAt,
            UpdatedAt = observedAt,
        });
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            State = JsonSerializer.Serialize(session, JSON.Options),
            RunnerId = "runner-events",
            AgentSessionId = sessionId,
            Status = "opened",
            CreatedAt = now,
            LastDataAt = now,
        });
        db.PublicStreamStates.Add(new PublicStreamStateRow
        {
            SessionId = sessionId,
            ActiveGeneration = 1,
            NextSequence = totalEvents + 1,
            EarliestSequence = 1,
            LatestSequence = totalEvents,
            CreatedAt = observedAt,
            UpdatedAt = observedAt,
        });
        for (var sequence = 1L; sequence <= totalEvents; sequence++)
        {
            var isContextReset = addContextReset && sequence == totalEvents;
            db.PublicSessionEvents.Add(new PublicSessionEventRow
            {
                SessionId = sessionId,
                Generation = 1,
                Sequence = sequence,
                Type = isContextReset
                    ? PublicSessionEventTypes.ContextReset
                    : sequence == 1
                        ? PublicSessionEventTypes.InputAccepted
                        : PublicSessionEventTypes.TurnRunning,
                OccurredAt = observedAt.UtcDateTime.ToString("O"),
                PayloadJson = isContextReset
                    ? JsonSerializer.Serialize(new PublicSessionEventPayload
                    {
                        ProjectId = projectId,
                        AgentId = agentId,
                        SessionId = sessionId,
                        SessionActivity = PublicExecutionFieldValues.SessionActive,
                        Admission = PublicExecutionFieldValues.AdmissionReady,
                        ReasonCode = PublicExecutionFieldValues.Reasons.ContextReset,
                    }, JSON.PublicApi)
                    : ExecutionPayload(projectId, agentId, sessionId, observedAt, sequence),
                SourceTransition = $"direct-test:{sequence}",
                RecordedAt = observedAt,
            });
        }
        db.PublicProjectionCheckpoints.Add(new PublicProjectionCheckpointRow
        {
            Feed = PublicProjectionFeeds.AgentSessions,
            SourceKey = sessionId,
            Watermark = PublicExecutionAggregator.StateDigest(JsonSerializer.Serialize(session, JSON.Options)),
            UpdatedAt = observedAt,
        });
        await db.SaveChangesAsync();
        return new SeededStream(projectId, sessionId);
    }

    private static string ExecutionPayload(
        string projectId,
        string agentId,
        string sessionId,
        DateTimeOffset observedAt,
        long sequence) =>
        JsonSerializer.Serialize(new PublicExecutionRead
        {
            ProjectId = projectId,
            AgentId = agentId,
            JobId = null,
            SessionId = sessionId,
            InputId = "input-events-1",
            TurnId = "turn-events-1",
            Status = PublicExecutionFieldValues.StatusRunning,
            JobStatus = null,
            SessionActivity = PublicExecutionFieldValues.SessionActive,
            Admission = PublicExecutionFieldValues.AdmissionReady,
            InputStatus = PublicExecutionFieldValues.InputAccepted,
            TurnStatus = PublicExecutionFieldValues.TurnRunning,
            Outcome = null,
            ReasonCode = null,
            Output = null,
            Error = null,
            AcceptedAt = observedAt,
            QueuedAt = observedAt,
            StartedAt = observedAt,
            TerminalAt = null,
            ObservedAt = observedAt,
            Sequence = sequence,
        }, JSON.PublicApi);

    private async Task<SeededStream> SeedStreamViaProjectorAsync(
        int additionalEvents = 0,
        bool addContextReset = false)
    {
        var projectId = $"direct-events-{Guid.NewGuid():N}";
        var sessionId = $"session-events-{Guid.NewGuid():N}";
        var agentId = $"agent-events-{Guid.NewGuid():N}";
        var now = fixture.TimeProvider.GetUtcNow().UtcDateTime;

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            db.Projects.Add(new ProjectRow
            {
                Id = projectId,
                Name = projectId,
                RepositoriesJson = "[]",
                CreatedAt = fixture.TimeProvider.GetUtcNow(),
                UpdatedAt = fixture.TimeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();

            var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
            var session = AgentSession.Create(
                sessionId,
                "runner-events",
                "/mohist-tests/work",
                new AgentSessionMetadata(Labels: new Dictionary<string, string>
                {
                    ["mohist.io/project-id"] = projectId,
                    ["mohist.io/source-kind"] = "agent-launch",
                    ["mohist.io/agent-id"] = agentId,
                }),
                now,
                runtime: "opencode");
            session.Status = session.Status with
            {
                Activity = AgentSessionActivity.Active,
                Inputs =
                [
                    new AgentSessionInputRecord(
                        "input-events-1",
                        1,
                        "event test input",
                        "direct-test",
                        AgentSessionInputAcceptance.Accepted,
                        now),
                ],
                Turns =
                [
                    new AgentTurnRecord(
                        "turn-events-1",
                        1,
                        ["input-events-1"],
                        AgentTurnStatus.Executing,
                        null,
                        null,
                        now,
                        now.AddSeconds(1)),
                ],
            };
            await sessions.SaveAsync(sessionId, session);
        }

        await TestWait.ForAsync(
            probe: async () =>
            {
                await using var db = await fixture.Services
                    .GetRequiredService<IDbContextFactory<MohistDbContext>>()
                    .CreateDbContextAsync();
                return await db.PublicStreamStates.AsNoTracking()
                    .AnyAsync(row => row.SessionId == sessionId);
            },
            isDone: exists => exists,
            timeout: TimeSpan.FromSeconds(30),
            step: TimeSpan.FromMilliseconds(20),
            description: "the hosted public projector to create the Session stream",
            advance: () => fixture.Client.GetAsync("/api/health"));

        await using (var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync())
        {
            var state = await db.PublicStreamStates.SingleAsync(row => row.SessionId == sessionId);
            var next = state.NextSequence;
            var projectEventCount = additionalEvents + (addContextReset ? 1 : 0);
            var observedAt = fixture.TimeProvider.GetUtcNow();
            for (var index = 0; index < projectEventCount; index++)
            {
                var sequence = next + index;
                var isContextReset = addContextReset && index == projectEventCount - 1;
                db.PublicSessionEvents.Add(new PublicSessionEventRow
                {
                    SessionId = sessionId,
                    Generation = state.ActiveGeneration,
                    Sequence = sequence,
                    Type = isContextReset ? PublicSessionEventTypes.ContextReset : PublicSessionEventTypes.InputAccepted,
                    OccurredAt = observedAt.UtcDateTime.ToString("O"),
                    PayloadJson = isContextReset
                        ? JsonSerializer.Serialize(new PublicSessionEventPayload
                        {
                            ProjectId = projectId,
                            AgentId = agentId,
                            SessionId = sessionId,
                            SessionActivity = PublicExecutionFieldValues.SessionActive,
                            Admission = PublicExecutionFieldValues.AdmissionReady,
                            ReasonCode = PublicExecutionFieldValues.Reasons.ContextReset,
                        }, JSON.PublicApi)
                        : JsonSerializer.Serialize(new PublicExecutionRead
                        {
                            ProjectId = projectId,
                            AgentId = agentId,
                            JobId = null,
                            SessionId = sessionId,
                            InputId = "input-events-1",
                            TurnId = "turn-events-1",
                            Status = PublicExecutionFieldValues.StatusRunning,
                            JobStatus = null,
                            SessionActivity = PublicExecutionFieldValues.SessionActive,
                            Admission = PublicExecutionFieldValues.AdmissionReady,
                            InputStatus = PublicExecutionFieldValues.InputAccepted,
                            TurnStatus = PublicExecutionFieldValues.TurnRunning,
                            Outcome = null,
                            ReasonCode = null,
                            Output = null,
                            Error = null,
                            AcceptedAt = observedAt,
                            QueuedAt = observedAt,
                            StartedAt = observedAt,
                            TerminalAt = null,
                            ObservedAt = observedAt,
                            Sequence = sequence,
                        }, JSON.PublicApi),
                    SourceTransition = $"direct-test:{sequence}",
                    RecordedAt = observedAt,
                });
            }

            if (projectEventCount > 0)
            {
                state.NextSequence += projectEventCount;
                state.LatestSequence = state.NextSequence - 1;
                state.UpdatedAt = observedAt;
            }

            await db.SaveChangesAsync();
        }

        return new SeededStream(projectId, sessionId);
    }

    private async Task<HttpClient> CreateReaderAsync(string projectId)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/api/auth/tokens", new
        {
            name = $"direct-events-{Guid.NewGuid():N}",
            scope = "readonly",
            projectIds = new[] { projectId },
        });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = body.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string projectId,
        string sessionId,
        string? query = null) =>
        await client.GetAsync($"/api/v1/projects/{projectId}/agent-sessions/{sessionId}/events{(query is null ? string.Empty : "?" + query)}");

    private async Task<JsonDocument> ReadAllEventsAsync(HttpClient client, SeededStream seeded)
    {
        using var first = await GetAsync(client, seeded.ProjectId, seeded.SessionId, "limit=100");
        first.EnsureSuccessStatusCode();
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var cursor = firstBody.RootElement.GetProperty("nextCursor").GetString()!;
        using var second = await GetAsync(
            client,
            seeded.ProjectId,
            seeded.SessionId,
            $"after={Uri.EscapeDataString(cursor)}&limit=100");
        second.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await second.Content.ReadAsStringAsync());
    }

    private async Task<string> EncodeCursorAsync(
        string projectId,
        string sessionId,
        long generation,
        long afterPosition)
    {
        var codec = fixture.Services.GetRequiredService<PublicSessionEventCursorCodec>();
        var signer = await codec.OpenAsync();
        return signer.Encode(new PublicSessionEventCursorPayload(
            projectId,
            sessionId,
            generation,
            afterPosition,
            PublicSessionEventCursorCodec.CurrentVersion));
    }

    private long DecodeCursorPosition(
        string projectId,
        string sessionId,
        long generation,
        string token)
    {
        var codec = fixture.Services.GetRequiredService<PublicSessionEventCursorCodec>();
        var signer = codec.OpenAsync().GetAwaiter().GetResult();
        Assert.True(signer.TryDecode(token, projectId, sessionId, out var payload));
        Assert.Equal(generation, payload!.Generation);
        return payload.AfterPosition;
    }

    private async Task UpdateStreamAsync(string sessionId, Action<PublicStreamStateRow> update)
    {
        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var state = await db.PublicStreamStates.SingleAsync(row => row.SessionId == sessionId);
        update(state);
        await db.SaveChangesAsync();
    }

    private async Task PurgeStreamAsync(string sessionId)
    {
        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var state = await db.PublicStreamStates.SingleAsync(row => row.SessionId == sessionId);
        db.PublicStreamStates.Remove(state);
        await db.SaveChangesAsync();
    }

    private async Task RewindSessionCheckpointAsync(string sessionId)
    {
        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var checkpoint = await db.PublicProjectionCheckpoints.SingleAsync(row =>
            row.Feed == PublicProjectionFeeds.AgentSessions
            && row.SourceKey == sessionId);
        checkpoint.Watermark = "stale";
        await db.SaveChangesAsync();
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed record SeededStream(string ProjectId, string SessionId);
}
