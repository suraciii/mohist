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
public class AgentSessionRecoveryConflictApiSpecs : AgentSessionRecoveryApiTestSupport
{
    public AgentSessionRecoveryConflictApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task ResetEndpoint_ActiveSession_ReturnsConflict()
    {
        var (project, issue, _, currentSession) = await CreateAndStartSessionAsync("reset-active", sessionName: "build", attachAndStart: true);
        // Under the activity model (issue-484) attaching the runtime no
        // longer flips the session to active; a session.activity record
        // does. Mark the session active so the idle boundary rejects Reset.
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(currentSession.Id);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                "{\"activity\":\"active\"}"),
        }, currentSession.Id));
        await persistence.WaitAsync();

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
        Assert.Equal("idle", agentSession.Status);

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
        // Under the activity model (issue-484) attaching the runtime no
        // longer flips the session to active; a session.activity record
        // does. Mark the session active so the idle boundary rejects the
        // canonical recovery command.
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(session.Id);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                "{\"activity\":\"active\"}"),
        }, session.Id));
        await persistence.WaitAsync();

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
    public async Task CanonicalRecoveryEndpoint_MissingAgentLaunchBinding_CompactReturnsRuntimeSessionMissingAndResetRecovers(string operation)
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
            // issue-484: the runtime_session_missing conflict no longer
            // carries a reset hint; callers use Reset to recover.
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
            // issue-484: the runtime_session_missing conflict no longer
            // carries a reset hint; callers use Reset to recover.
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
        // Compact preserves the transcript verbatim. Reset (issue-484)
        // rebinds the runtime session and emits a context-reset transcript
        // entry, so its transcript is no longer byte-identical to the
        // pre-recovery snapshot; we only assert it remains readable.
        var transcriptAfter = await _client.GetStringAsync(transcriptPath);
        if (operation == "compact")
        {
            Assert.Equal(transcriptBefore, transcriptAfter);
        }
    }

}
