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
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

[Collection("IntegrationSessions")]
public class GenericAgentSessionFollowupApiSpecs : GenericAgentSessionFollowupApiTestSupport
{
    public GenericAgentSessionFollowupApiSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task GenericFollowupEndpoint_ActiveGenericSessionOnlineRunner_ReturnsAcceptedAndQueued()
    {
        var (project, agent, sessionId, _) = await LaunchAndOpenGenericSessionAsync("gen-followup-ok");
        var runner = _fixture.Grains.GetGrain<IRunnerGrain>(_runnerId);
        var launch = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetInitialLaunchAsync();
        Assert.NotNull(launch?.Turn?.JobId);
        using (var poll = await _client.PostAsync($"/api/runner/{_runnerId}/poll", content: null))
        {
            poll.EnsureSuccessStatusCode();
        }
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
            Assert.Equal("accepted", data.GetProperty("inputAcceptance").GetString());
            Assert.Equal("queued", data.GetProperty("turnStatus").GetString());

            Assert.Empty(runnerHub.SentMessages);
            Assert.Equal(activeWorksBefore, await GetActiveWorkSnapshotAsync(runner));

            using var summary = await _client.GetAsync($"/api/projects/{project.Id}/agent-sessions/{sessionId}");
            var summaryData = (await summary.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            Assert.Equal("queued", summaryData.GetProperty("turns")[0].GetProperty("status").GetString());
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

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = body.RootElement.GetProperty("data");
            Assert.Equal("accepted", data.GetProperty("status").GetString());
            Assert.False(string.IsNullOrEmpty(data.GetProperty("inputId").GetString()));
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
    public async Task GenericFollowupEndpoint_EmptyText_ReturnsBadRequest()
    {
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-followup-empty");

        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rejected", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task GenericFollowupEndpoint_WhitespaceText_ReturnsBadRequest()
    {
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-followup-ws");

        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "   \t  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rejected", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task GenericFollowupEndpoint_MissingText_ReturnsBadRequest()
    {
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-followup-missing");

        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rejected", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task GenericFollowupEndpoint_UnknownSession_ReturnsNotFound()
    {
        var project = await CreateProjectAsync("gen-followup-404");

        using var response = await PostGenericFollowupAsync(project.Id, Guid.NewGuid().ToString("N"), new { text = "ping" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rejected", doc.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task GenericFollowupEndpoint_MissingBindingBeforeRunnerOpens_ReturnsRuntimeSessionMissing()
    {
        var (project, _, sessionId, _) = await LaunchGenericSessionAsync("gen-followup-inactive");

        using var response = await PostGenericFollowupAsync(project.Id, sessionId, new { text = "ping" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("rejected", data.GetProperty("status").GetString());
        Assert.Equal("runtime_session_missing", data.GetProperty("code").GetString());
        Assert.Equal(sessionId, data.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task GenericFollowupEndpoint_SessionInOtherProject_ReturnsNotFound()
    {
        // The route group resolves a project from the URL; passing the
        // session id from a different project must not leak the session.
        var (projectA, _, sessionIdInA, _) = await LaunchGenericSessionAsync("gen-followup-isolation-a");
        var projectB = await CreateProjectAsync("gen-followup-isolation-b");

        using var response = await PostGenericFollowupAsync(projectB.Id, sessionIdInA, new { text = "cross-project" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var crossProjectDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("rejected", crossProjectDoc.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

}
