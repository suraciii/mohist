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
public class AgentSessionRecoveryApiSpecs : AgentSessionRecoveryApiTestSupport
{
    public AgentSessionRecoveryApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
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
  public async Task CompactEndpoint_PiBoundSession_AdmitsCommandAndStampsPiRuntimeOnWire()
  {
    var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("compact-pi-bound", sessionName: "plan", attachIdle: true);
    await SetPersistedRuntimeAsync(currentSession.Id, "pi");

    using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var request = AssertSingleSessionCommandInvocation();
    Assert.Equal(SessionCommandKind.Compact, request.Command);
    Assert.Equal("pi", request.Runtime);
    Assert.Equal(currentSession.Id, request.RuntimeSessionId);
  }

  [Fact]
  public async Task ResetEndpoint_PiBoundSession_AdmitsCommandAndStampsPiRuntimeOnWire()
  {
    var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("reset-pi-bound", sessionName: "build", attachIdle: true);
    await SetPersistedRuntimeAsync(currentSession.Id, "pi");

    using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset", content: null);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var request = AssertSingleSessionCommandInvocation();
    Assert.Equal(SessionCommandKind.Reset, request.Command);
    Assert.Equal("pi", request.Runtime);
    Assert.Equal(currentSession.Id, request.ExpectedRuntimeSessionId);
  }

  [Fact]
  public async Task CompactEndpoint_PiBoundActiveSession_StillRejectsWithIdleConflict()
  {
    var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("compact-pi-active", sessionName: "plan", attachAndStart: true);
    await SetPersistedRuntimeAsync(currentSession.Id, "pi");

    using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    var body = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(body);
    Assert.Equal("session_active", doc.RootElement.GetProperty("code").GetString());
    Assert.Empty(RunnerHub.Invocations);
  }
}
