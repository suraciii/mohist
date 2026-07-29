using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class GenericAgentSessionFollowupApiSpecs : GenericAgentSessionFollowupApiTestSupport
{
    public GenericAgentSessionFollowupApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task GenericFollowupEndpoint_ActiveGenericSessionOnlineRunner_ReturnsAccepted()
    {
        var (project, agent, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-followup-ok");
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId);
        var activeWorksBefore = await GetActiveWorkSnapshotAsync(runner);
        Assert.NotEmpty(activeWorksBefore);
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        tracker.Register(_runnerId, "conn-gen-followup-1");
        try
        {
            using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "add a logout route" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(sessionId, data.GetProperty("sessionId").GetString());
            Assert.Equal("accepted", data.GetProperty("status").GetString());
            Assert.False(string.IsNullOrEmpty(data.GetProperty("inputId").GetString()));
            Assert.False(string.IsNullOrEmpty(data.GetProperty("turnId").GetString()));

            var sent = Assert.Single(runnerHub.SentMessages);
            Assert.Equal("conn-gen-followup-1", sent.ConnectionId);
            Assert.Equal("ReceiveFollowup", sent.Method);

            var payload = JsonSerializer.SerializeToElement(sent.Arguments.Single());
            Assert.Equal("add a logout route", payload.GetProperty("text").GetString());
            var target = payload.GetProperty("target");
            Assert.Equal("generic", target.GetProperty("kind").GetString());
            Assert.Equal(project.Id, target.GetProperty("projectId").GetString());
            Assert.Equal(sessionId, target.GetProperty("sessionId").GetString());
            Assert.Equal(activeWorksBefore, await GetActiveWorkSnapshotAsync(runner));
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task GenericFollowupEndpoint_IdleSession_StartsUserTurnWithoutCreatingWorkUnit()
    {
        var (project, sessionId, runtimeSessionId) = await CreateIdleGenericSessionAsync("gen-followup-idle");
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        Assert.NotEqual("active", (await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync())?.Status);
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId);
        var activeWorksBefore = await GetActiveWorkSnapshotAsync(runner);
        Assert.Empty(activeWorksBefore);

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        tracker.Register(_runnerId, "conn-gen-followup-idle");
        try
        {
            using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "start an idle turn" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(sessionId, data.GetProperty("sessionId").GetString());
            Assert.Equal("accepted", data.GetProperty("status").GetString());
            Assert.False(string.IsNullOrEmpty(data.GetProperty("inputId").GetString()));
            Assert.False(string.IsNullOrEmpty(data.GetProperty("turnId").GetString()));

            var sent = Assert.Single(runnerHub.SentMessages);
            Assert.Equal("ReceiveFollowup", sent.Method);
            Assert.Equal(activeWorksBefore, await GetActiveWorkSnapshotAsync(runner));

            var unchanged = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
            Assert.Equal(sessionId, unchanged?.Id);
            Assert.Equal(runtimeSessionId, unchanged?.AgentSessionId);
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task GenericFollowupEndpoint_RunnerCannotResolveRestartedSession_ReturnsResetHint()
    {
        var (project, sessionId, _) = await CreateIdleGenericSessionAsync("gen-followup-restarted");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("ReceiveFollowup", new RunnerFollowupDeliveryResult(false, "missing"));
        tracker.Register(_runnerId, "conn-gen-followup-restarted");
        try
        {
            using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "resume after restart" });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("runtime_session_missing", body.RootElement.GetProperty("code").GetString());
            Assert.Equal(sessionId, body.RootElement.GetProperty("details").GetProperty("sessionId").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task GenericRunnerRoutes_CrossProjectSession_ReturnNotFoundAndDoNotMutate()
    {
        var launched = await LaunchAndOpenGenericSessionAsync("gen-cross-project");
        var otherProject = await CreateProjectAsync("gen-cross-project-other");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(launched.SessionId);
        var before = await grain.GetAsync() ?? throw new InvalidOperationException("session grain returned null");

        using var get = await _client.GetAsync($"/api/runner/{_runnerId}/agent-sessions/{otherProject.Id}/{launched.SessionId}");
        using var open = await _client.PostAsJsonAsync($"/api/runner/{_runnerId}/agent-sessions/{otherProject.Id}/{launched.SessionId}/open", new { workId = "bad-open" });
        using var attach = await _client.PostAsJsonAsync($"/api/runner/{_runnerId}/agent-sessions/{otherProject.Id}/{launched.SessionId}/attach", new { runtimeSessionId = "bad-acp" });
        using var events = await _client.PostAsJsonAsync($"/api/runner/{_runnerId}/agent-sessions/{otherProject.Id}/{launched.SessionId}/runtime-events", new
        {
            runtimeEvents = new[] { new { type = "session.input", payload = new { text = "bad" } } }
        });

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, open.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, attach.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, events.StatusCode);
        var after = await grain.GetAsync() ?? throw new InvalidOperationException("session grain returned null");
        Assert.Equal(before.AgentSessionId, after.AgentSessionId);
        Assert.Equal(before.WorkDir, after.WorkDir);
        Assert.Equal(before.LastDataAt, after.LastDataAt);
    }

    [Fact]
    public async Task GenericRunnerOpen_UnknownSession_ReturnsNotFoundAndDoesNotCreateSession()
    {
        var project = await CreateProjectAsync("gen-unknown-open");
        var sessionId = $"missing-{Guid.NewGuid():N}";

        using var response = await _client.PostAsJsonAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{project.Id}/{sessionId}/open",
            new { workId = "bad-open" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        Assert.Null(await grain.GetAsync());
    }

    [Fact]
    public async Task GenericRunnerOpen_PreMintedLaunchSession_BindsRunnerIdForFollowupAndCancelResolution()
    {
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-open-binds-runner");

        using var existing = await _client.GetAsync($"/api/runner/{_runnerId}/agent-sessions/{project.Id}/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        var existingPayload = await existing.Content.ReadFromJsonAsync<JsonElement>();
        if (existingPayload.TryGetProperty("runtimeSessionId", out var runtimeSessionId))
            Assert.True(runtimeSessionId.ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(runtimeSessionId.GetString()));
        Assert.Equal("opencode", existingPayload.GetProperty("runtime").GetString());
        Assert.False(existingPayload.TryGetProperty("coderSessionId", out _));

        await _fixture.Client.PostOkAsync(
            $"/api/runner/{_runnerId}/agent-sessions/{project.Id}/{sessionId}/open",
            new
            {
                workId = $"work-{Guid.NewGuid():N}",
                workType = "agent-job",
                stage = "agent",
                title = "bind pre-minted generic session",
            });

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var info = await grain.GetAsync() ?? throw new InvalidOperationException("session grain returned null");
        Assert.Equal(_runnerId, info.RunnerId);

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();
        var followupTarget = await querier.ResolveGenericFollowupTargetAsync(project.Id, sessionId);

        Assert.NotNull(followupTarget);
        Assert.Equal(_runnerId, followupTarget!.RunnerId);
    }

    [Fact]
    public async Task GenericFollowupEndpoint_EmptyText_ReturnsBadRequest()
    {
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-followup-empty");

        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("followup_text_missing", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GenericFollowupEndpoint_WhitespaceText_ReturnsBadRequest()
    {
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-followup-ws");

        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "   \t  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("followup_text_missing", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GenericFollowupEndpoint_MissingText_ReturnsBadRequest()
    {
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-followup-missing");

        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("followup_text_missing", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GenericFollowupEndpoint_UnknownSession_ReturnsNotFound()
    {
        var project = await CreateProjectAsync("gen-followup-404");

        using var response = await PostGenericFollowupAsync(project.Id, Guid.NewGuid().ToString("N"), new { text = "ping" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GenericFollowupEndpoint_MissingBindingBeforeRunnerOpens_ReturnsRuntimeSessionMissing()
    {
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-followup-inactive");

        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "ping" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("runtime_session_missing", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(sessionId, doc.RootElement.GetProperty("details").GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task GenericFollowupEndpoint_TerminalActivityStaysFollowable()
    {
        var (project, agent, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-followup-lifecycle");

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var activePersistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionActivity,
                PayloadJson: "{\"activity\":\"active\"}"),
        }, sessionId));
        await activePersistence.WaitAsync();

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.SetInvocationResponse("ReceiveFollowup", new RunnerFollowupDeliveryResult(true));
        tracker.Register(_runnerId, "conn-gen-followup-lifecycle");
        try
        {
            using var activeResponse = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "while alive" });
            Assert.Equal(HttpStatusCode.OK, activeResponse.StatusCode);

            await using var scope = _fixture.Services.CreateAsyncScope();
            var jobs = scope.ServiceProvider.GetRequiredService<AgentJobQuerier>();
            var jobId = Assert.Single(await jobs.ListByAgentAsync(project.Id, agent.Id)).JobKey;
            var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
            var snapshot = await job.GetRuntimeSnapshotAsync();
            await _fixture.Grains.GetGrain<IRunnerGrain>(snapshot.RunnerId!).ReportAgentJobResultAsync(
                jobId,
                snapshot.CurrentWorkId!,
                new WorkResult("completed"));

            using var terminalResponse = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "after close" });
            Assert.Equal(HttpStatusCode.OK, terminalResponse.StatusCode);
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Fact]
    public async Task GenericFollowupEndpoint_ActiveSessionOfflineRunner_ReturnsAcceptedAndQueued()
    {
        // Per the new accept semantics (D4): acceptance is decoupled from
        // runner delivery. The input is persisted and the turn is queued;
        // runner-offline is no longer a 503 — the input is still accepted
        // and a same-key retry will re-attempt delivery.
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-followup-offline");

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionLiveness,
                PayloadJson: "{}"),
        }, sessionId));

        // The runner opened the session, so it's marked active, but
        // there's no SignalR connection tracked for this runner id.
        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "ping" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("accepted", data.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("inputId").GetString()));
    }

    [Fact]
    public async Task GenericFollowupEndpoint_SessionInOtherProject_ReturnsNotFound()
    {
        // The route group resolves a project from the URL; passing the
        // session id from a different project must not leak the session.
        var (projectA, _, sessionIdInA, _) = await LaunchGenericSessionAsync("gen-followup-isolation-a");
        var projectB = await CreateProjectAsync("gen-followup-isolation-b");

        using var response = await PostGenericFollowupAsync(projectB.Id, sessionIdInA, new { text = "cross-project" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResolveGenericFollowupTargetAsync_ReadsRunnerIdAndIsActiveFromSession()
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-resolve-target");

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionActivity,
                PayloadJson: "{\"activity\":\"active\"}"),
        }, sessionId));
        await persistence.WaitAsync();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var target = await querier.ResolveGenericFollowupTargetAsync(project.Id, sessionId);

        Assert.NotNull(target);
        Assert.Equal(_runnerId, target!.RunnerId);
        Assert.Equal(sessionId, target.SessionId);
        Assert.True(target.IsActive);
    }

    [Fact]
    public async Task ResolveGenericFollowupTargetAsync_NoRunnerOpened_ReturnsActiveQueuedTargetWithEmptyRunner()
    {
        // The launch minted the session, but the runner never opened it
        // (no RunnerId bound). The accepted initial turn keeps Session
        // activity active while the work is queued.
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-resolve-no-runner");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var target = await querier.ResolveGenericFollowupTargetAsync(project.Id, sessionId);

        Assert.NotNull(target);
        Assert.Equal(string.Empty, target!.RunnerId);
        Assert.Equal(sessionId, target.SessionId);
        Assert.True(target.IsActive);
    }

    [Fact]
    public async Task ResolveGenericFollowupTargetAsync_UnknownSessionId_ReturnsNull()
    {
        var project = await CreateProjectAsync("gen-resolve-404");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var target = await querier.ResolveGenericFollowupTargetAsync(project.Id, Guid.NewGuid().ToString("N"));

        Assert.Null(target);
    }

}
