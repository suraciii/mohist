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
public class AgentSessionRecoveryConflictApiSpecs : AgentSessionRecoveryApiTestSupport
{
    public AgentSessionRecoveryConflictApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
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

}
