using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;
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
    public async Task Preview_WithNestedExecutionOverride_ResolvesWithoutCreatingSessionOrJob()
    {
        var projectId = await CreateProjectAsync("launch-preview-override");
        var agent = await CreateAgentAsync(projectId, "preview-agent", runtime: "opencode");
        var sessionsBefore = await CountAgentLaunchSessionsAsync(projectId);
        var jobsBefore = await CountAgentJobsAsync(projectId);

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/sessions/preview",
            new
            {
                prompt = "preview only",
                execution = new { runtime = "pi", reasoningEffort = "high" },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data");
        Assert.Equal("pi", data.GetProperty("runtime").GetString());
        Assert.Equal("high", data.GetProperty("reasoningEffort").GetString());
        Assert.Equal("unknown", data.GetProperty("capabilityState").GetString());
        Assert.False(data.GetProperty("matchesSavedDefinition").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("requestFingerprint").GetString()));
        Assert.Equal(sessionsBefore, await CountAgentLaunchSessionsAsync(projectId));
        Assert.Equal(jobsBefore, await CountAgentJobsAsync(projectId));
    }

    [Fact]
    public async Task Launch_WithNestedNonMatchingExecutionOverride_FailsBeforeSideEffects()
    {
        var projectId = await CreateProjectAsync("launch-override-gate");
        var agent = await CreateAgentAsync(projectId, "override-gate-agent", runtime: "opencode");
        var sessionsBefore = await CountAgentLaunchSessionsAsync(projectId);
        var jobsBefore = await CountAgentJobsAsync(projectId);

        using var response = await _fixture.Client.LaunchAgentSessionAsync(
            projectId,
            agent.Id,
            new
            {
                prompt = "must not create",
                execution = new { runtime = "pi" },
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("execution_capability_unconfirmed", body.GetProperty("code").GetString());
        Assert.Equal(sessionsBefore, await CountAgentLaunchSessionsAsync(projectId));
        Assert.Equal(jobsBefore, await CountAgentJobsAsync(projectId));
    }

    [Fact]
    public async Task Preview_CanonicalizesExecutionObjectAndRejectsUnknownNestedFields()
    {
        var projectId = await CreateProjectAsync("launch-preview-canonical");
        var agent = await CreateAgentAsync(projectId, "preview-canonical-agent");

        using var first = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/sessions/preview",
            new { execution = new { runtime = "pi", reasoningEffort = "high" } });
        using var second = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/sessions/preview",
            new { execution = new { reasoningEffort = "high", runtime = "pi" } });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstData = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var secondData = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(
            firstData.GetProperty("requestFingerprint").GetString(),
            secondData.GetProperty("requestFingerprint").GetString());

        using var unsupported = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/sessions/preview",
            new { execution = new { runtime = "pi", provider = "custom" } });
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        var unsupportedBody = await unsupported.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unsupported_field", unsupportedBody.GetProperty("code").GetString());
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
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);
        ClaimResult? claim = null;

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "execute on pi via config" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = payload.GetProperty("data").GetProperty("jobId").GetString()!;

            var sessionGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            var sessionInfo = await sessionGrain.GetAsync();
            Assert.NotNull(sessionInfo);
            Assert.Equal("pi", sessionInfo!.Runtime);

            claim = await AcquirePreparedAgentJobAsync(jobId, runnerId, projectId);
            AssertPreparedAgentJobClaim(claim, jobId, runnerId, sessionId);
            Assert.Equal("pi", ReadRuntimeFromDispatch(claim.Dispatch));
        }
        finally
        {
            if (claim is not null)
            {
                await CompleteClaimedAgentJobAsync(runnerId, claim.AgentJobId, claim.Dispatch.WorkId);
                await DrainDispatchAsync(runnerId);
            }
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_WithoutRuntimeOverrideOrConfig_DefaultsToOpenCode()
    {
        var projectId = await CreateProjectAsync("launch-runtime-default");
        var runnerId = $"launch-runtime-default-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "default-runtime-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);
        ClaimResult? claim = null;

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "default runtime" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = payload.GetProperty("data").GetProperty("jobId").GetString()!;

            var sessionGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            var sessionInfo = await sessionGrain.GetAsync();
            Assert.NotNull(sessionInfo);
            Assert.Equal("opencode", sessionInfo!.Runtime);

            claim = await AcquirePreparedAgentJobAsync(jobId, runnerId, projectId);
            AssertPreparedAgentJobClaim(claim, jobId, runnerId, sessionId);
            Assert.Equal("opencode", ReadRuntimeFromDispatch(claim.Dispatch));
        }
        finally
        {
            if (claim is not null)
            {
                await CompleteClaimedAgentJobAsync(runnerId, claim.AgentJobId, claim.Dispatch.WorkId);
                await DrainDispatchAsync(runnerId);
            }
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_WithIssueRuntime_IsRejectedAndDoesNotOverrideAgent()
    {
        var projectId = await CreateProjectAsync("launch-issue-runtime-override");
        var agent = await CreateAgentAsync(projectId, "issue-runtime-override-agent", runtime: "opencode");
        var issueNumber = await CreateIssueAsync(projectId, "Issue runtime override");
        string? jobId = null;
        try
        {
            using var patch = await _fixture.Client.PatchAsJsonAsync(
                $"/api/projects/{projectId}/issues/{issueNumber}/variables",
                new { vars = new { agent = new { runtime = "pi" } } });
            Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);

            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
                {
                    prompt = "use the agent backend",
                    context = new { issueNumber },
                });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;
            jobId = payload.GetProperty("data").GetProperty("jobId").GetString();
            var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            var info = await session.GetAsync();

            Assert.NotNull(info);
            Assert.Equal("opencode", info!.Runtime);
        }
        finally
        {
            await CleanupLaunchedAgentJobAsync(null, jobId);
        }
    }

    [Fact]
    public async Task Launch_WithProjectRuntimeAndNoIssueOverride_UsesAgentConfigRuntime()
    {
        var projectId = await CreateProjectAsync("launch-project-runtime-not-issue-override");
        var agent = await CreateAgentAsync(projectId, "project-runtime-agent", runtime: "opencode");
        var issueNumber = await CreateIssueAsync(projectId, "No issue runtime override");
        string? jobId = null;
        try
        {
            using var projectPatch = await _fixture.Client.PatchAsJsonAsync(
                $"/api/projects/{projectId}/variables",
                new { vars = new { agent = new { runtime = "pi" } } });
            Assert.Equal(HttpStatusCode.OK, projectPatch.StatusCode);

            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
                {
                    prompt = "keep the agent backend",
                    context = new { issueNumber },
                });

            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
            jobId = launchPayload.GetProperty("data").GetProperty("jobId").GetString();
            var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            var info = await session.GetAsync();

            Assert.NotNull(info);
            Assert.Equal("opencode", info!.Runtime);
        }
        finally
        {
            await CleanupLaunchedAgentJobAsync(null, jobId);
        }
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
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);
        ClaimResult? claim = null;

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "snapshot" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = payload.GetProperty("data").GetProperty("jobId").GetString()!;

            claim = await AcquirePreparedAgentJobAsync(jobId, runnerId, projectId);
            AssertPreparedAgentJobClaim(claim, jobId, runnerId, sessionId);
            Assert.Equal("pi", ReadRuntimeFromDispatch(claim.Dispatch));

            await PatchAgentRuntimeAsync(projectId, agent.Id, "opencode");

            var sessionInfo = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
            Assert.Equal("pi", sessionInfo!.Runtime);
        }
        finally
        {
            if (claim is not null)
            {
                await CompleteClaimedAgentJobAsync(runnerId, claim.AgentJobId, claim.Dispatch.WorkId);
                await DrainDispatchAsync(runnerId);
            }
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private static string ReadRuntimeFromDispatch(WorkDispatch dispatch)
    {
        var withJson = dispatch.With;
        Assert.False(string.IsNullOrWhiteSpace(withJson));
        using var doc = JsonDocument.Parse(withJson!);
        return doc.RootElement.GetProperty("runtime").GetString()!;
    }
}
