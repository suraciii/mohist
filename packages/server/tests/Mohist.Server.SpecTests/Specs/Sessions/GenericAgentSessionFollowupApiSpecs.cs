using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
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
public class GenericAgentSessionFollowupApiSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly string _runnerId = $"generic-followup-{Guid.NewGuid():N}";

    public GenericAgentSessionFollowupApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // Issue-129 T-004: each test instance registers its own runner via
    // `LaunchGenericSessionAsync` / `LaunchAndOpenGenericSessionAsync` /
    // `CreateWorkflowSessionAsync`. Unregister it here so other specs
    // that iterate the global runner registry (e.g. the launch-route
    // test that picks the first AgentJob owner across all runners) don't
    // pick up a leftover generic-followup work item and assert against
    // the wrong runner.
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/unregister", content: null);
            _ = response; // unregister is best-effort; ignore status
        }
        catch
        {
            // Best-effort cleanup; do not mask test failures.
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GenericFollowupEndpoint_ActiveGenericSessionOnlineRunner_ReturnsSent()
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
            Assert.Equal("sent", data.GetProperty("status").GetString());

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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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
            Assert.Equal("sent", data.GetProperty("status").GetString());

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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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
            Assert.Equal("reset", body.RootElement.GetProperty("details").GetProperty("hint").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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
        Assert.False(existingPayload.TryGetProperty("acpSessionId", out _));
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
        var cancelTarget = await querier.ResolveGenericCancelTargetAsync(project.Id, sessionId);

        Assert.NotNull(followupTarget);
        Assert.Equal(_runnerId, followupTarget!.RunnerId);
        Assert.NotNull(cancelTarget);
        Assert.Equal(_runnerId, cancelTarget!.RunnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GenericFollowupEndpoint_EmptyText_ReturnsBadRequest()
    {
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-followup-empty");

        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("followup_text_missing", doc.RootElement.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GenericFollowupEndpoint_WhitespaceText_ReturnsBadRequest()
    {
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-followup-ws");

        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "   \t  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("followup_text_missing", doc.RootElement.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GenericFollowupEndpoint_MissingText_ReturnsBadRequest()
    {
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-followup-missing");

        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("followup_text_missing", doc.RootElement.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GenericFollowupEndpoint_UnknownSession_ReturnsNotFound()
    {
        var project = await CreateProjectAsync("gen-followup-404");

        using var response = await PostGenericFollowupAsync(project.Id, Guid.NewGuid().ToString("N"), new { text = "ping" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GenericFollowupEndpoint_TerminalSession_ReturnsConflict()
    {
        // After a session.closed runtime event the session's status flips
        // to "inactive" and is treated as terminal. The endpoint must
        // surface 409 conflict for that state.
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-followup-terminal");

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionClosed,
                PayloadJson: "{\"status\":\"completed\"}"),
        }, sessionId));
        await grain.FlushForTestAsync();

        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "ping" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("session_inactive", doc.RootElement.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GenericFollowupEndpoint_ActivityMarksActiveThenClosedBecomesConflict()
    {
        // Activity records flip the session to active (within the runtime
        // event window); a subsequent session.closed moves it to terminal.
        // The endpoint accepts followups only between those two events.
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-followup-lifecycle");

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionLiveness,
                PayloadJson: "{}"),
        }, sessionId));
        await grain.FlushForTestAsync();

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        tracker.Register(_runnerId, "conn-gen-followup-lifecycle");
        try
        {
            using var activeResponse = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "while alive" });
            Assert.Equal(HttpStatusCode.OK, activeResponse.StatusCode);
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionClosed,
                PayloadJson: "{\"status\":\"completed\"}"),
        }, sessionId));
        await grain.FlushForTestAsync();

        using var terminalResponse = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "after close" });
        Assert.Equal(HttpStatusCode.Conflict, terminalResponse.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GenericFollowupEndpoint_ActiveSessionOfflineRunner_ReturnsServiceUnavailable()
    {
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

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("runner_offline", doc.RootElement.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task ResolveGenericFollowupTargetAsync_ReadsRunnerIdAndIsActiveFromSession()
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-resolve-target");

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionLiveness,
                PayloadJson: "{}"),
        }, sessionId));

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var target = await querier.ResolveGenericFollowupTargetAsync(project.Id, sessionId);

        Assert.NotNull(target);
        Assert.Equal(_runnerId, target!.RunnerId);
        Assert.Equal(sessionId, target.SessionId);
        Assert.True(target.IsActive);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task ResolveGenericFollowupTargetAsync_NoRunnerOpened_ReturnsInactiveTargetWithEmptyRunner()
    {
        // The launch minted the session, but the runner never opened it
        // (no RunnerId bound). The resolver still finds the session (so
        // the endpoint returns 409 inactive, not 404 not-found), with
        // RunnerId empty and IsActive=false.
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-resolve-no-runner");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var target = await querier.ResolveGenericFollowupTargetAsync(project.Id, sessionId);

        Assert.NotNull(target);
        Assert.Equal(string.Empty, target!.RunnerId);
        Assert.Equal(sessionId, target.SessionId);
        Assert.False(target.IsActive);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task ResolveGenericFollowupTargetAsync_UnknownSessionId_ReturnsNull()
    {
        var project = await CreateProjectAsync("gen-resolve-404");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var target = await querier.ResolveGenericFollowupTargetAsync(project.Id, Guid.NewGuid().ToString("N"));

        Assert.Null(target);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task IssueScopedFollowupRoute_StillEmitsBothTopLevelAndTargetFields()
    {
        // Acceptance: the issue-scoped route must remain reachable AND its
        // payload must still populate `workflowRunId`/`sessionName` for
        // older runners. The unified `target` field is added on top so the
        // newer runner can route by kind, but the legacy fields stay.
        var (project, issue, workflowRunId, sessionName, sessionId) = await CreateWorkflowSessionAsync("gen-issue-scoped-shape");

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        tracker.Register(_runnerId, "conn-issue-scoped-shape");
        try
        {
            using var response = await _client.PostAsJsonAsync(
                $"/api/projects/{project.Id}/issues/{issue.Number}/sessions/{sessionName}/followup",
                new { text = "ship it" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var responseDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var responseData = responseDoc.RootElement.GetProperty("data");
            Assert.Equal(sessionId, responseData.GetProperty("sessionId").GetString());
            Assert.Equal("sent", responseData.GetProperty("status").GetString());

            var sent = Assert.Single(runnerHub.SentMessages);
            Assert.Equal("ReceiveFollowup", sent.Method);

            var payload = JsonSerializer.SerializeToElement(sent.Arguments.Single());
            Assert.Equal(workflowRunId, payload.GetProperty("workflowRunId").GetString());
            Assert.Equal(sessionName, payload.GetProperty("sessionName").GetString());
            Assert.Equal("ship it", payload.GetProperty("text").GetString());

            var target = payload.GetProperty("target");
            Assert.Equal("workflow", target.GetProperty("kind").GetString());
            Assert.Equal(project.Id, target.GetProperty("projectId").GetString());
            Assert.Equal(workflowRunId, target.GetProperty("workflowRunId").GetString());
            Assert.Equal(sessionName, target.GetProperty("sessionName").GetString());
            Assert.Equal("opencode", target.GetProperty("binding").GetProperty("runtime").GetString());
            Assert.Equal(sessionId, target.GetProperty("binding").GetProperty("runtimeSessionId").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task CanonicalFollowupRoute_WorkflowSession_UsesWorkflowTarget()
    {
        var (project, _, workflowRunId, sessionName, sessionId) = await CreateWorkflowSessionAsync("canonical-workflow-shape");
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();
            var persisted = await querier.ResolveCanonicalFollowupTargetAsync(project.Id, sessionId);
            Assert.NotNull(persisted);
            Assert.Equal("opencode", persisted!.Runtime);
            Assert.Equal(WorkDirFor(project.Id), persisted.WorkDir);
            Assert.Equal(sessionId, persisted.RuntimeSessionId);
            Assert.Equal(_runnerId, persisted.RunnerId);
        }
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        tracker.Register(_runnerId, "conn-canonical-workflow-shape");
        try
        {
            using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "ship through canonical route" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var responseDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(sessionId, responseDoc.RootElement.GetProperty("data").GetProperty("sessionId").GetString());

            var invocation = Assert.Single(runnerHub.Invocations);
            Assert.Equal("ReceiveFollowup", invocation.Method);
            var payload = JsonSerializer.SerializeToElement(invocation.Arguments.Single());
            var target = payload.GetProperty("target");
            Assert.Equal("workflow", target.GetProperty("kind").GetString());
            Assert.Equal(workflowRunId, target.GetProperty("workflowRunId").GetString());
            Assert.Equal(sessionName, target.GetProperty("sessionName").GetString());
            var binding = target.GetProperty("binding");
            Assert.True(binding.ValueKind == JsonValueKind.Object, payload.GetRawText());
            Assert.Equal(sessionId, binding.GetProperty("runtimeSessionId").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Followup_AfterReset_IgnoresTerminalStateFromPredecessorRuntime()
    {
        var (project, sessionId, firstRuntimeSessionId) = await CreateIdleGenericSessionAsync("followup-reset-terminal");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionClosed, """{"status":"completed"}"""),
        }, firstRuntimeSessionId));
        await grain.FlushForTestAsync();
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        await grain.ResetAsync(new ResetAgentSessionCommand(firstRuntimeSessionId, "runtime-replacement"));

        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("ReceiveFollowup", new RunnerFollowupDeliveryResult(true));
        tracker.Register(_runnerId, "conn-followup-reset-terminal");
        try
        {
            using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "continue on the replacement" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = JsonSerializer.SerializeToElement(Assert.Single(runnerHub.Invocations).Arguments.Single());
            Assert.Equal("runtime-replacement", payload.GetProperty("target").GetProperty("binding").GetProperty("runtimeSessionId").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task IdleFollowupReservation_BlocksRecoveryUntilDeliveryCompletes()
    {
        var (project, sessionId, _) = await CreateIdleGenericSessionAsync("followup-recovery-race");
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new TaskCompletionSource<RunnerFollowupDeliveryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        runnerHub.Clear();
        runnerHub.SetInvocationResponseFactory("ReceiveFollowup", _ =>
        {
            started.TrySetResult();
            return delivery.Task;
        });
        tracker.Register(_runnerId, "conn-followup-recovery-race");
        try
        {
            var followup = PostGenericFollowupAsync(project.Id, sessionId, new { text = "start and hold" });
            await started.Task;

            using var compact = await _client.PostAsync($"/api/projects/{project.Id}/agent-sessions/{sessionId}/compact", content: null);
            using var reset = await _client.PostAsync($"/api/projects/{project.Id}/agent-sessions/{sessionId}/reset", content: null);

            Assert.Equal(HttpStatusCode.Conflict, compact.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, reset.StatusCode);
            Assert.Equal("session_active", JsonDocument.Parse(await compact.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
            Assert.Equal("session_active", JsonDocument.Parse(await reset.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain(runnerHub.Invocations, invocation => invocation.Method == "SessionCommand");

            delivery.SetResult(new RunnerFollowupDeliveryResult(true));
            using var accepted = await followup;
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RejectedIdleFollowup_AbandonsReservationAndAllowsRecovery()
    {
        var (project, sessionId, _) = await CreateIdleGenericSessionAsync("followup-recovery-abandon");
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("ReceiveFollowup", new RunnerFollowupDeliveryResult(false, "missing"));
        tracker.Register(_runnerId, "conn-followup-recovery-abandon");
        try
        {
            using var rejected = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "reject this turn" });
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.Equal("runtime_session_missing", JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

            runnerHub.SetInvocationResponse("SessionCommand", new SessionCommandResult(Ok: true));
            using var compact = await _client.PostAsync($"/api/projects/{project.Id}/agent-sessions/{sessionId}/compact", content: null);

            Assert.Equal(HttpStatusCode.OK, compact.StatusCode);
            Assert.Contains(runnerHub.Invocations, invocation => invocation.Method == "SessionCommand");
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    private Task<HttpResponseMessage> PostGenericFollowupAsync(string projectId, string sessionId, object body) =>
        _client.PostAsJsonAsync($"/api/projects/{projectId}/agent-sessions/{sessionId}/followup", body);

    private static async Task<string[]> GetActiveWorkSnapshotAsync(IRunnerGrain runner) =>
        (await runner.GetRuntimeStateAsync()).ActiveWorks
            .OrderBy(work => work.WorkId, StringComparer.Ordinal)
            .Select(work => $"{work.WorkId}|{work.OwnerKind}|{work.OwnerId}|{work.WorkType}")
            .ToArray();

    private async Task<(ProjectRef Project, AgentRef Agent, string SessionId, AgentSessionInfo Info)> LaunchGenericSessionAsync(string name)
    {
        var project = await CreateProjectAsync(name);
        var runnerId = _runnerId;
        var agent = await CreateAgentAsync(project.Id, $"gen-followup-agent-{name}");

        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId = project.Id,
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = 2 });

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/{agent.Id}/sessions",
            new { prompt = $"hello from {name}" });

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var info = await grain.GetAsync() ?? throw new InvalidOperationException("session grain returned null");
        return (project, agent, sessionId, info);
    }

    private async Task<(ProjectRef Project, AgentRef Agent, string SessionId, AgentSessionInfo Info)> LaunchAndOpenGenericSessionAsync(string name)
    {
        var launched = await LaunchGenericSessionAsync(name);

        var runnerId = _runnerId;
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{runnerId}/agent-sessions/{launched.Project.Id}/{launched.SessionId}/open",
            new
            {
                workId = $"work-{Guid.NewGuid():N}",
                workType = "task",
                stage = "Build",
                title = $"session for {name}",
                issueNumber = 1,
            });

        // Attach the physical session so AgentRuntimeSessionId is set;
        // StatusName() requires this for the session to read as "active"
        // once runtime events start flowing (same shape the workflow
        // followup tests use).
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{runnerId}/agent-sessions/{launched.Project.Id}/{launched.SessionId}/attach",
            new
            {
                runtimeSessionId = launched.SessionId,
                workDir = WorkDirFor(launched.Project.Id),
                processPid = 1234,
            });

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(launched.SessionId);
        var info = await grain.GetAsync() ?? throw new InvalidOperationException("session grain returned null");
        return (launched.Project, launched.Agent, launched.SessionId, info);
    }

    private async Task<(ProjectRef Project, string SessionId, string RuntimeSessionId)> CreateIdleGenericSessionAsync(string name)
    {
        var project = await CreateProjectAsync(name);
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId);
        await runner.RegisterAsync(new RunnerInfo(_runnerId, ["spec/*"], $"{_runnerId}-host", project.Id));

        var sessionId = $"idle-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: _runnerId,
            AgentRuntime: "opencode",
            WorkDir: WorkDirFor(project.Id),
            Metadata: GenericAgentSessionMetadata.Metadata(new GenericAgentSessionContext(
                project.Id,
                $"agent-{Guid.NewGuid():N}",
                "idle-agent"))));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            runtimeSessionId,
            WorkDir: WorkDirFor(project.Id)));

        return (project, sessionId, runtimeSessionId);
    }

    /// <summary>
    /// Creates a workflow-shaped session via the runner's
    /// <c>POST /api/runner/{id}/sessions/{project}/{wf}/{name}/open</c>
    /// endpoint and attaches a physical session so the existing issue-scoped
    /// followup route is exercised with the same shape production uses.
    /// </summary>
    private async Task<(ProjectRef Project, IssueRef Issue, string WorkflowRunId, string SessionName, string SessionId)> CreateWorkflowSessionAsync(string name)
    {
        var project = await CreateProjectAsync(name);

        var issue = await _client.PostDataAsync<IssueDto>($"/api/projects/{project.Id}/issues", new
        {
            title = $"Generic followup route shape {name}",
            body = "followup route shape test",
            labels = new Dictionary<string, string>(StringComparer.Ordinal),
            priority = "p1",
            projectId = project.Id,
            isDraft = false,
        });

        var runnerId = _runnerId;
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId = project.Id,
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = 2 });

        var runnerGrain = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await runnerGrain.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], $"{runnerId}-host", project.Id));

        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issue.Number)));
        await issueGrain.StartWorkAsync();
        await DispatchEventsAsync();
        var status = await issueGrain.GetWorkflowStatusAsync();
        var workflowRunId = status!.WorkflowRunId!;
        const string sessionName = "plan";

        await _fixture.Client.PostOkAsync(
            $"/api/runner/{runnerId}/sessions/{project.Id}/{workflowRunId}/{sessionName}/open",
            new
            {
                workId = $"work-{Guid.NewGuid():N}",
                workType = "task",
                stage = "Build",
                title = $"session for {name}",
                issueNumber = issue.Number,
                workDir = WorkDirFor(project.Id),
            });

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        var sessionId = await db.AgentSessions
            .Where(s => s.LabelSourceId == workflowRunId && s.LabelSessionName == sessionName)
            .Select(s => s.Id)
            .SingleAsync();
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var attached = await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            AgentSessionId: sessionId,
            Model: null,
            WorkDir: WorkDirFor(project.Id),
            ChangeDir: null,
             ProcessPid: 1234,
             Runtime: "opencode"));
        Assert.Equal(WorkDirFor(project.Id), attached.WorkDir);
        Assert.Equal("opencode", attached.Runtime);

        return (project, new IssueRef(issue.Number), workflowRunId, sessionName, sessionId);
    }

    private Task DispatchEventsAsync() =>
        _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

    private async Task<ProjectRef> CreateProjectAsync(string name)
    {
        var projectName = $"gen-followup-{Guid.NewGuid():N}";
        if (projectName.Length > 63) projectName = projectName[..63];
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", projectName);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
        });
        return new ProjectRef(project.Id);
    }

    private async Task<AgentRef> CreateAgentAsync(string projectId, string agentName)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name = agentName,
                description = $"description for {agentName}",
                instructions = $"instructions for {agentName}",
                agentConfig = new { type = "opencode" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, agentName);
    }

    private static string WorkDirFor(string projectId) => $"/workspaces/{projectId}";

    private sealed record ProjectRef(string Id);
    private sealed record AgentRef(string Id, string Name);
    private sealed record IssueRef(int Number);
    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(int Number, string Title);
}
