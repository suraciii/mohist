using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.L1Tests.Specs.Agent.Api;

/// <summary>
/// Issue-512 T-002 route specs for the launch-observation read API. Covers:
/// - launch 201 surfaces all four stable references plus the observation URL
/// - project-scoped observation reads preserve the cross-project 404 boundary
/// Projection details for queued, terminal, and recovering states are covered
/// by the L0 assembler specs; lifecycle/reconciliation remains covered by the
/// AgentJob grain specs.
/// </summary>
[Collection("LaunchIntegration")]
public class AgentLaunchObservationRoutesSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentLaunchObservationRoutesSpecs(IsolatedMohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Launch_ReturnsAllFourStableReferencesAndObservationUrl()
    {
        var projectId = await CreateProjectAsync("obs-201-ids");
        var runnerId = $"obs-201-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "obs-id-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "return four ids" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var data = launchPayload.GetProperty("data");

            var jobId = data.GetProperty("jobId").GetString();
            var sessionId = data.GetProperty("sessionId").GetString();
            var inputId = data.GetProperty("inputId").GetString();
            var turnId = data.GetProperty("turnId").GetString();
            var observationUrl = data.GetProperty("observationUrl").GetString();

            Assert.False(string.IsNullOrWhiteSpace(jobId));
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            Assert.False(string.IsNullOrWhiteSpace(inputId));
            Assert.False(string.IsNullOrWhiteSpace(turnId));
            Assert.Equal(
                $"/api/projects/{projectId}/agent-jobs/{jobId}/launch-observation",
                observationUrl);
        }
        finally
        {
            await DrainDispatchAsync(runnerId);
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Observation_CrossProjectJob_Returns404()
    {
        var projectA = await CreateProjectAsync("obs-proj-a");
        var projectB = await CreateProjectAsync("obs-proj-b");
        var agent = await CreateAgentAsync(projectA, "obs-cross-agent");

        using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectA, agent.Id, new { prompt = "cross project" });
        Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
        var jobId = (await launch.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("jobId").GetString()!;

        // Cross-project read must NOT return the Job — even though it
        // exists, it belongs to project A.
        using var crossRead = await _fixture.Client.GetAsync(
            $"/api/projects/{projectB}/agent-jobs/{jobId}/launch-observation");
        Assert.Equal(HttpStatusCode.NotFound, crossRead.StatusCode);

        // Same-project read returns 200.
        using var sameRead = await _fixture.Client.GetAsync(
            $"/api/projects/{projectA}/agent-jobs/{jobId}/launch-observation");
        Assert.Equal(HttpStatusCode.OK, sameRead.StatusCode);
    }

}
