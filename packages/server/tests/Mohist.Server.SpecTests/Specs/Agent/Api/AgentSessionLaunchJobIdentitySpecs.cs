using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

/// <summary>
/// Issue-479 T-003 specs for the launch identity surface: the launch 201
/// surfaces BOTH the AgentJob identity and the AgentSession identity, the
/// launched <c>jobId</c> is accepted verbatim by the AgentJob read surface
/// (no id translation), and a launch still creates exactly one AgentJob,
/// exactly one AgentSession, and issues exactly one dispatch. The three
/// domain gates (whitespace prompt, unknown agent, archived agent) live in
/// <see cref="AgentSessionLaunchValidationRoutesSpecs"/>; this file owns
/// the identity + exactly-once invariants.
/// </summary>
[Collection("MohistIntegration")]
public class AgentSessionLaunchJobIdentitySpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentSessionLaunchJobIdentitySpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Launch_ReturnedJobId_IsAcceptedVerbatimByAgentJobViewRoute()
    {
        var projectId = await CreateProjectAsync("launch-job-id-roundtrip");
        var runnerId = $"launch-job-roundtrip-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "roundtrip-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "roundtrip the job id" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var data = launchPayload.GetProperty("data");
            var jobId = data.GetProperty("jobId").GetString()!;
            var sessionId = data.GetProperty("sessionId").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(jobId));
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            Assert.NotEqual(jobId, sessionId);

            using var view = await _fixture.Client.GetAsync(
                $"/api/projects/{projectId}/agent-jobs/{jobId}");
            Assert.Equal(HttpStatusCode.OK, view.StatusCode);
            var viewPayload = await view.Content.ReadFromJsonAsync<JsonElement>();
            var viewData = viewPayload.GetProperty("data");
            // The id returned at launch is accepted verbatim by the view
            // route (no id translation). The status is current
            // (pending/running while the runner has not yet reported);
            // the launcher only submits — it does not assert a terminal
            // result.
            Assert.Equal(jobId, viewData.GetProperty("jobId").GetString());
            var status = viewData.GetProperty("status").GetString();
            Assert.Contains(status, new[] { "pending", "running" });
        }
        finally
        {
            await DrainDispatchAsync(runnerId);
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_CreatesExactlyOneAgentJobAndOneAgentSessionAndIssuesOneDispatch()
    {
        var projectId = await CreateProjectAsync("launch-exactly-once");
        var runnerId = $"launch-exactly-once-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "exactly-once-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            var sessionsBefore = await CountAgentLaunchSessionsAsync(projectId);

            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "exactly one of each" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = launchPayload.GetProperty("data").GetProperty("jobId").GetString()!;

            // Exactly one AgentSession: the count grows by exactly one.
            var sessionsAfter = await CountAgentLaunchSessionsAsync(projectId);
            Assert.Equal(sessionsBefore + 1, sessionsAfter);

            // Exactly one AgentJob: exactly one dispatch carrying that
            // session id is observable on the runner, and that dispatch
            // references the launched job id verbatim.
            var polled = await PollDispatchForSessionAsync(jobId, runnerId, sessionId);
            Assert.Equal(jobId, polled.AgentJobId);
            Assert.Equal(sessionId, polled.AgentSessionId);
            Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polled.OwnerKind);
        }
        finally
        {
            await DrainDispatchAsync(runnerId);
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }
}
