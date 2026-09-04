using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;
namespace Mohist.Server.Tests.Agent.Api;

[Collection("RunnerMutationIntegration")]
[Trait("level", "L1")]
public class AgentSessionLaunchValidationRoutesSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentSessionLaunchValidationRoutesSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Launch_NonPositiveEpicContext_Returns400WithoutCreatingSession()
    {
        const int epicNumber = 0;
        var projectId = await CreateProjectAsync("launch-invalid-epic");
        var agent = await CreateAgentAsync(projectId, "invalid-epic-agent");
        var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "invalid context", context = new { epicNumber } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(sessionCountBefore, await CountAgentLaunchSessionsAsync(projectId));
    }

    [Fact]
    public async Task Launch_NonPositiveIssueContext_Returns400WithoutCreatingSession()
    {
        const int issueNumber = 0;
        var projectId = await CreateProjectAsync("launch-invalid-issue");
        var agent = await CreateAgentAsync(projectId, "invalid-issue-agent");
        var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "invalid context", context = new { issueNumber } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(sessionCountBefore, await CountAgentLaunchSessionsAsync(projectId));
    }

    [Fact]
    public async Task Launch_OpaqueEpicContext_Returns400WithoutCreatingSession()
    {
        var projectId = await CreateProjectAsync("launch-opaque-epic");
        var agent = await CreateAgentAsync(projectId, "opaque-epic-agent");
        var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "invalid context", context = new { epicNumber = "epic-7" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(sessionCountBefore, await CountAgentLaunchSessionsAsync(projectId));
    }

    [Fact]
    public async Task Launch_EpicContextFromAnotherProject_Returns404WithoutCreatingSession()
    {
        var projectId = await CreateProjectAsync("launch-local-epic");
        var otherProjectId = await CreateProjectAsync("launch-other-epic");
        var agent = await CreateAgentAsync(projectId, "cross-project-agent");
        var otherEpicNumber = await CreateEpicAsync(otherProjectId, "Other project epic");
        var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "cross project context", context = new { epicNumber = otherEpicNumber } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(sessionCountBefore, await CountAgentLaunchSessionsAsync(projectId));
    }

    [Fact]
    public async Task Launch_UnknownAgent_Returns404_WithoutCreatingSessionOrJob()
    {
        var projectId = await CreateProjectAsync("launch-unknown");

        var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var response = await _fixture.Client.LaunchAgentSessionAsync(
            projectId,
            $"agent_{Guid.NewGuid():N}",
            new { prompt = "find me" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());

        var sessionCountAfter = await CountAgentLaunchSessionsAsync(projectId);
        Assert.Equal(sessionCountBefore, sessionCountAfter);
    }

    [Fact]
    public async Task Launch_ArchivedAgent_Returns409_WithoutCreatingSessionOrJob()
    {
        var projectId = await CreateProjectAsync("launch-archived");
        var agent = await CreateAgentAsync(projectId, "archived-launch-agent");
        using var archive = await _fixture.Client.DeleteAsync($"/api/projects/{projectId}/agents/{agent.Id}");
        archive.EnsureSuccessStatusCode();
        var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);
        var jobCountBefore = await CountAgentJobsAsync(projectId);

        using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "should not launch" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("agent_archived", payload.GetProperty("code").GetString());

        Assert.Equal(sessionCountBefore, await CountAgentLaunchSessionsAsync(projectId));
        Assert.Equal(jobCountBefore, await CountAgentJobsAsync(projectId));
    }

    [Fact]
    public async Task Launch_ResolvesAgentByName_WhenAgentRefIsFriendlyName()
    {
        var projectId = await CreateProjectAsync("launch-name");
        var runnerId = $"launch-name-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "name-fallback");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);
        string? jobId = null;

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(
                projectId,
                "name-fallback",
                new { prompt = "by name please" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = payload.GetProperty("data");
            jobId = data.GetProperty("jobId").GetString();
            Assert.Equal(agent.Id, data.GetProperty("agentId").GetString());
            Assert.Equal("name-fallback", data.GetProperty("agentName").GetString());
        }
        finally
        {
            await CleanupLaunchedAgentJobAsync(runnerId, jobId);
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_IsDistinctFromValidationOnlyAgentJobsRoute()
    {
        var projectId = await CreateProjectAsync("launch-distinct");

        // The validation-only endpoint has no project/agent scoping,
        // no AgentSession minting, and no source-kind=agent-launch label.
        // Use a synchronous validation error here; this route-boundary spec
        // must not start an AgentJob and wait for runner/report completion.
        using var validate = await _fixture.Client.PostAsJsonAsync(
            AgentJobController.ValidatePath,
            new { workspace = new { path = "/tmp/validation-only" } });
        Assert.Equal(HttpStatusCode.BadRequest, validate.StatusCode);
        var validatePayload = await validate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(validatePayload.GetProperty("success").GetBoolean());
        Assert.Equal("validation_failed", validatePayload.GetProperty("code").GetString());

        using var launch = await _fixture.Client.LaunchAgentSessionAsync(
            projectId,
            "agent_unknown",
            new { prompt = "distinctness check" });
        Assert.Equal(HttpStatusCode.NotFound, launch.StatusCode);

        Assert.NotEqual(AgentJobController.ValidatePath,
            $"/api/projects/{projectId}/agents/agent_unknown/sessions");
    }

}
