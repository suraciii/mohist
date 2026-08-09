using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

[Collection("MohistIntegration")]
public class AgentSessionLaunchRuntimeResolutionSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentSessionLaunchRuntimeResolutionSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Launch_WithRuntimeOverride_IsRejectedBeforeAgentLookup()
    {
        var projectId = await CreateProjectAsync("launch-runtime-override");
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/does-not-exist/sessions",
            new { prompt = "execute on pi", runtime = "pi" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unsupported_field", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Launch_WithoutRuntimeOverride_UsesAgentConfigRuntime()
    {
        var projectId = await CreateProjectAsync("launch-runtime-from-config");
        var runnerId = $"launch-runtime-from-config-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "config-runtime-agent", runtime: "pi");
        await CreateWorkspaceAsync(projectId, "launch-runtime-workspace");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
            {
                prompt = "execute on pi via config",
                context = new { workspace = "launch-runtime-workspace" },
            });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = payload.GetProperty("data").GetProperty("jobId").GetString()!;

            var sessionGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            var sessionInfo = await sessionGrain.GetAsync();
            Assert.NotNull(sessionInfo);
            Assert.Equal("pi", sessionInfo!.Runtime);

            var snapshot = await PollDispatchForSessionAsync(jobId, runnerId, sessionId);
            var polledDispatch = snapshot.Dispatch;
            Assert.Equal("pi", ReadRuntimeFromDispatch(polledDispatch));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_WithoutRuntimeOverrideOrConfig_DefaultsToOpenCode()
    {
        var projectId = await CreateProjectAsync("launch-runtime-default");
        var runnerId = $"launch-runtime-default-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "default-runtime-agent");
        await CreateWorkspaceAsync(projectId, "launch-runtime-workspace");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
            {
                prompt = "default runtime",
                context = new { workspace = "launch-runtime-workspace" },
            });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = payload.GetProperty("data").GetProperty("jobId").GetString()!;

            var sessionGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            var sessionInfo = await sessionGrain.GetAsync();
            Assert.NotNull(sessionInfo);
            Assert.Equal("opencode", sessionInfo!.Runtime);

            var snapshot = await PollDispatchForSessionAsync(jobId, runnerId, sessionId);
            var polledDispatch = snapshot.Dispatch;
            Assert.Equal("opencode", ReadRuntimeFromDispatch(polledDispatch));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_WithIssueRuntime_IsRejectedAndDoesNotOverrideAgent()
    {
        var projectId = await CreateProjectAsync("launch-issue-runtime-override");
        var agent = await CreateAgentAsync(projectId, "issue-runtime-override-agent", runtime: "opencode");
        var issueNumber = await CreateIssueAsync(projectId, "Issue runtime override");
        await CreateWorkspaceAsync(projectId, "launch-runtime-workspace");

        using var patch = await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/variables",
            new { vars = new { agent = new { runtime = "pi" } } });
        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);

        using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
            {
                 prompt = "use the agent backend",
                context = new { issueNumber, workspace = "launch-runtime-workspace" },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var info = await session.GetAsync();

        Assert.NotNull(info);
        Assert.Equal("opencode", info!.Runtime);
    }

    [Fact]
    public async Task Launch_WithProjectRuntimeAndNoIssueOverride_UsesAgentConfigRuntime()
    {
        var projectId = await CreateProjectAsync("launch-project-runtime-not-issue-override");
        var agent = await CreateAgentAsync(projectId, "project-runtime-agent", runtime: "opencode");
        var issueNumber = await CreateIssueAsync(projectId, "No issue runtime override");
        await CreateWorkspaceAsync(projectId, "launch-runtime-workspace");

        using var projectPatch = await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/variables",
            new { vars = new { agent = new { runtime = "pi" } } });
        Assert.Equal(HttpStatusCode.OK, projectPatch.StatusCode);

        using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
            {
                prompt = "keep the agent backend",
                context = new { issueNumber, workspace = "launch-runtime-workspace" },
            });

        Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
        var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var info = await session.GetAsync();

        Assert.NotNull(info);
        Assert.Equal("opencode", info!.Runtime);
    }

    [Fact]
    public async Task IssueWorkflowVariables_RejectInvalidAgentRuntime()
    {
        var projectId = await CreateProjectAsync("issue-runtime-invalid");
        var issueNumber = await CreateIssueAsync(projectId, "Invalid issue runtime");

        using var response = await _fixture.Client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issueNumber}/variables",
            new { vars = new { agent = new { runtime = "unknown" } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_agent_config", body.GetProperty("code").GetString());
        Assert.Contains("runtime", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Launch_WithUnknownRuntimeOverride_Returns400()
    {
        var projectId = await CreateProjectAsync("launch-runtime-invalid");
        var agent = await CreateAgentAsync(projectId, "runtime-invalid-agent");

        using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
            {
                prompt = "execute on unknown",
                runtime = "mystery",
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unsupported_field", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateAgent_WithInvalidRuntime_Returns400()
    {
        var projectId = await CreateProjectAsync("agent-create-runtime-invalid");

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name = "bad-runtime-agent",
                description = "agent description",
                instructions = "instructions",
                agentConfig = new { model = "openai/gpt-5.6", runtime = "mystery" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_agent_config", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CreateAgent_WithPiRuntime_Accepts()
    {
        var projectId = await CreateProjectAsync("agent-create-runtime-pi");

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name = "pi-runtime-agent",
                description = "agent description",
                instructions = "instructions",
                agentConfig = new { model = "openai/gpt-5.6", runtime = "pi" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        Assert.Equal("pi", data.GetProperty("agentConfig").GetProperty("runtime").GetString());
    }

    [Fact]
    public async Task Launch_EditingAgentRuntimeAfterLaunch_DoesNotChangeInFlightRuntime()
    {
        var projectId = await CreateProjectAsync("launch-snapshot-fixed");
        var runnerId = $"launch-snapshot-fixed-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "snapshot-agent", runtime: "pi");
        await CreateWorkspaceAsync(projectId, "launch-runtime-workspace");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
            {
                prompt = "snapshot",
                context = new { workspace = "launch-runtime-workspace" },
            });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = payload.GetProperty("data").GetProperty("jobId").GetString()!;

            var snapshot = await PollDispatchForSessionAsync(jobId, runnerId, sessionId);
            var firstDispatch = snapshot.Dispatch;
            Assert.Equal("pi", ReadRuntimeFromDispatch(firstDispatch));

            await PatchAgentRuntimeAsync(projectId, agent.Id, "opencode");

            var sessionInfo = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
            Assert.Equal("pi", sessionInfo!.Runtime);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private static string ReadRuntimeFromDispatch(JsonElement dispatch)
    {
        var withJson = dispatch.GetProperty("with").GetString();
        Assert.False(string.IsNullOrWhiteSpace(withJson));
        using var doc = JsonDocument.Parse(withJson!);
        return doc.RootElement.GetProperty("runtime").GetString()!;
    }
}
