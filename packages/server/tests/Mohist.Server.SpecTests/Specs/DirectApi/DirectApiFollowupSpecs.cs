using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.DirectApi;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.DirectApi;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.PublicApi;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.DirectApi;

/// <summary>
/// The direct follow-up command resolves its target from the canonical
/// Session, then uses the shared durable mapping fence before admitting a
/// Session input. These tests pin replay, conflict, durable rejection, and
/// the write-scope pipeline for the follow-up route.
/// </summary>
[Collection("PublicProjectionIntegration")]
public sealed class DirectApiFollowupSpecs(PublicProjectionIntegrationFixture fixture)
{
    [Fact]
    public async Task MissingOrForeignSession_ReturnsSessionNotFoundWithoutMapping()
    {
        var projectId = await SeedProjectAsync();
        var foreignProjectId = await SeedProjectAsync();
        var foreignSessionId = $"session-foreign-{Guid.NewGuid():N}";
        await SeedSessionAsync(
            foreignSessionId,
            foreignProjectId,
            "agent-canonical",
            AgentSessionActivity.Active);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        var before = await MappingCountAsync();

        using (var missing = Request(
            projectId,
            $"session-missing-{Guid.NewGuid():N}",
            "followup-missing",
            "continue"))
        using (var response = await client.SendAsync(missing))
        {
            await AssertErrorAsync(response, HttpStatusCode.NotFound, DirectApiErrorCodes.SessionNotFound);
        }

        using (var foreign = Request(
            projectId,
            foreignSessionId,
            "followup-foreign",
            "continue"))
        using (var response = await client.SendAsync(foreign))
        {
            await AssertErrorAsync(response, HttpStatusCode.NotFound, DirectApiErrorCodes.SessionNotFound);
        }

        Assert.Equal(before, await MappingCountAsync());
    }

    [Fact]
    public async Task DerivedTargetFieldsInBody_AreRejectedBeforeMappingOrAdmission()
    {
        var projectId = await SeedProjectAsync();
        var sessionId = $"session-followup-body-{Guid.NewGuid():N}";
        await SeedSessionAsync(sessionId, projectId, "agent-canonical", AgentSessionActivity.Active);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        var before = await MappingCountAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/projects/{projectId}/agent-sessions/{sessionId}/inputs")
        {
            Content = new StringContent(
                $$"""{"text":"continue","projectId":"attacker-project","agentId":"attacker-agent"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("Idempotency-Key", "derived-target-body");
        using var response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, DirectApiErrorCodes.InvalidRequest);
        Assert.Equal(before, await MappingCountAsync());

        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var row = await db.AgentSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
        var session = AgentSessionJson.Deserialize(row)!;
        Assert.Empty(session.Status.Inputs ?? []);
    }

    [Fact]
    public async Task SessionDerivedTarget_ReplaysOneInputTurnPairAndReturnsNoJobId()
    {
        var projectId = await SeedProjectAsync();
        var sessionId = $"session-followup-{Guid.NewGuid():N}";
        var agentId = $"agent-derived-{Guid.NewGuid():N}";
        await SeedSessionAsync(sessionId, projectId, agentId, AgentSessionActivity.Active);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        const string key = "followup-replay";
        const string text = "continue the investigation";

        using var first = await PostObservationAsync(client, projectId, sessionId, key, text);
        var firstInputId = first.RootElement.GetProperty("inputId").GetString();
        var firstTurnId = first.RootElement.GetProperty("turnId").GetString();
        Assert.Null(first.RootElement.GetProperty("jobId").GetString());
        Assert.Equal(projectId, first.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(agentId, first.RootElement.GetProperty("agentId").GetString());
        Assert.NotNull(firstInputId);
        Assert.NotNull(firstTurnId);
        Assert.Equal(
            DirectApiWriteValidation.FollowupInputId(sessionId, key),
            firstInputId);
        Assert.Equal(
            DirectApiWriteValidation.FollowupTurnId(sessionId, key),
            firstTurnId);
        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var mapping = await db.DirectApiIdempotencyMappings.SingleAsync(row =>
            row.Command == DirectApiCommands.Followup
            && row.ScopeKey == $"{sessionId}|{key}");
        Assert.Equal(DirectApiMappingStates.Completed, mapping.State);
        Assert.Equal(
            DirectApiWriteValidation.FollowupFingerprint(sessionId, text),
            mapping.Fingerprint);
        var outcome = JsonDocument.Parse(mapping.Outcome!).RootElement;
        Assert.Equal(projectId, outcome.GetProperty("projectId").GetString());
        Assert.Equal(firstInputId, outcome.GetProperty("inputId").GetString());
        Assert.Equal(firstTurnId, outcome.GetProperty("turnId").GetString());
        using var snapshot = JsonDocument.Parse(outcome.GetProperty("snapshotJson").GetString()!);
        Assert.Equal(firstInputId, snapshot.RootElement.GetProperty("inputId").GetString());

        var row = await db.AgentSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
        var session = AgentSessionJson.Deserialize(row)!;
        Assert.Single(session.Status.Inputs!, input => input.Id == firstInputId);
        Assert.Single(session.Status.Turns!, turn => turn.Id == firstTurnId);
        Assert.Equal(1, await db.DirectApiIdempotencyMappings.CountAsync(item =>
            item.Command == DirectApiCommands.Followup
            && item.ScopeKey == $"{sessionId}|{key}"));
    }

    [Fact]
    public async Task CompletedFollowupReplaySurvivesSessionTargetInvalidation()
    {
        var projectId = await SeedProjectAsync();
        var sessionId = $"session-followup-replay-{Guid.NewGuid():N}";
        await SeedSessionAsync(sessionId, projectId, "agent-canonical", AgentSessionActivity.Active);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        const string key = "followup-replay-after-invalidation";
        const string text = "continue after target invalidation";

        using var first = await PostObservationAsync(client, projectId, sessionId, key, text);
        var originalInputId = first.RootElement.GetProperty("inputId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(originalInputId));

        await InvalidateSessionTargetAsync(sessionId);
        // The invalidation is a direct durable write that bypasses the
        // canonical write path's projector nudge; wake the projector so
        // the replayed observation converges instead of racing the hourly
        // fixture sweep (see PublicExecutionReadRouteSpecs).
        await ProjectSessionAsync(sessionId);

        using var replayBody = await PostObservationAsync(client, projectId, sessionId, key, text);
        Assert.Equal(originalInputId, replayBody.RootElement.GetProperty("inputId").GetString());
    }

    [Fact]
    public async Task ExistingFollowupMappingCannotBeReplayedThroughAnotherProject()
    {
        var ownerProjectId = await SeedProjectAsync();
        var attackerProjectId = await SeedProjectAsync();
        var sessionId = $"session-followup-cross-project-{Guid.NewGuid():N}";
        var token = await CreatePatForProjectsAsync(ownerProjectId, attackerProjectId);
        using var client = DirectClient(token);
        const string key = "followup-cross-project";
        const string text = "must remain project scoped";
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var setupDb = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            setupDb.DirectApiIdempotencyMappings.Add(new DirectApiIdempotencyMappingRow
            {
                Command = DirectApiCommands.Followup,
                ScopeKey = $"{sessionId}|{key}",
                CallerKeyId = "owner-caller",
                Fingerprint = DirectApiWriteValidation.FollowupFingerprint(sessionId, text),
                State = DirectApiMappingStates.Completed,
                Outcome = JSON.Serialize(new DirectApiFollowupOutcome(
                    ownerProjectId,
                    sessionId,
                    "agent-canonical",
                    "input-owner",
                    "turn-owner")),
                CreatedAt = fixture.TimeProvider.GetUtcNow(),
                CompletedAt = fixture.TimeProvider.GetUtcNow(),
            });
            await setupDb.SaveChangesAsync();
        }

        using var replay = Request(attackerProjectId, sessionId, key, text);
        using var response = await client.SendAsync(replay);
        await AssertErrorAsync(response, HttpStatusCode.NotFound, DirectApiErrorCodes.SessionNotFound);

        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var mapping = await db.DirectApiIdempotencyMappings.SingleAsync(row =>
            row.Command == DirectApiCommands.Followup
            && row.ScopeKey == $"{sessionId}|{key}");
        Assert.Equal(DirectApiMappingStates.Completed, mapping.State);
    }

    [Fact]
    public async Task SameKeyWithDifferentText_IsAStableConflictWithoutAnotherInput()
    {
        var projectId = await SeedProjectAsync();
        var sessionId = $"session-followup-conflict-{Guid.NewGuid():N}";
        await SeedSessionAsync(sessionId, projectId, "agent-canonical", AgentSessionActivity.Active);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        const string key = "followup-conflict";

        using (var first = await PostObservationAsync(client, projectId, sessionId, key, "text A"))
        {
            Assert.Equal("accepted", first.RootElement.GetProperty("inputStatus").GetString());
        }

        using (var conflict = Request(projectId, sessionId, key, "text B"))
        using (var response = await client.SendAsync(conflict))
        {
            await AssertErrorAsync(response, HttpStatusCode.Conflict, DirectApiErrorCodes.IdempotencyKeyReused);
        }

        using (var repeatedConflict = Request(projectId, sessionId, key, "text B"))
        using (var response = await client.SendAsync(repeatedConflict))
        {
            await AssertErrorAsync(response, HttpStatusCode.Conflict, DirectApiErrorCodes.IdempotencyKeyReused);
        }

        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var row = await db.AgentSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
        var session = AgentSessionJson.Deserialize(row)!;
        Assert.Single(session.Status.Inputs!, input => input.Text == "text A");
        Assert.DoesNotContain(session.Status.Inputs!, input => input.Text == "text B");
        Assert.Equal(1, await db.DirectApiIdempotencyMappings.CountAsync(item =>
            item.Command == DirectApiCommands.Followup
            && item.ScopeKey == $"{sessionId}|{key}"));
    }

    [Fact]
    public async Task CapacityRejection_IsDurableWithNullInputAndTurnIds()
    {
        var projectId = await SeedProjectAsync();
        var sessionId = $"session-followup-rejected-{Guid.NewGuid():N}";
        await SeedSessionWithQueuedFollowupsAsync(sessionId, projectId, "agent-canonical");
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        const string key = "followup-capacity";
        const string body = "wait for capacity";

        string firstBody;
        using (var first = Request(projectId, sessionId, key, body))
        using (var response = await client.SendAsync(first))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            firstBody = await response.Content.ReadAsStringAsync();
        }

        using (var firstJson = JsonDocument.Parse(firstBody))
        {
            Assert.Equal("terminal", firstJson.RootElement.GetProperty("status").GetString());
            Assert.Equal("rejected", firstJson.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("queue_full", firstJson.RootElement.GetProperty("reasonCode").GetString());
            Assert.Null(firstJson.RootElement.GetProperty("jobId").GetString());
            Assert.Null(firstJson.RootElement.GetProperty("inputId").GetString());
            Assert.Null(firstJson.RootElement.GetProperty("turnId").GetString());
        }

        using (var replay = Request(projectId, sessionId, key, body))
        using (var response = await client.SendAsync(replay))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(firstBody, await response.Content.ReadAsStringAsync());
        }

        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        Assert.Equal(1, await db.DirectApiIdempotencyMappings.CountAsync(item =>
            item.Command == DirectApiCommands.Followup
            && item.ScopeKey == $"{sessionId}|{key}"));
        var row = await db.AgentSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
        var session = AgentSessionJson.Deserialize(row)!;
        Assert.DoesNotContain(session.Status.Inputs!, input => input.Text == body);
    }

    [Fact]
    public async Task ReadonlyPat_IsForbiddenBeforeFollowupBodyOrMappingWork()
    {
        var projectId = await SeedProjectAsync();
        var sessionId = $"session-followup-readonly-{Guid.NewGuid():N}";
        await SeedSessionAsync(sessionId, projectId, "agent-canonical", AgentSessionActivity.Active);
        var token = await CreatePatAsync(projectId, "readonly");
        using var client = DirectClient(token);
        var before = await MappingCountAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/projects/{projectId}/agent-sessions/{sessionId}/inputs")
        {
            Content = new StringContent("{ not valid json", Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, DirectApiErrorCodes.Forbidden);
        Assert.Equal(before, await MappingCountAsync());
    }

    [Fact]
    public async Task InProgressAdmission_RemainsPendingAndRetryable()
    {
        var projectId = await SeedProjectAsync();
        var sessionId = $"session-followup-pending-{Guid.NewGuid():N}";
        await SeedSessionWithPendingFollowupAsync(sessionId, projectId, "agent-canonical");
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        const string key = "followup-pending";
        const string text = "wait for the current admission";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = Request(projectId, sessionId, key, text);
            using var response = await client.SendAsync(request);
            await AssertErrorAsync(
                response,
                HttpStatusCode.ServiceUnavailable,
                DirectApiErrorCodes.FollowupPending);
        }

        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var mapping = await db.DirectApiIdempotencyMappings.SingleAsync(row =>
            row.Command == DirectApiCommands.Followup
            && row.ScopeKey == $"{sessionId}|{key}");
        Assert.Equal(DirectApiMappingStates.Pending, mapping.State);
        using var outcome = JsonDocument.Parse(mapping.Outcome!);
        Assert.Equal(
            DirectApiWriteValidation.FollowupInputId(sessionId, key),
            outcome.RootElement.GetProperty("inputId").GetString());

        var row = await db.AgentSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
        var session = AgentSessionJson.Deserialize(row)!;
        Assert.DoesNotContain(session.Status.Inputs ?? [], input => input.Text == text);
        Assert.DoesNotContain(session.Status.Turns ?? [], turn => turn.Id == DirectApiWriteValidation.FollowupTurnId(sessionId, key));
    }

    private async Task<JsonDocument> PostObservationAsync(
        HttpClient client,
        string projectId,
        string sessionId,
        string key,
        string text)
    {
        await ProjectSessionAsync(sessionId);
        using (var submitted = await SendAsync(client, projectId, sessionId, key, text))
        {
            if (submitted.StatusCode == HttpStatusCode.OK)
                return JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());

            Assert.True(
                submitted.StatusCode == HttpStatusCode.ServiceUnavailable,
                $"Follow-up submission answered {submitted.StatusCode}: {await submitted.Content.ReadAsStringAsync()}");
            using var error = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
            Assert.Equal(
                DirectApiErrorCodes.ProjectionLag,
                error.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        await ProjectSessionAsync(sessionId);
        // The replay is meaningful only after the exact projection is readable;
        // do not let the disabled background projector define this boundary.
        var observation = await fixture.Services
            .GetRequiredService<PublicExecutionReadQuerier>()
            .ReadInputAsync(projectId, DirectApiWriteValidation.FollowupInputId(sessionId, key));
        Assert.Equal(PublicReadStatus.Found, observation.Status);
        using var response = await SendAsync(client, projectId, sessionId, key, text);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Follow-up observation answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string projectId,
        string sessionId,
        string key,
        string text)
    {
        using var request = Request(projectId, sessionId, key, text);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Request(
        string projectId,
        string sessionId,
        string key,
        string text)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/projects/{projectId}/agent-sessions/{sessionId}/inputs")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { text }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private HttpClient DirectClient(string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> CreatePatAsync(string projectId, string scope = "operator")
        => await DirectApiCredentialTestSupport.CreatePatAsync(
            fixture, "direct-followup", [projectId], scope);

    private async Task<string> CreatePatForProjectsAsync(params string[] projectIds)
        => await DirectApiCredentialTestSupport.CreatePatAsync(
            fixture, "direct-followup-multi-project", projectIds);

    private async Task<string> SeedProjectAsync()
    {
        var projectId = $"direct-followup-{Guid.NewGuid():N}";
        await using var scope = fixture.Services.CreateAsyncScope();
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
        return projectId;
    }

    private async Task SeedSessionAsync(
        string sessionId,
        string projectId,
        string agentId,
        AgentSessionActivity activity)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
        var now = fixture.TimeProvider.GetUtcNow().UtcDateTime;
        var session = AgentSession.Create(
            sessionId,
            "runner-followup",
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
            Activity = activity,
            AgentRuntimeSessionId = "runtime-followup",
            BoundAt = now,
        };
        await sessions.SaveAsync(session.Id, session);
    }

    private async Task SeedSessionWithPendingFollowupAsync(
        string sessionId,
        string projectId,
        string agentId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
        var now = fixture.TimeProvider.GetUtcNow().UtcDateTime;
        var session = AgentSession.Create(
            sessionId,
            "runner-followup",
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
            AgentRuntimeSessionId = "runtime-followup",
            BoundAt = now,
            PendingFollowup = new AgentSessionFollowupLease(
                OperationId: "already-admitting",
                RuntimeSessionId: "runtime-followup",
                StartedAt: now),
        };
        await sessions.SaveAsync(session.Id, session);
    }

    private async Task SeedSessionWithQueuedFollowupsAsync(
        string sessionId,
        string projectId,
        string agentId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
        var now = fixture.TimeProvider.GetUtcNow().UtcDateTime;
        var session = AgentSession.Create(
            sessionId,
            "runner-followup",
            "/mohist-tests/work",
            new AgentSessionMetadata(Labels: new Dictionary<string, string>
            {
                ["mohist.io/project-id"] = projectId,
                ["mohist.io/source-kind"] = "agent-launch",
                ["mohist.io/agent-id"] = agentId,
            }),
            now,
            runtime: "opencode");
        var inputs = Enumerable.Range(1, 16)
            .Select(index => new AgentSessionInputRecord(
                Id: $"input-capacity-{index}-{Guid.NewGuid():N}",
                Sequence: index,
                Text: $"queued {index}",
                Source: "agent-session-followup",
                Acceptance: AgentSessionInputAcceptance.Accepted,
                RecordedAt: now,
                JobId: null,
                IdempotencyKey: $"capacity-{index}"))
            .ToArray();
        var turns = inputs.Select((input, index) => new AgentTurnRecord(
            Id: $"turn-capacity-{index + 1}-{Guid.NewGuid():N}",
            Sequence: index + 1,
            InputIds: [input.Id],
            Status: AgentTurnStatus.Queued,
            JobId: null,
            Result: null,
            RecordedAt: now,
            UpdatedAt: now)).ToArray();
        session.Status = session.Status with
        {
            Activity = AgentSessionActivity.Active,
            AgentRuntimeSessionId = "runtime-followup",
            BoundAt = now,
            Inputs = inputs,
            Turns = turns,
        };
        await sessions.SaveAsync(session.Id, session);
    }

    private async Task InvalidateSessionTargetAsync(string sessionId)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.AgentSessions.SingleAsync(session => session.Id == sessionId);
        var session = AgentSessionJson.Deserialize(row)
            ?? throw new InvalidOperationException("The seeded Session state is unreadable.");
        session.Metadata = session.Metadata.WithLabel("mohist.io/source-kind", "invalidated");
        row.State = JSON.Serialize(session);
        await db.SaveChangesAsync();
    }

    private Task ProjectSessionAsync(string sessionId) =>
        fixture.ProjectSessionAsync(sessionId);

    private async Task<int> MappingCountAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return await db.DirectApiIdempotencyMappings.CountAsync();
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
}
