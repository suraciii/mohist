using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class AgentSessionRecoveryApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly string _runnerId = $"recovery-api-{Guid.NewGuid():N}";

    public AgentSessionRecoveryApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;

        var runnerHub = fixture.Services.GetRequiredService<RecordingRunnerHubContext>();
        runnerHub.Clear();
        runnerHub.SetInvocationResponseFactory("SessionCommand", arguments =>
        {
            var request = Assert.IsType<SessionCommandRequest>(Assert.Single(arguments));
            return request.Command switch
            {
                SessionCommandKind.Compact => new SessionCommandResult(Ok: true),
                SessionCommandKind.Reset => new SessionCommandResult(
                    Ok: true,
                    RuntimeSessionId: $"{request.RuntimeSessionId ?? "new"}-replacement"),
                _ => new SessionCommandResult(Ok: false, Error: SessionCommandError.Unavailable),
            };
        });
        fixture.Services.GetRequiredService<RunnerConnectionTracker>()
            .Register(_runnerId, $"connection-{_runnerId}");
    }

    [Fact]
    public async Task CompactEndpoint_InactiveSession_ReturnsStableSessionIdOnly()
    {
        var (project, issue, work, currentSession) = await CreateAndStartSessionAsync("compact-inactive", sessionName: "plan", attachIdle: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(currentSession.Id, data.GetProperty("id").GetString());
        Assert.False(data.TryGetProperty("agentSessionId", out _));
        Assert.Equal("compact", data.GetProperty("operation").GetString());
        Assert.True(data.GetProperty("wasCompacted").GetBoolean());

        var request = AssertSingleSessionCommandInvocation();
        Assert.Equal(SessionCommandKind.Compact, request.Command);
        Assert.Equal(currentSession.Id, request.SessionId);
        Assert.Equal("opencode", request.Runtime);
        Assert.Equal(currentSession.Id, request.RuntimeSessionId);
        Assert.Equal(_runnerId, request.RunnerId);
        Assert.Equal($"/workspaces/{project.Id}", request.WorkDir);
        Assert.Null(request.ExpectedRuntimeSessionId);

        var persisted = await _fixture.Grains.GetGrain<IAgentSessionGrain>(currentSession.Id).GetAsync();
        Assert.Equal(currentSession.Id, persisted?.AgentSessionId);
    }

    [Fact]
    public async Task CompactEndpoint_ActiveSession_ReturnsConflict()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("compact-active", sessionName: "plan", attachAndStart: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session_active", doc.RootElement.GetProperty("code").GetString());
        Assert.Contains("active", doc.RootElement.GetProperty("error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(currentSession.Id, doc.RootElement.GetProperty("details").GetProperty("sessionId").GetString());
        Assert.Empty(RunnerHub.Invocations);
    }

    [Fact]
    public async Task CompactEndpoint_NonexistentSession_ReturnsNotFound()
    {
        var (project, issue) = await CreateProjectAndIssueAsync("compact-not-found");

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/does-not-exist/compact", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResetEndpoint_InactiveSession_ReturnsStableSessionIdOnly()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("reset-inactive", sessionName: "build", attachIdle: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(currentSession.Id, data.GetProperty("id").GetString());
        Assert.False(data.TryGetProperty("agentSessionId", out _));
        Assert.Equal("reset", data.GetProperty("operation").GetString());
        Assert.False(data.GetProperty("wasCompacted").GetBoolean());

        var request = AssertSingleSessionCommandInvocation();
        Assert.Equal(SessionCommandKind.Reset, request.Command);
        Assert.Equal(currentSession.Id, request.SessionId);
        Assert.Equal(currentSession.Id, request.RuntimeSessionId);
        Assert.Equal(currentSession.Id, request.ExpectedRuntimeSessionId);

        var persisted = await _fixture.Grains.GetGrain<IAgentSessionGrain>(currentSession.Id).GetAsync();
        Assert.Equal($"{currentSession.Id}-replacement", persisted?.AgentSessionId);
    }

    [Fact]
    public async Task ResetEndpoint_MissingReplacementRuntimeSessionId_ReturnsInvalidRunnerResponseWithoutRebinding()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("reset-invalid-result", sessionName: "build", attachIdle: true);
        RunnerHub.SetInvocationResponse("SessionCommand", new SessionCommandResult(Ok: true));

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset", content: null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("runner_invalid_response", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(currentSession.Id, (await _fixture.Grains.GetGrain<IAgentSessionGrain>(currentSession.Id).GetAsync())?.AgentSessionId);
    }

    [Theory]
    [InlineData("compact", false)]
    [InlineData("compact", true)]
    [InlineData("reset", false)]
    [InlineData("reset", true)]
    public async Task RecoveryEndpoint_AmbiguousRunnerResult_ReusesThePersistedOperation(string operation, bool malformed)
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync(
            $"{operation}-ambiguous-{malformed}",
            sessionName: "build",
            attachIdle: true);
        var requests = new List<SessionCommandRequest>();
        RunnerHub.SetInvocationResponseFactory("SessionCommand", arguments =>
        {
            var request = Assert.IsType<SessionCommandRequest>(Assert.Single(arguments));
            requests.Add(request);
            if (requests.Count == 1)
            {
                if (!malformed)
                    return new SessionCommandResult(Ok: false, Error: SessionCommandError.Unavailable);
                return request.Command == SessionCommandKind.Compact
                    ? new SessionCommandResult(Ok: true, RuntimeSessionId: "unexpected")
                    : new SessionCommandResult(Ok: true);
            }

            return request.Command == SessionCommandKind.Compact
                ? new SessionCommandResult(Ok: true)
                : new SessionCommandResult(Ok: true, RuntimeSessionId: $"{request.RuntimeSessionId}-replacement");
        });

        using var first = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/{operation}",
            content: null);
        Assert.Equal(malformed ? HttpStatusCode.BadGateway : HttpStatusCode.ServiceUnavailable, first.StatusCode);

        using var retry = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/{operation}",
            content: null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].OperationId, requests[1].OperationId);

        var state = await _fixture.Grains.GetGrain<IAgentSessionGrain>(currentSession.Id).GetAsync();
        Assert.Equal(operation == "compact" ? currentSession.Id : $"{currentSession.Id}-replacement", state?.AgentSessionId);
    }

    [Fact]
    public async Task ResetEndpoint_NewIdempotencyKeyJoinsPendingOperationAndReplaysItsResult()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync(
            "reset-join-pending-key",
            sessionName: "build",
            attachIdle: true);
        var requests = new List<SessionCommandRequest>();
        RunnerHub.SetInvocationResponseFactory("SessionCommand", arguments =>
        {
            var request = Assert.IsType<SessionCommandRequest>(Assert.Single(arguments));
            requests.Add(request);
            return requests.Count == 1
                ? new SessionCommandResult(Ok: false, Error: SessionCommandError.Unavailable)
                : new SessionCommandResult(Ok: true, RuntimeSessionId: $"{request.RuntimeSessionId}-replacement");
        });

        using var firstRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset");
        firstRequest.Headers.Add("Idempotency-Key", "reset-1");
        using var first = await _client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);

        using var joinedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset");
        joinedRequest.Headers.Add("Idempotency-Key", "reset-2");
        using var joined = await _client.SendAsync(joinedRequest);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        Assert.Equal(requests[0].OperationId, requests[1].OperationId);

        using var replayRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset");
        replayRequest.Headers.Add("Idempotency-Key", "reset-2");
        using var replay = await _client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(2, requests.Count);
        Assert.Equal($"{currentSession.Id}-replacement", (await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(currentSession.Id)
            .GetAsync())?.AgentSessionId);
    }

    [Fact]
    public async Task ResetEndpoint_CommandNotStartedAllowsANewOperation()
    {
        var (project, issue, _, _) = await CreateAndStartSessionAsync(
            "reset-not-started",
            sessionName: "build",
            attachIdle: true);
        var requests = new List<SessionCommandRequest>();
        RunnerHub.SetInvocationResponseFactory("SessionCommand", arguments =>
        {
            var request = Assert.IsType<SessionCommandRequest>(Assert.Single(arguments));
            requests.Add(request);
            return requests.Count == 1
                ? new SessionCommandResult(Ok: false, Error: SessionCommandError.NotStarted)
                : new SessionCommandResult(Ok: true, RuntimeSessionId: $"{request.RuntimeSessionId}-replacement");
        });

        using var first = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset",
            content: null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);

        using var retry = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset",
            content: null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.NotEqual(requests[0].OperationId, requests[1].OperationId);
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task RecoveryEndpoint_TimedOutRunnerResponse_RetriesThePersistedOperation(string operation)
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync(
            $"{operation}-timeout-retry",
            sessionName: "build",
            attachIdle: true);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedResult = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<SessionCommandRequest>();
        RunnerHub.SetInvocationResponseFactory("SessionCommand", arguments =>
        {
            var request = Assert.IsType<SessionCommandRequest>(Assert.Single(arguments));
            requests.Add(request);
            if (requests.Count == 1)
            {
                dispatched.TrySetResult();
                return delayedResult.Task;
            }

            return request.Command == SessionCommandKind.Compact
                ? new SessionCommandResult(Ok: true)
                : new SessionCommandResult(Ok: true, RuntimeSessionId: $"{request.RuntimeSessionId}-replacement");
        });

        var first = _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/{operation}",
            content: null);
        await dispatched.Task;
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(15));

        using var timedOut = await first;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);

        using var retry = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/{operation}",
            content: null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].OperationId, requests[1].OperationId);

        var state = await LoadSessionStateAsync(currentSession.Id);
        Assert.Equal(operation == "compact" ? 1 : 2, state.Status.RuntimeSessionLineage?.Count);
        Assert.Equal(operation == "compact" ? currentSession.Id : $"{currentSession.Id}-replacement", state.Status.AgentRuntimeSessionId);
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task RecoveryEndpoint_CancelledRequest_RetriesThePersistedOperation(string operation)
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync(
            $"{operation}-cancel-retry",
            sessionName: "build",
            attachIdle: true);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedResult = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<SessionCommandRequest>();
        RunnerHub.SetInvocationResponseFactory("SessionCommand", arguments =>
        {
            var request = Assert.IsType<SessionCommandRequest>(Assert.Single(arguments));
            requests.Add(request);
            if (requests.Count == 1)
            {
                dispatched.TrySetResult();
                return delayedResult.Task;
            }

            return request.Command == SessionCommandKind.Compact
                ? new SessionCommandResult(Ok: true)
                : new SessionCommandResult(Ok: true, RuntimeSessionId: $"{request.RuntimeSessionId}-replacement");
        });

        using var cancellation = new CancellationTokenSource();
        var first = _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/{operation}",
            content: null,
            cancellation.Token);
        await dispatched.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        using var retry = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/{operation}",
            content: null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].OperationId, requests[1].OperationId);

        var state = await LoadSessionStateAsync(currentSession.Id);
        Assert.Equal(operation == "compact" ? 1 : 2, state.Status.RuntimeSessionLineage?.Count);
        Assert.Equal(operation == "compact" ? currentSession.Id : $"{currentSession.Id}-replacement", state.Status.AgentRuntimeSessionId);
    }

    [Fact]
    public async Task RuntimeEventsEndpoint_IgnoresOldPhysicalBindingAfterReset()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("stale-runtime-events", sessionName: "build", attachIdle: true);
        using var reset = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset", content: null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(currentSession.Id);
        var afterReset = await grain.GetAsync();
        Assert.NotNull(afterReset);
        Assert.NotEqual(currentSession.Id, afterReset!.AgentSessionId);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        using var staleEvent = await _client.PostAsJsonAsync(RunnerAgentSessionRuntimeEventsPath(currentSession), new
        {
            runtimeSessionId = currentSession.Id,
            runtimeEvents = new[] { new { type = "session.closed", payload = new { status = "completed" } } },
        });

        Assert.Equal(HttpStatusCode.OK, staleEvent.StatusCode);
        var afterStaleEvent = await grain.GetAsync();
        Assert.Equal(afterReset.AgentSessionId, afterStaleEvent?.AgentSessionId);
        Assert.Equal(afterReset.Status, afterStaleEvent?.Status);
        Assert.Equal(afterReset.LastDataAt, afterStaleEvent?.LastDataAt);

        await grain.FlushForTestAsync();
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var closedParts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Join(
                db.AgentSessionTranscriptTurns.AsNoTracking().Where(turn => turn.SessionId == currentSession.Id),
                part => part.TurnId,
                turn => turn.Id,
                (part, _) => part)
            .Where(part => part.Type == "session.closed")
            .ToListAsync();
        Assert.Empty(closedParts);
    }

    [Fact]
    public async Task CompactEndpoint_WhileResetDispatchIsInFlight_ReturnsRecoveryInProgressWithoutMutation()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync(
            "recovery-overlap",
            sessionName: "build",
            attachIdle: true);
        var resetStarted = new TaskCompletionSource<SessionCommandRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeReset = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunnerHub.SetInvocationResponseFactory("SessionCommand", arguments =>
        {
            var request = Assert.IsType<SessionCommandRequest>(Assert.Single(arguments));
            if (request.Command != SessionCommandKind.Reset)
                return new SessionCommandResult(Ok: false, Error: SessionCommandError.Unavailable);

            resetStarted.TrySetResult(request);
            return completeReset.Task;
        });

        var resetTask = _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset",
            content: null);
        var resetRequest = await resetStarted.Task;

        using var compact = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/compact",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, compact.StatusCode);
        using var compactBody = JsonDocument.Parse(await compact.Content.ReadAsStringAsync());
        Assert.Equal("recovery_in_progress", compactBody.RootElement.GetProperty("code").GetString());
        Assert.Equal(currentSession.Id, compactBody.RootElement.GetProperty("details").GetProperty("sessionId").GetString());
        Assert.Equal("reset", compactBody.RootElement.GetProperty("details").GetProperty("operation").GetString());
        Assert.Equal(currentSession.Id, (await _fixture.Grains.GetGrain<IAgentSessionGrain>(currentSession.Id).GetAsync())?.AgentSessionId);
        Assert.Single(RunnerHub.Invocations);

        completeReset.SetResult(new SessionCommandResult(
            Ok: true,
            RuntimeSessionId: $"{resetRequest.RuntimeSessionId}-replacement"));
        using var reset = await resetTask;

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal($"{currentSession.Id}-replacement", (await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(currentSession.Id)
            .GetAsync())?.AgentSessionId);
    }

    [Fact]
    public async Task CompactEndpoint_HandlerConflict_ReturnsIdleBoundaryConflict()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync(
            "compact-handler-conflict",
            sessionName: "plan",
            attachIdle: true);
        RunnerHub.SetInvocationResponse(
            "SessionCommand",
            new SessionCommandResult(Ok: false, Error: SessionCommandError.Conflict));

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("session_active", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(currentSession.Id, doc.RootElement.GetProperty("details").GetProperty("sessionId").GetString());
        Assert.Equal(SessionCommandKind.Compact, AssertSingleSessionCommandInvocation().Command);
        Assert.Equal(currentSession.Id, (await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(currentSession.Id)
            .GetAsync())?.AgentSessionId);
    }

    [Fact]
    public async Task ResetEndpoint_HandlerMissing_ReturnsRuntimeSessionMissingWithResetHint()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync(
            "reset-handler-missing",
            sessionName: "build",
            attachIdle: true);
        RunnerHub.SetInvocationResponse(
            "SessionCommand",
            new SessionCommandResult(Ok: false, Error: SessionCommandError.Missing));

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("runtime_session_missing", doc.RootElement.GetProperty("code").GetString());
        var details = doc.RootElement.GetProperty("details");
        Assert.Equal(currentSession.Id, details.GetProperty("sessionId").GetString());
        Assert.Equal("reset", details.GetProperty("hint").GetString());
        Assert.Contains("Reset", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(currentSession.Id, (await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(currentSession.Id)
            .GetAsync())?.AgentSessionId);
    }

    [Fact]
    public async Task ResetEndpoint_ActiveSession_ReturnsConflict()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("reset-active", sessionName: "build", attachAndStart: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session_active", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(currentSession.Id, doc.RootElement.GetProperty("details").GetProperty("sessionId").GetString());
        Assert.Empty(RunnerHub.Invocations);
    }

    [Fact]
    public async Task ResetEndpoint_NonexistentSession_ReturnsNotFound()
    {
        var (project, issue) = await CreateProjectAndIssueAsync("reset-not-found");

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/does-not-exist/reset", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("compact", true)]
    [InlineData("reset", false)]
    public async Task RecoveryEndpoints_BothSourcesShareCanonicalRouting(
        string operation,
        bool wasCompacted)
    {
        var (project, issue, _, workflowSession) = await CreateAndStartSessionAsync(
            $"{operation}-source-parity",
            sessionName: "plan",
            attachIdle: true);
        var agentSession = await CreateAgentLaunchSessionAsync(
            project,
            $"{operation}-source-parity",
            attach: true,
            idle: true);
        Assert.Equal("inactive", agentSession.Status);

        using var agentResponse = await _client.PostAsync(
            $"/api/projects/{project.Id}/agent-sessions/{agentSession.Id}/{operation}",
            content: null);
        using var workflowResponse = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/{operation}",
            content: null);

        var workflowShape = await AssertRecoveryResponseAsync(
            workflowResponse,
            workflowSession.Id,
            operation,
            wasCompacted);
        var agentShape = await AssertRecoveryResponseAsync(
            agentResponse,
            agentSession.Id,
            operation,
            wasCompacted);
        Assert.Equal(workflowShape, agentShape);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        using var canonicalWorkflowResponse = await _client.PostAsync(
            $"/api/projects/{project.Id}/agent-sessions/{workflowSession.Id}/{operation}",
            content: null);
        var canonicalWorkflowShape = await AssertRecoveryResponseAsync(
            canonicalWorkflowResponse,
            workflowSession.Id,
            operation,
            wasCompacted);
        Assert.Equal(workflowShape, canonicalWorkflowShape);

        Assert.Equal(workflowSession.Id, (await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(workflowSession.Id)
            .GetAsync())?.Id);
        Assert.Equal(agentSession.Id, (await _fixture.Grains
            .GetGrain<IAgentSessionGrain>(agentSession.Id)
            .GetAsync())?.Id);
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task CanonicalRecoveryEndpoint_ActiveAgentLaunchSession_ReturnsSharedConflict(string operation)
    {
        var (project, _) = await CreateProjectAndIssueAsync($"{operation}-agent-active");
        var session = await CreateAgentLaunchSessionAsync(
            project,
            $"{operation}-agent-active",
            attach: true,
            idle: false);

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/agent-sessions/{session.Id}/{operation}",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("session_active", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(session.Id, doc.RootElement.GetProperty("details").GetProperty("sessionId").GetString());
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task CanonicalRecoveryEndpoint_MissingAgentLaunchBinding_CompactReturnsResetHintAndResetRecovers(string operation)
    {
        var (project, _) = await CreateProjectAndIssueAsync($"{operation}-agent-missing");
        var session = await CreateAgentLaunchSessionAsync(
            project,
            $"{operation}-agent-missing",
            attach: false,
            idle: false);

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/agent-sessions/{session.Id}/{operation}",
            content: null);

        if (operation == "compact")
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("runtime_session_missing", doc.RootElement.GetProperty("code").GetString());
            var details = doc.RootElement.GetProperty("details");
            Assert.Equal(session.Id, details.GetProperty("sessionId").GetString());
            Assert.Equal("reset", details.GetProperty("hint").GetString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(session.Id, doc.RootElement.GetProperty("data").GetProperty("id").GetString());
            var rebound = await _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id).GetAsync();
            Assert.Equal("opencode", rebound?.Runtime);
            Assert.False(string.IsNullOrWhiteSpace(rebound?.AgentSessionId));
        }
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("reset")]
    public async Task CanonicalRecoveryEndpoint_CrossProjectSession_ReturnsNotFound(string operation)
    {
        var (sourceProject, _) = await CreateProjectAndIssueAsync($"{operation}-agent-project-a");
        var session = await CreateAgentLaunchSessionAsync(
            sourceProject,
            $"{operation}-agent-project-a",
            attach: true,
            idle: true);
        var (otherProject, _) = await CreateProjectAndIssueAsync($"{operation}-agent-project-b");

        using var response = await _client.PostAsync(
            $"/api/projects/{otherProject.Id}/agent-sessions/{session.Id}/{operation}",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CompactEndpoint_PersistsCompactionEventAndPreservesRuntimeBinding()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("compact-persist", sessionName: "plan", attachIdle: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(p => p.Type == "compaction")
            .Join(db.AgentSessionTranscriptTurns.AsNoTracking().Where(t => t.SessionId == currentSession.Id),
                part => part.TurnId,
                turn => turn.Id,
                (part, _) => part)
            .ToListAsync();

        Assert.NotEmpty(parts);
        var compaction = parts.First();
        var payload = JsonDocument.Parse(compaction.PayloadJson).RootElement;
        Assert.Equal("summary", payload.GetProperty("strategy").GetString());

        var row = await db.AgentSessions.AsNoTracking()
            .SingleAsync(r => r.Id == currentSession.Id);
        Assert.Equal(currentSession.Id, row.AgentSessionId);
    }

    [Fact]
    public async Task SessionMetadataEndpoint_AfterCompact_ExposesContextUsagePercent()
    {
        var (project, issue, _, _) = await CreateAndStartSessionAsync("compact-dto", sessionName: "plan", attachIdle: true);

        using var compactResponse = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);
        Assert.Equal(HttpStatusCode.OK, compactResponse.StatusCode);

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.GetProperty("data");
        var usage = root.GetProperty("usage");
    }

    [Fact]
    public async Task CompactEndpoint_AfterClosedSession_EmitsContextExhaustionCategoryOnMetadata()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("compact-after-close", sessionName: "plan", attachIdle: true);

        using var compactResponse = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);
        Assert.Equal(HttpStatusCode.OK, compactResponse.StatusCode);

        var raw = await _client.GetRawAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.GetProperty("data");
        Assert.Equal(currentSession.Id, root.GetProperty("id").GetString());
        var usage = root.GetProperty("usage");
    }

    [Fact]
    public async Task AgentSessionGrain_Compact_RecoversAfterRuntimeEventsMakeSessionActive()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("compact-deactivate", sessionName: "plan", attachIdle: true);

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("compact", null)]
    [InlineData("reset", null)]
    [InlineData("compact", "acp")]
    [InlineData("reset", "acp")]
    public async Task RecoveryEndpoint_LegacyBackendBinding_CompactFailsAndResetEstablishesOpenCodeBinding(
        string operation,
        string? runtime)
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync(
            $"{operation}-legacy-missing",
            sessionName: "plan",
            attachIdle: true);
        var transcriptPath = $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/transcript";
        var transcriptBefore = await _client.GetStringAsync(transcriptPath);

        await SetPersistedRuntimeAsync(currentSession.Id, runtime);

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/{operation}",
            content: null);

        if (operation == "compact")
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("runtime_session_missing", doc.RootElement.GetProperty("code").GetString());
            var details = doc.RootElement.GetProperty("details");
            Assert.Equal(currentSession.Id, details.GetProperty("sessionId").GetString());
            Assert.Equal("reset", details.GetProperty("hint").GetString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(currentSession.Id, doc.RootElement.GetProperty("data").GetProperty("id").GetString());
            var persisted = await _fixture.Grains.GetGrain<IAgentSessionGrain>(currentSession.Id).GetAsync();
            Assert.Equal("opencode", persisted?.Runtime);
            Assert.EndsWith("-replacement", persisted?.AgentSessionId, StringComparison.Ordinal);
        }

        using var metadataResponse = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan");
        Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
        Assert.Equal(transcriptBefore, await _client.GetStringAsync(transcriptPath));
    }

    private async Task SetPersistedRuntimeAsync(string sessionId, string? runtimeName)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var row = await db.AgentSessions.SingleAsync(r => r.Id == sessionId);
        var state = JsonNode.Parse(row.State)?.AsObject()
            ?? throw new InvalidOperationException($"Session {sessionId} state could not be parsed.");
        var runtime = state["runtime"]?.AsObject()
            ?? throw new InvalidOperationException($"Session {sessionId} state has no runtime binding.");
        if (runtimeName is null)
            runtime.Remove("runtime");
        else
            runtime["runtime"] = runtimeName;

        if (state["status"]?["runtimeSessionLineage"] is JsonArray lineage && lineage.Count > 0)
        {
            var current = lineage[lineage.Count - 1]?.AsObject()
                ?? throw new InvalidOperationException($"Session {sessionId} current lineage entry is invalid.");
            if (runtimeName is null)
                current.Remove("runtime");
            else
                current["runtime"] = runtimeName;
        }

        row.State = state.ToJsonString();
        await db.SaveChangesAsync();

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.DeactivateForTestAsync();
        _ = await grain.GetAsync();
    }

    private async Task<AgentSession> LoadSessionStateAsync(string sessionId)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var state = await db.AgentSessions.AsNoTracking()
            .Where(row => row.Id == sessionId)
            .Select(row => row.State)
            .SingleAsync();
        return JsonSerializer.Deserialize<AgentSession>(state, JSON.Options)
            ?? throw new InvalidOperationException($"Session {sessionId} state could not be deserialized.");
    }

    private static async Task<string[]> AssertRecoveryResponseAsync(
        HttpResponseMessage response,
        string expectedSessionId,
        string expectedOperation,
        bool expectedWasCompacted)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected recovery success, got {(int)response.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(expectedSessionId, data.GetProperty("id").GetString());
        Assert.Equal(expectedOperation, data.GetProperty("operation").GetString());
        Assert.Equal(expectedWasCompacted, data.GetProperty("wasCompacted").GetBoolean());
        Assert.False(data.TryGetProperty("agentSessionId", out _));
        return data.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private RecordingRunnerHubContext RunnerHub =>
        _fixture.Services.GetRequiredService<RecordingRunnerHubContext>();

    private SessionCommandRequest AssertSingleSessionCommandInvocation()
    {
        var invocation = Assert.Single(RunnerHub.Invocations);
        Assert.Equal($"connection-{_runnerId}", invocation.ConnectionId);
        Assert.Equal("SessionCommand", invocation.Method);
        return Assert.IsType<SessionCommandRequest>(Assert.Single(invocation.Arguments));
    }

    private async Task<AgentSessionInfo> CreateAgentLaunchSessionAsync(
        ProjectDto project,
        string name,
        bool attach,
        bool idle)
    {
        var sessionId = $"agent-recovery-{Guid.NewGuid():N}";
        var workDir = $"/workspaces/{project.Id}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: _runnerId,
            AgentRuntime: "opencode",
            WorkDir: workDir,
            Model: null,
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                ProjectId: project.Id,
                AgentId: $"agent-{Guid.NewGuid():N}",
                AgentName: $"recovery-{name}"))));

        if (attach)
        {
            await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
                AgentSessionId: sessionId,
                Model: null,
                WorkDir: workDir,
                ChangeDir: null,
                ProcessPid: 1234));
        }

        if (idle)
            _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        return await grain.GetAsync()
            ?? throw new InvalidOperationException($"Agent session {sessionId} was not created.");
    }

    private async Task<(ProjectDto Project, IssueDto Issue, WorkDispatch Work, CreatedSession Session)> CreateAndStartSessionAsync(
        string name,
        string sessionName = "plan",
        bool attachAndStart = false,
        bool attachIdle = false)
    {
        var (project, issue) = await CreateProjectAndIssueAsync(name);
        var work = new WorkDispatch(
            WorkflowRunId: $"wf-{Guid.NewGuid():N}",
            WorkId: $"work-{Guid.NewGuid():N}",
            Uses: "mohist/acp-agent",
            WorkType: "task",
            Stage: "Build",
            Title: $"Session api {name}",
            Issue: new WorkIssueRef(project.Id, issue.Number));

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
        await issueGrain.StartWorkAsync();
        var currentWorkflowRunId = (await issueGrain.GetWorkflowStatusAsync())!.WorkflowRunId!;
        var currentSession = await OpenRunnerSessionAsync(project.Id, issue.Number, currentWorkflowRunId, sessionName, work, $"Session api {name}");

        if (attachAndStart)
        {
            await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { runtimeSessionId = currentSession.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });
        }
        else if (attachIdle)
        {
            await _client.PostOkAsync(RunnerAgentSessionAttachPath(currentSession), new { runtimeSessionId = currentSession.Id, workDir = $"/workspaces/{project.Id}", processPid = 1234 });
            _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        }

        return (project, issue, work, currentSession);
    }

    private async Task<(ProjectDto Project, IssueDto Issue)> CreateProjectAndIssueAsync(string name)
    {
        var projectName = $"recovery-api-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main", setDefault = true });
        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new { title = $"Recovery api {name}", body = "track sessions", labels = new Dictionary<string, string>(StringComparer.Ordinal), priority = "p1", projectId = project.Id, isDraft = false });
        return (project, issue);
    }

    private async Task<CreatedSession> OpenRunnerSessionAsync(string projectId, int issueNumber, string workflowRunId, string sessionName, WorkDispatch work, string title)
    {
        await _client.PostOkAsync($"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(workflowRunId)}/{Uri.EscapeDataString(sessionName)}/open", new
        {
            workId = work.WorkId,
            workType = work.WorkType,
            stage = work.Stage,
            title,
            issueNumber
        });

        var sessionId = await ResolveSessionIdAsync(workflowRunId, sessionName);
        var session = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
        return new CreatedSession(projectId, issueNumber, workflowRunId, sessionName, session ?? throw new InvalidOperationException($"Session {workflowRunId}/{sessionName} was not created."));
    }

    private async Task<string> ResolveSessionIdAsync(string workflowRunId, string sessionName)
    {
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        return await db.AgentSessions
            .Where(s => s.LabelSourceId == workflowRunId && s.LabelSessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
    }

    private string RunnerAgentSessionAttachPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/attach";

    private string RunnerAgentSessionRuntimeEventsPath(CreatedSession session) =>
        $"{RunnerSessionPath(session)}/runtime-events";

    private string RunnerSessionPath(CreatedSession session) =>
        $"/api/runner/{_runnerId}/sessions/{Uri.EscapeDataString(session.ProjectId)}/{Uri.EscapeDataString(session.WorkflowRunId)}/{Uri.EscapeDataString(session.SessionName)}";

    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(string Id, int Number, string Title);
    private sealed record CreatedSession(
        string ProjectId,
        int IssueNumber,
        string WorkflowRunId,
        string SessionName,
        AgentSessionInfo Info)
    {
        public string Id => Info.Id;
    }
}
