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
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public class AgentSessionRecoveryApiSpecs : AgentSessionRecoveryApiTestSupport
{
    public AgentSessionRecoveryApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

  [Fact]
  public async Task RuntimeEventsEndpoint_IgnoresOldPhysicalBindingAfterReset()
  {
    var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("stale-runtime-events", sessionName: "build", attach: true);
    using var reset = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset", content: null);
    Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
    var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(currentSession.Id);
    var afterReset = await grain.GetAsync();
    Assert.NotNull(afterReset);
    Assert.NotEqual(currentSession.Id, afterReset!.AgentSessionId);

    using var staleEvent = await _client.PostAsJsonAsync(
      $"/api/runner/{_runnerId}/agent-sessions/{currentSession.Id}/runtime-events",
      new
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
    var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("compact-pi-bound", sessionName: "plan", attach: true);
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
    var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("reset-pi-bound", sessionName: "build", attach: true);
    await SetPersistedRuntimeAsync(currentSession.Id, "pi");

    using var response = await _client.PostAsync($"/api/projects/{project.Id}/issues/{issue.Number}/sessions/build/reset", content: null);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var request = AssertSingleSessionCommandInvocation();
    Assert.Equal(SessionCommandKind.Reset, request.Command);
    Assert.Equal("pi", request.Runtime);
    Assert.Equal(currentSession.Id, request.ExpectedRuntimeSessionId);
  }

}
