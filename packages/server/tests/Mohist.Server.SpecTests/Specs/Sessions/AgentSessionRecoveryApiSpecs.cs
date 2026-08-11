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
using Mohist.Server.TestSupport;
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

        var request = AssertSingleSessionCommandInvocation(currentSession.Id);
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
        // Attaching the runtime does not make the session active; session.activity does.
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(currentSession.Id);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                "{\"activity\":\"active\"}"),
        }, currentSession.Id));
        await persistence.WaitAsync();

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session_active", doc.RootElement.GetProperty("code").GetString());
        Assert.Contains("active", doc.RootElement.GetProperty("error").GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(currentSession.Id, doc.RootElement.GetProperty("details").GetProperty("sessionId").GetString());
        AssertNoSessionCommandInvocations(currentSession.Id);
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

        var request = AssertSingleSessionCommandInvocation(currentSession.Id);
        Assert.Equal(SessionCommandKind.Reset, request.Command);
        Assert.Equal(currentSession.Id, request.SessionId);
        Assert.Equal(currentSession.Id, request.RuntimeSessionId);
        Assert.Equal(currentSession.Id, request.ExpectedRuntimeSessionId);

        var persisted = await _fixture.Grains.GetGrain<IAgentSessionGrain>(currentSession.Id).GetAsync();
        Assert.Equal($"{currentSession.Id}-replacement", persisted?.AgentSessionId);
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
    var request = AssertSingleSessionCommandInvocation(currentSession.Id);
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
    var request = AssertSingleSessionCommandInvocation(currentSession.Id);
    Assert.Equal(SessionCommandKind.Reset, request.Command);
    Assert.Equal("pi", request.Runtime);
    Assert.Equal(currentSession.Id, request.ExpectedRuntimeSessionId);
  }

    [Fact]
    public async Task CompactEndpoint_PiBoundActiveSession_StillRejectsWithIdleConflict()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("compact-pi-active", sessionName: "plan", attachAndStart: true);
        await SetPersistedRuntimeAsync(currentSession.Id, "pi");
        // Attaching the runtime does not make the session active; session.activity does.
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(currentSession.Id);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                "{\"activity\":\"active\"}"),
        }, currentSession.Id));
        await persistence.WaitAsync();

        using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/plan/compact", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("session_active", doc.RootElement.GetProperty("code").GetString());
        AssertNoSessionCommandInvocations(currentSession.Id);
    }
}
