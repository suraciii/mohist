using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.DirectApi;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.DirectApi;

[Collection("IntegrationMisc")]
public sealed class DirectApiStopSpecs(MohistIntegrationFixture fixture)
{
    [Fact]
    public async Task NonEmptyBody_IsRejectedBeforeMappingOrStopEffect()
    {
        var projectId = await SeedProjectAsync();
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        var before = await MappingCountAsync();

        using var request = StopRequest(projectId, "missing-turn", "non-empty-body", "{}");
        using var response = await client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, DirectApiErrorCodes.InvalidRequest);
        Assert.Equal(before, await MappingCountAsync());
    }

    [Fact]
    public async Task TerminalTurn_IsDurableNoOpWithoutRunnerCallAndKeepsResponsePrivate()
    {
        var projectId = await SeedProjectAsync();
        var (sessionId, turnId) = await SeedSessionAsync(
            projectId,
            AgentTurnStatus.Completed,
            AgentSessionActivity.Idle);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        var hub = GetRunnerHub();
        hub.Clear();

        using var response = await PostUntilProjectedAsync(
            client,
            projectId,
            turnId,
            "terminal-no-op",
            "terminal");

        Assert.Equal("terminal", response.RootElement.GetProperty("status").GetString());
        Assert.Equal("completed", response.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(turnId, response.RootElement.GetProperty("turnId").GetString());
        Assert.Empty(hub.Invocations);
        AssertPublicObservation(response.RootElement);

        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var mapping = (await db.DirectApiIdempotencyMappings
            .Where(row => row.Command == DirectApiCommands.Stop)
            .ToListAsync())
            .Single(row => row.ScopeKey.EndsWith("|terminal-no-op", StringComparison.Ordinal));
        Assert.Equal(DirectApiMappingStates.Completed, mapping.State);
        Assert.False(string.IsNullOrWhiteSpace(mapping.FrozenTarget));
    }

    [Fact]
    public async Task QueuedTurn_IsCancelledLocallyWithoutRunnerCall()
    {
        var projectId = await SeedProjectAsync();
        var (sessionId, turnId) = await SeedSessionAsync(
            projectId,
            AgentTurnStatus.Queued,
            AgentSessionActivity.Active);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        var hub = GetRunnerHub();
        hub.Clear();

        using var response = await PostUntilProjectedAsync(
            client,
            projectId,
            turnId,
            "queued-local-cancel",
            "terminal");

        Assert.Equal("terminal", response.RootElement.GetProperty("status").GetString());
        Assert.Equal("cancelled", response.RootElement.GetProperty("outcome").GetString());
        Assert.Empty(hub.Invocations);
        Assert.Equal(
            AgentTurnStatus.Cancelled,
            Assert.Single(await fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).ListTurnsAsync()).Status);
    }

    [Fact]
    public async Task UnknownStopKeepsMappingPendingAndBlocksASecondKey()
    {
        var projectId = await SeedProjectAsync();
        var (sessionId, turnId) = await SeedSessionAsync(
            projectId,
            AgentTurnStatus.Executing,
            AgentSessionActivity.Active);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        var hub = GetRunnerHub();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("unknown"));

        using (var first = await client.SendAsync(StopRequest(projectId, turnId, "unknown-a")))
        {
            Assert.Contains(first.StatusCode, new[]
            {
                HttpStatusCode.OK,
                HttpStatusCode.ServiceUnavailable,
            });
        }

        using var second = await client.SendAsync(StopRequest(projectId, turnId, "unknown-b"));
        await AssertErrorAsync(second, HttpStatusCode.Conflict, DirectApiErrorCodes.StopOutcomeUnknown);
        Assert.Single(hub.Invocations);

        await using var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        Assert.Equal(1, await db.DirectApiIdempotencyMappings.CountAsync(row =>
            row.Command == DirectApiCommands.Stop
            && row.TurnId == turnId));
        var mapping = await db.DirectApiIdempotencyMappings.SingleAsync(row =>
            row.Command == DirectApiCommands.Stop && row.TurnId == turnId);
        Assert.Equal(DirectApiMappingStates.Pending, mapping.State);
        AssertFrozenTargetIsInternal(mapping.FrozenTarget!, turnId);

        var secondCaller = await CreatePatAsync(projectId);
        using var secondClient = DirectClient(secondCaller);
        using var sameKey = await secondClient.SendAsync(StopRequest(projectId, turnId, "unknown-a"));
        await AssertErrorAsync(sameKey, HttpStatusCode.Conflict, DirectApiErrorCodes.StopOutcomeUnknown);
        Assert.Equal(1, await db.DirectApiIdempotencyMappings.CountAsync(row => row.TurnId == turnId));

        // A later terminal fact resolves the fenced unknown lifecycle. The
        // next caller then creates its own mapping and classifies the current
        // terminal Turn without replaying the first caller's effect.
        var session = fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.MarkTurnTerminalAsync(turnId, AgentTurnStatus.Completed, null);
        using var ownMapping = await PostUntilProjectedAsync(
            secondClient,
            projectId,
            turnId,
            "unknown-a",
            "terminal");
        Assert.Equal(turnId, ownMapping.RootElement.GetProperty("turnId").GetString());

        var mappings = await db.DirectApiIdempotencyMappings
            .AsNoTracking()
            .Where(row => row.Command == DirectApiCommands.Stop && row.TurnId == turnId)
            .ToListAsync();
        Assert.Equal(2, mappings.Count);
        Assert.Equal(2, mappings.Select(row => row.CallerKeyId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(mappings, row => row.ScopeKey.EndsWith("|unknown-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MatchingRetryUsesTheFrozenOperationAndBinding()
    {
        var projectId = await SeedProjectAsync();
        var (sessionId, turnId) = await SeedSessionAsync(
            projectId,
            AgentTurnStatus.Executing,
            AgentSessionActivity.Active);
        var token = await CreatePatAsync(projectId);
        using var client = DirectClient(token);
        var hub = GetRunnerHub();
        hub.Clear();
        hub.SetInvocationResponse("CancelAgentSession", null);

        using (var first = await client.SendAsync(StopRequest(projectId, turnId, "frozen-retry")))
        {
            await AssertErrorAsync(first, HttpStatusCode.ServiceUnavailable, DirectApiErrorCodes.StopPending);
        }

        await using (var db = await fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync())
        {
            var mapping = await db.DirectApiIdempotencyMappings.SingleAsync(row =>
                row.Command == DirectApiCommands.Stop && row.TurnId == turnId);
            AssertFrozenTargetIsInternal(mapping.FrozenTarget!, turnId);
        }

        // A matching retry observes the existing fenced claim and does not
        // dispatch a replacement effect from the HTTP route.
        using (var retry = await client.SendAsync(StopRequest(projectId, turnId, "frozen-retry")))
        {
            await AssertErrorAsync(retry, HttpStatusCode.ServiceUnavailable, DirectApiErrorCodes.StopPending);
        }
        Assert.Single(hub.Invocations);

        // Canonical recovery owns the redelivery and keeps the original
        // operation identity. The direct mapping resolves on the next retry.
        hub.SetInvocationResponse("CancelAgentSession", new RunnerStopReply("not-cancellable"));
        var session = fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.RunStopRecoveryAsync();
        Assert.Equal(2, hub.Invocations.Count);
        var firstPayload = JsonSerializer.SerializeToElement(hub.Invocations[0].Arguments.Single());
        var recoveryPayload = JsonSerializer.SerializeToElement(hub.Invocations[1].Arguments.Single());
        Assert.Equal(
            firstPayload.GetProperty("operationId").GetString(),
            recoveryPayload.GetProperty("operationId").GetString());

        using var resolved = await client.SendAsync(StopRequest(projectId, turnId, "frozen-retry"));
        Assert.Contains(resolved.StatusCode, new[]
        {
            HttpStatusCode.OK,
            HttpStatusCode.ServiceUnavailable,
        });
        Assert.Equal("runtime-direct-stop", (await session.GetAsync())!.AgentSessionId);
    }

    private async Task<JsonDocument> PostUntilProjectedAsync(
        HttpClient client,
        string projectId,
        string turnId,
        string key,
        string? expectedStatus = null)
    {
        var body = await TestWait.ForAsync(
            probe: async () =>
            {
                using var response = await client.SendAsync(StopRequest(projectId, turnId, key));
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    return null;
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(body);
                if (expectedStatus is not null
                    && !string.Equals(
                        document.RootElement.GetProperty("status").GetString(),
                        expectedStatus,
                        StringComparison.Ordinal))
                {
                    return null;
                }

                return JsonDocument.Parse(body);
            },
            isDone: value => value is not null,
            timeout: TimeSpan.FromSeconds(30),
            step: TimeSpan.FromMilliseconds(20),
            description: "direct stop public observation to become projected",
            advance: () => fixture.Client.GetAsync("/api/health"));
        return body!;
    }

    private async Task<(string SessionId, string TurnId)> SeedSessionAsync(
        string projectId,
        AgentTurnStatus turnStatus,
        AgentSessionActivity activity)
    {
        var sessionId = $"direct-stop-session-{Guid.NewGuid():N}";
        var turnId = $"direct-stop-turn-{Guid.NewGuid():N}";
        var inputId = $"direct-stop-input-{Guid.NewGuid():N}";
        await using var scope = fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
        var now = fixture.TimeProvider.GetUtcNow().UtcDateTime;
        var session = AgentSession.Create(
            sessionId,
            "runner-direct-stop",
            "/mohist-tests/work",
            new AgentSessionMetadata(Labels: new Dictionary<string, string>
            {
                ["mohist.io/project-id"] = projectId,
                ["mohist.io/source-kind"] = "agent-launch",
                ["mohist.io/agent-id"] = "agent-direct-stop",
            }),
            now,
            runtime: "opencode");
        session.Status = session.Status with
        {
            Activity = activity,
            AgentRuntimeSessionId = "runtime-direct-stop",
            BoundAt = now,
            Inputs =
            [
                new AgentSessionInputRecord(
                    inputId,
                    1,
                    "stop test",
                    "direct-test",
                    AgentSessionInputAcceptance.Accepted,
                    now),
            ],
            Turns =
            [
                new AgentTurnRecord(
                    turnId,
                    1,
                    [inputId],
                    turnStatus,
                    RecordedAt: now,
                    UpdatedAt: now),
            ],
        };
        await store.SaveAsync(sessionId, session);
        fixture.Services.GetRequiredService<RunnerConnectionTracker>()
            .Register("runner-direct-stop", "direct-stop-connection");
        return (sessionId, turnId);
    }

    private HttpRequestMessage StopRequest(
        string projectId,
        string turnId,
        string key,
        string? body = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/projects/{projectId}/agent-turns/{turnId}/stop");
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private HttpClient DirectClient(string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private RecordingRunnerHubContext GetRunnerHub() =>
        fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
        ?? throw new InvalidOperationException("Recording runner hub context was not registered.");

    private async Task<string> SeedProjectAsync()
    {
        var projectId = $"direct-stop-project-{Guid.NewGuid():N}";
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

    private async Task<string> CreatePatAsync(string projectId)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/api/auth/tokens", new
        {
            name = $"direct-stop-{Guid.NewGuid():N}",
            scope = "operator",
            projectIds = new[] { projectId },
            allProjects = false,
        });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private async Task<int> MappingCountAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return await db.DirectApiIdempotencyMappings.CountAsync();
    }

    private static void AssertFrozenTargetIsInternal(string json, string turnId)
    {
        using var target = JsonDocument.Parse(json);
        var root = target.RootElement;
        Assert.Equal(turnId, root.GetProperty("turnId").GetString());
        Assert.True(root.TryGetProperty("turnRevision", out _));
        Assert.True(root.TryGetProperty("contextGeneration", out _));
        Assert.True(root.TryGetProperty("binding", out _));
        Assert.True(root.TryGetProperty("deadlineAt", out _));
        Assert.True(root.TryGetProperty("operationId", out _));
    }

    private static void AssertPublicObservation(JsonElement body)
    {
        var keys = body.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();
        Assert.Equal(
            new[]
            {
                "acceptedAt", "admission", "agentId", "error", "inputId", "inputStatus",
                "jobId", "jobStatus", "observedAt", "outcome", "output", "projectId",
                "queuedAt", "reasonCode", "sequence", "sessionActivity", "sessionId",
                "startedAt", "status", "terminalAt", "turnId", "turnStatus",
            },
            keys);
        Assert.DoesNotContain("operationId", keys);
        Assert.DoesNotContain("binding", keys);
        Assert.DoesNotContain("deadlineAt", keys);
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
