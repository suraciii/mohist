using Mohist.Server.Infrastructure;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;
namespace Mohist.Server.SpecTests.Specs.Agent.Api;

[Collection("MohistIntegration")]
public class AgentSessionLaunchValidationRoutesSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentSessionLaunchValidationRoutesSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Launch_NonPositiveEpicContext_Returns400WithoutCreatingSession(int epicNumber)
    {
        var projectId = await CreateProjectAsync("launch-invalid-epic");
        var agent = await CreateAgentAsync(projectId, "invalid-epic-agent");
        var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
            new { prompt = "invalid context", context = new { epicNumber } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(sessionCountBefore, await CountAgentLaunchSessionsAsync(projectId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Launch_NonPositiveIssueContext_Returns400WithoutCreatingSession(int issueNumber)
    {
        var projectId = await CreateProjectAsync("launch-invalid-issue");
        var agent = await CreateAgentAsync(projectId, "invalid-issue-agent");
        var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
            new { prompt = "invalid context", context = new { issueNumber } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(sessionCountBefore, await CountAgentLaunchSessionsAsync(projectId));
    }

    [Fact]
    public async Task Launch_OpaqueEpicContext_Returns400WithoutCreatingSession()
    {
        var projectId = await CreateProjectAsync("launch-opaque-epic");
        var agent = await CreateAgentAsync(projectId, "opaque-epic-agent");
        var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
            new { prompt = "invalid context", context = new { epicNumber = "epic-7" } });

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

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
            new { prompt = "cross project context", context = new { epicNumber = otherEpicNumber } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(sessionCountBefore, await CountAgentLaunchSessionsAsync(projectId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n  ")]
    public async Task Launch_EmptyOrWhitespacePrompt_Returns400_WithoutCreatingSessionOrJob(string prompt)
    {
        var projectId = await CreateProjectAsync("launch-bad-prompt");
        var agent = await CreateAgentAsync(projectId, "bad-prompt-agent");

        var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
            new { prompt });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Contains("prompt", payload.GetProperty("error").GetString()!);

        var sessionCountAfter = await CountAgentLaunchSessionsAsync(projectId);
        Assert.Equal(sessionCountBefore, sessionCountAfter);
    }

    [Fact]
    public async Task Launch_MissingPromptField_Returns400_WithoutCreatingSessionOrJob()
    {
        var projectId = await CreateProjectAsync("launch-missing-prompt");
        var agent = await CreateAgentAsync(projectId, "missing-prompt-agent");

        var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
            new { context = new { issueNumber = 1 } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var sessionCountAfter = await CountAgentLaunchSessionsAsync(projectId);
        Assert.Equal(sessionCountBefore, sessionCountAfter);
    }

    [Fact]
    public async Task Launch_UnknownAgent_Returns404_WithoutCreatingSessionOrJob()
    {
        var projectId = await CreateProjectAsync("launch-unknown");

        var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/agent_{Guid.NewGuid():N}/sessions",
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
        var runnerId = $"launch-archived-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "archived-launch-agent");
        using var archive = await _fixture.Client.DeleteAsync($"/api/projects/{projectId}/agents/{agent.Id}");
        archive.EnsureSuccessStatusCode();
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            var sessionCountBefore = await CountAgentLaunchSessionsAsync(projectId);

            using var response = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
                new { prompt = "should not launch" });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(payload.GetProperty("success").GetBoolean());
            Assert.Equal("agent_archived", payload.GetProperty("code").GetString());

            var sessionCountAfter = await CountAgentLaunchSessionsAsync(projectId);
            Assert.Equal(sessionCountBefore, sessionCountAfter);
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            Assert.Equal(HttpStatusCode.NoContent, poll.StatusCode);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_ResolvesAgentByName_WhenAgentRefIsFriendlyName()
    {
        var projectId = await CreateProjectAsync("launch-name");
        var runnerId = $"launch-name-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "name-fallback");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{projectId}/agents/name-fallback/sessions",
                new { prompt = "by name please" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = payload.GetProperty("data");
            Assert.Equal(agent.Id, data.GetProperty("agentId").GetString());
            Assert.Equal("name-fallback", data.GetProperty("agentName").GetString());
        }
        finally
        {
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

        using var launch = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/agent_unknown/sessions",
            new { prompt = "distinctness check" });
        Assert.Equal(HttpStatusCode.NotFound, launch.StatusCode);

        Assert.NotEqual(AgentJobController.ValidatePath,
            $"/api/projects/{projectId}/agents/agent_unknown/sessions");
    }

    [Fact]
    public async Task Launch_PolledDispatch_CarriesMintedAgentSessionIdVerbatimWithNoWorkflowRunId()
    {
        var projectId = await CreateProjectAsync("launch-dispatch-contract");
        var runnerId = $"launch-dispatch-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "dispatch-contract-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
                new { prompt = "dispatch contract guard" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var mintedSessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(mintedSessionId));

            var polled = await PollDispatchForSessionAsync(runnerId, mintedSessionId);

            // Launch-route regression guard: the dispatch envelope the
            // runner picks up must carry the minted AgentSessionId verbatim
            // as a non-null AgentSessionId with no workflowRunId. A
            // null-dispatch regression would fail this assertion.
            Assert.Equal(string.Empty, polled.WorkflowRunId);
            Assert.Equal(mintedSessionId, polled.AgentSessionId);
            Assert.False(string.IsNullOrWhiteSpace(polled.AgentSessionId));
            Assert.False(string.IsNullOrWhiteSpace(polled.WorkId));
            Assert.Equal(WorkDispatchOwnerKinds.AgentJob, polled.OwnerKind);
            Assert.Equal(projectId, polled.ProjectId);
            Assert.False(string.IsNullOrWhiteSpace(polled.AgentJobId));
        }
        finally
        {
            await DrainDispatchAsync(runnerId);
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_CompletedAgentJob_RecordsSessionClosedCompleted_AndResolvesCompletedStatus()
    {
        var projectId = await CreateProjectAsync("launch-completed-terminal");
        var runnerId = $"launch-completed-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "completed-terminal-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
                new { prompt = "complete the generic session" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;

            var polled = await PollDispatchForSessionAsync(runnerId, sessionId);
            Assert.False(string.IsNullOrWhiteSpace(polled.AgentJobId));

            var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(polled.AgentJobId!);
            var report = await jobGrain.ReportResultAsync(
                runnerId,
                polled.WorkId,
                new WorkResult(
                    Status: "completed",
                    Message: "generic job completed",
                    Output: JSON.DeserializeElement("{}"),
                    ArtifactUploadIds: null,
                    ExitCode: 0));
            Assert.True(report.Accepted, "AgentJob rejected completed report");

            var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 1, _fixture.Grains);
            var closePayload = Assert.Single(await LoadSessionClosedPayloadsAsync(dbFactory, sessionId));
            Assert.Equal("completed", closePayload.GetProperty("status").GetString());
            Assert.Equal(0, closePayload.GetProperty("exitCode").GetInt32());

            using var summary = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agent-sessions/{sessionId}");
            Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
            var summaryPayload = await summary.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("completed", summaryPayload.GetProperty("data").GetProperty("status").GetString());

            using var list = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents/{agent.Id}/sessions");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var listPayload = await list.Content.ReadFromJsonAsync<JsonElement>();
            var item = listPayload.GetProperty("data").EnumerateArray()
                .Single(entry => entry.GetProperty("sessionId").GetString() == sessionId);
            Assert.Equal("completed", item.GetProperty("status").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_AgentJobTimeout_TransitionsGenericSessionToTerminalFailedState()
    {
        var projectId = await CreateProjectAsync("launch-timeout");
        var runnerId = $"launch-timeout-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "timeout-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
                new { prompt = "this will never finish" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;

            var jobGrain = await FindAgentJobGrainAsync(sessionId);
            Assert.NotNull(jobGrain);

            // The fixture configures JobTimeout=8s. Wait for the grain
            // timer to fire and OnJobTimeoutAsync to run. After timeout,
            // the AgentJob is Failed and the session has been transitioned
            // to a terminal state via a synthesized session.closed runtime
            // event.
            await WaitForJobTerminalAsync(
                jobGrain!,
                AgentJobStatus.Failed,
                TimeSpan.FromSeconds(30),
                async () =>
                {
                    _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(9));
                    await jobGrain!.CheckTimeoutsAsync();
                });

            var terminal = await jobGrain!.GetTerminalResultAsync();
            Assert.Equal(AgentJobStatus.Failed, terminal.Status);
            Assert.Equal(AgentJobFailureReasons.ReportTimeout, terminal.FailureReason);

            var query = await GetAgentSessionQueryAsync();
            var record = await query.FirstByLabelsAsync(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                });
            Assert.NotNull(record);
            Assert.Equal(sessionId, record!.Session.Id);
            Assert.Equal(agent.Id, record.Session.Metadata.Label(GenericAgentSessionMetadata.AgentId));

            var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await dbFactory.WaitForTranscriptPartsAsync(sessionId, 1, _fixture.Grains);
            var closePayload = Assert.Single(await LoadSessionClosedPayloadsAsync(dbFactory, sessionId));
            Assert.Equal("failed", closePayload.GetProperty("status").GetString());
            Assert.Contains(AgentJobFailureReasons.ReportTimeout, closePayload.GetProperty("failureReason").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

}
