using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class GenericAgentSessionCancelApiSpecs : IAsyncLifetime
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;
    private readonly string _runnerId = $"generic-cancel-{Guid.NewGuid():N}";

    public GenericAgentSessionCancelApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // Issue-129 T-005: each test instance registers its own runner via
    // LaunchAndOpenGenericSessionAsync so the runner id is unique across
    // tests and we don't pick up a stale work item from a previous test.
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            using var response = await _client.PostAsync($"/api/runner/{_runnerId}/unregister", content: null);
            _ = response;
        }
        catch
        {
            // Best-effort cleanup; do not mask test failures.
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Cancel_ActiveSessionRunnerReportsCancelled_ReturnsCancelledState()
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-ok");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("CancelAgentSession", new AgentSessionCancelReply("cancelled"));
        tracker.Register(_runnerId, "conn-gen-cancel-ok");
        try
        {
            using var response = await PostGenericCancelAsync(project.Id, sessionId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("cancelled", doc.RootElement.GetProperty("data").GetProperty("state").GetString());

            // The runner received a `CancelAgentSession` invocation with
            // the unified generic session target.
            var invocation = Assert.Single(runnerHub.Invocations);
            Assert.Equal("CancelAgentSession", invocation.Method);
            Assert.Equal("conn-gen-cancel-ok", invocation.ConnectionId);
            var payload = JsonSerializer.SerializeToElement(invocation.Arguments.Single());
            var target = payload.GetProperty("target");
            Assert.Equal("generic", target.GetProperty("kind").GetString());
            Assert.Equal(project.Id, target.GetProperty("projectId").GetString());
            Assert.Equal(sessionId, target.GetProperty("sessionId").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Cancel_ActiveSessionRunnerReportsNotCancellable_ReturnsNotCancellableState()
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-not-cancellable");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("CancelAgentSession", new AgentSessionCancelReply("not-cancellable"));
        tracker.Register(_runnerId, "conn-gen-cancel-not-cancellable");
        try
        {
            using var response = await PostGenericCancelAsync(project.Id, sessionId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            // The response mirrors the runner's reported state; the API
            // never pretends success.
            Assert.Equal("not-cancellable", doc.RootElement.GetProperty("data").GetProperty("state").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Cancel_AlreadyTerminalSession_ShortCircuitsWithoutCallingRunner()
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-terminal");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");

        // Mark the session terminal via a runtime event, then drain the
        // transcript so the resolver's DB read sees the close event.
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionClosed,
                PayloadJson: "{\"status\":\"completed\"}"),
        }));
        await grain.FlushForTestAsync();

        runnerHub.Clear();
        tracker.Register(_runnerId, "conn-gen-cancel-terminal");
        try
        {
            using var response = await PostGenericCancelAsync(project.Id, sessionId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            // The server short-circuits and returns the current terminal
            // state without invoking the runner. The response carries
            // "completed" verbatim — the API does not report a fresh
            // cancellation.
            Assert.Equal("completed", doc.RootElement.GetProperty("data").GetProperty("state").GetString());
            Assert.Empty(runnerHub.Invocations);
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("stopped")]
    public async Task Cancel_AlreadyTerminalSession_MirrorsEachTerminalStateVerbatim(string terminalStatus)
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync($"gen-cancel-terminal-{terminalStatus}");

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionClosed,
                PayloadJson: $"{{\"status\":\"{terminalStatus}\"}}"),
        }));
        await grain.FlushForTestAsync();

        using var response = await PostGenericCancelAsync(project.Id, sessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(terminalStatus, doc.RootElement.GetProperty("data").GetProperty("state").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Cancel_UnknownSession_ReturnsNotFound()
    {
        var project = await CreateProjectAsync("gen-cancel-404");

        using var response = await PostGenericCancelAsync(project.Id, Guid.NewGuid().ToString("N"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Cancel_SessionInOtherProject_ReturnsNotFound()
    {
        var (_, _, sessionIdInA, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-isolation-a");
        var projectB = await CreateProjectAsync("gen-cancel-isolation-b");

        using var response = await PostGenericCancelAsync(projectB.Id, sessionIdInA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Cancel_ActiveSessionButRunnerOffline_ReturnsNotCancellableState()
    {
        // The session is opened (so the runner has bound a RunnerId) but
        // the runner's SignalR connection is not registered: the API
        // surfaces `not-cancellable` honestly rather than faking success.
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-offline");
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();

        using var response = await PostGenericCancelAsync(project.Id, sessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not-cancellable", doc.RootElement.GetProperty("data").GetProperty("state").GetString());
        Assert.Empty(runnerHub.Invocations);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Cancel_RunnerRepliesWithTerminalState_MirrorsThatTerminalState()
    {
        // The runner is allowed to return a terminal-state name in its
        // reply (e.g. it observed the session close as a side effect of
        // the cancel notification). The server mirrors that value
        // verbatim into the HTTP response.
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-runner-terminal");
        var tracker = _fixture.Services.GetRequiredService<RunnerConnectionTracker>();
        var runnerHub = _fixture.Services.GetRequiredService<IHubContext<RunnerHub>>() as RecordingRunnerHubContext
            ?? throw new InvalidOperationException("Recording runner hub context was not registered.");
        runnerHub.Clear();
        runnerHub.SetInvocationResponse("CancelAgentSession", new AgentSessionCancelReply("failed"));
        tracker.Register(_runnerId, "conn-gen-cancel-runner-terminal");
        try
        {
            using var response = await PostGenericCancelAsync(project.Id, sessionId);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("failed", doc.RootElement.GetProperty("data").GetProperty("state").GetString());
        }
        finally
        {
            tracker.Unregister(_runnerId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task ResolveGenericCancelTargetAsync_UnknownSessionId_ReturnsNull()
    {
        var project = await CreateProjectAsync("gen-cancel-resolve-404");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var target = await querier.ResolveGenericCancelTargetAsync(project.Id, Guid.NewGuid().ToString("N"));

        Assert.Null(target);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task ResolveGenericCancelTargetAsync_ActiveSession_ReturnsNullTerminalState()
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-resolve-active");

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var target = await querier.ResolveGenericCancelTargetAsync(project.Id, sessionId);

        Assert.NotNull(target);
        Assert.Equal(_runnerId, target!.RunnerId);
        Assert.Equal(sessionId, target.SessionId);
        Assert.Null(target.TerminalState);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task ResolveGenericCancelTargetAsync_TerminalSession_ReturnsTerminalState()
    {
        var (project, _, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-cancel-resolve-terminal");

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionClosed,
                PayloadJson: "{\"status\":\"failed\"}"),
        }));
        await grain.FlushForTestAsync();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();

        var target = await querier.ResolveGenericCancelTargetAsync(project.Id, sessionId);

        Assert.NotNull(target);
        Assert.Equal("failed", target!.TerminalState);
    }

    private Task<HttpResponseMessage> PostGenericCancelAsync(string projectId, string sessionId) =>
        _client.PostAsync($"/api/projects/{projectId}/agent-sessions/{sessionId}/cancel", content: null);

    private async Task<(ProjectRef Project, AgentRef Agent, string SessionId, AgentSessionInfo Info)> LaunchAndOpenGenericSessionAsync(string name)
    {
        var project = await CreateProjectAsync(name);
        var runnerId = _runnerId;
        var agent = await CreateAgentAsync(project.Id, $"gen-cancel-agent-{name}");

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

        // Open + attach the generic session so the runner's Runtime.RunnerId
        // is bound and IsActive resolves true (matches the followup
        // helper used in T-004).
        await _fixture.Client.PostOkAsync(
            $"/api/runner/{runnerId}/agent-sessions/{project.Id}/{sessionId}/open",
            new
            {
                workId = $"work-{Guid.NewGuid():N}",
                workType = "task",
                stage = "Build",
                title = $"session for {name}",
                issueNumber = 1,
            });

        await _fixture.Client.PostOkAsync(
            $"/api/runner/{runnerId}/agent-sessions/{project.Id}/{sessionId}/attach",
            new
            {
                agentSessionId = sessionId,
                workDir = $"/workspaces/{project.Id}",
                processPid = 1234,
            });

        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var info = await grain.GetAsync() ?? throw new InvalidOperationException("session grain returned null");
        return (project, agent, sessionId, info);
    }

    private async Task<ProjectRef> CreateProjectAsync(string name)
    {
        var projectName = $"gen-cancel-{Guid.NewGuid():N}";
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

    private sealed record ProjectRef(string Id);
    private sealed record AgentRef(string Id, string Name);
    private sealed record ProjectDto(string Id, string Name);
}
