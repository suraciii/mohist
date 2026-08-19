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

[Collection("IsolatedIntegration")]
public class AgentSessionLaunchRuntimeResolutionSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentSessionLaunchRuntimeResolutionSpecs(IsolatedMohistIntegrationFixture fixture) : base(fixture)
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
                await CompleteClaimedAgentJobAsync(runnerId, claim.AgentJobId, claim.Dispatch.WorkId);
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
