using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

/// <summary>
/// Issue-512 T-002 specs for Unknown-safe launch reconciliation and
/// the composite launch-observation read API. Covers:
/// - composite observation during queued / running / terminal / Unknown
/// - repeated observation does not mutate state
/// - cross-project isolation returns 404
/// - report timeout leaves the Job Unknown (not Failed, not Idle)
/// - authoritative terminal report from the original Runner resolves the
///   original Job/Turn
/// - launch 201 surfaces all four stable references plus the observation URL
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
    public async Task Observation_DuringQueuedState_ReportsAcceptedInputAndPendingTurn()
    {
        var projectId = await CreateProjectAsync("obs-queued");
        var agent = await CreateAgentAsync(projectId, "obs-queued-agent");

        using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "no runner online" });
        Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
        var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        var data = launchPayload.GetProperty("data");
        var jobId = data.GetProperty("jobId").GetString()!;

        // No runner registered → Job stays Pending. The Session's
        // first Input is accepted and the first Turn is queued.
        var observation = await ReadObservationAsync(projectId, jobId);
        Assert.NotNull(observation);
         var obs = observation!.Value.GetProperty("data");
        Assert.Equal("pending", obs.GetProperty("jobStatus").GetString());
        Assert.Equal("accepted", obs.GetProperty("inputAcceptance").GetString());
        Assert.Equal("queued", obs.GetProperty("turnStatus").GetString());
        Assert.False(string.IsNullOrWhiteSpace(obs.GetProperty("sessionId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(obs.GetProperty("inputId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(obs.GetProperty("turnId").GetString()));
    }

    [Fact]
    public async Task Observation_DuringTerminalState_ReportsJobResultAndTurnResult()
    {
        var projectId = await CreateProjectAsync("obs-terminal");
        var runnerId = $"obs-terminal-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "obs-terminal-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "complete me" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = launchPayload.GetProperty("data").GetProperty("jobId").GetString()!;

            var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
            var claim = await ClaimPreparedAgentJobAsync(jobId, runnerId, projectId, sessionId);
            var persistence = _fixture.Persistence.Checkpoint(sessionId);
            var report = await jobGrain.ReportResultAsync(
                runnerId,
                claim.WorkId,
                new WorkResult(
                    Status: "completed",
                    Message: "all done",
                    Output: JSON.DeserializeElement("{}"),
                    ArtifactUploadIds: null,
                    ExitCode: 0));
            Assert.True(report.Accepted, "AgentJob rejected completed report");

            var observation = await ReadObservationAsync(projectId, jobId);
            Assert.NotNull(observation);
             var obs = observation!.Value.GetProperty("data");
            Assert.Equal("completed", obs.GetProperty("jobStatus").GetString());
            Assert.Equal("completed", obs.GetProperty("turnStatus").GetString());
            Assert.Equal("accepted", obs.GetProperty("inputAcceptance").GetString());
            // The Job terminal message and the Turn result are surfaced
            // through the same composite read.
            var turnResult = obs.GetProperty("turnResult");
            Assert.NotEqual(JsonValueKind.Null, turnResult.ValueKind);
            Assert.Equal("all done", turnResult.GetProperty("message").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Observation_AfterUnknownTransition_ReportsJobUnknownWithoutCreatingNewInputsOrTurns()
    {
        var projectId = await CreateProjectAsync("obs-unknown");
        var runnerId = $"obs-unknown-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "obs-unknown-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "will time out" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = launchPayload.GetProperty("data").GetProperty("jobId").GetString()!;
            var originalInputId = launchPayload.GetProperty("data").GetProperty("inputId").GetString()!;
            var originalTurnId = launchPayload.GetProperty("data").GetProperty("turnId").GetString()!;

            var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
            await ClaimPreparedAgentJobAsync(jobId, runnerId, projectId, sessionId);

            // This route owns the Unknown projection, not the clock-driven
            // timeout transition. Enter Unknown through its production boundary.
            await jobGrain.MarkUnknownAsync(
                $"{AgentJobFailureReasons.ReportTimeout}: observation projection");
            var terminal = await jobGrain.GetTerminalResultAsync();
            Assert.Equal(AgentJobStatus.Unknown, terminal.Status);

            await jobGrain.ReceiveReminder(AgentJobGrain.RecoveryReminderName, default);

            var observation = await ReadObservationAsync(projectId, jobId);
            Assert.NotNull(observation);
             var obs = observation!.Value.GetProperty("data");
            Assert.Equal("unknown", obs.GetProperty("jobStatus").GetString());
            Assert.Equal("unknown", obs.GetProperty("turnStatus").GetString());

            // The original Input / Turn ids are preserved; the
            // observation surface did not mint replacements.
            Assert.Equal(originalInputId, obs.GetProperty("inputId").GetString());
            Assert.Equal(originalTurnId, obs.GetProperty("turnId").GetString());
            Assert.Equal("accepted", obs.GetProperty("inputAcceptance").GetString());
            // Failure reason surfaces so the caller can decide to
            // re-read or retry with the original key.
            Assert.False(string.IsNullOrWhiteSpace(obs.GetProperty("jobFailureReason").GetString()));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Observation_DuringRunnerLoss_ProjectsRecoveringReasonAndDeadline_ThenSettles()
    {
        var projectId = await CreateProjectAsync("obs-recovering");
        var runnerId = $"obs-recovering-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "obs-recovering-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(
                projectId,
                agent.Id,
                new { prompt = "recover this launch" });
            var launchData = (await launch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            var jobId = launchData.GetProperty("jobId").GetString()!;
            var sessionId = launchData.GetProperty("sessionId").GetString()!;
            var job = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
            var claim = await ClaimPreparedAgentJobAsync(jobId, runnerId, projectId, sessionId);
            var deadline = _fixture.TimeProvider.GetUtcNow().AddMinutes(5);

            await job.MarkUnknownAsync(AgentJobFailureReasons.RunnerLost, deadline);

            var observation = await ReadObservationAsync(projectId, jobId);
            Assert.NotNull(observation);
            var data = observation!.Value.GetProperty("data");
            Assert.Equal("recovering", data.GetProperty("jobStatus").GetString());
            Assert.Equal(AgentJobFailureReasons.RunnerLost, data.GetProperty("jobFailureReason").GetString());
            Assert.True(
                data.TryGetProperty("recoveryDeadlineAt", out var recoveryDeadline),
                data.GetRawText());
            Assert.Equal(deadline, recoveryDeadline.GetDateTimeOffset());

            var report = await job.ReportResultAsync(
                runnerId,
                claim.WorkId,
                new WorkResult("completed", "recovered"));
            Assert.True(report.Accepted);
            var settled = await ReadObservationAsync(projectId, jobId);
            Assert.NotNull(settled);
            var settledData = settled!.Value.GetProperty("data");
            Assert.Equal("completed", settledData.GetProperty("jobStatus").GetString());
            Assert.True(
                !settledData.TryGetProperty("recoveryDeadlineAt", out var settledDeadline)
                || settledDeadline.ValueKind == JsonValueKind.Null);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Observation_RepeatedReads_DoNotCreateAdditionalInputsOrTurns()
    {
        var projectId = await CreateProjectAsync("obs-repeat");
        var agent = await CreateAgentAsync(projectId, "obs-repeat-agent");

        using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "read me twice" });
        Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
        var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = launchPayload.GetProperty("data").GetProperty("jobId").GetString()!;

        var first = await ReadObservationAsync(projectId, jobId);
        var second = await ReadObservationAsync(projectId, jobId);
        Assert.NotNull(first);
        Assert.NotNull(second);

         var firstObs = first!.Value.GetProperty("data");
         var secondObs = second!.Value.GetProperty("data");
        Assert.Equal(firstObs.GetProperty("inputId").GetString(), secondObs.GetProperty("inputId").GetString());
        Assert.Equal(firstObs.GetProperty("turnId").GetString(), secondObs.GetProperty("turnId").GetString());
        Assert.Equal(firstObs.GetProperty("sessionId").GetString(), secondObs.GetProperty("sessionId").GetString());
        Assert.Equal(firstObs.GetProperty("jobStatus").GetString(), secondObs.GetProperty("jobStatus").GetString());

        var sessionsBefore = await CountAgentLaunchSessionsAsync(projectId);
        await ReadObservationAsync(projectId, jobId);
        var sessionsAfter = await CountAgentLaunchSessionsAsync(projectId);
        Assert.Equal(sessionsBefore, sessionsAfter);
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

    [Fact]
    public async Task Observation_AfterAuthoritativeTerminalReport_ResolvesUnknownToCompleted()
    {
        var projectId = await CreateProjectAsync("obs-resolve");
        var runnerId = $"obs-resolve-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "obs-resolve-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "recover to terminal" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = launchPayload.GetProperty("data").GetProperty("jobId").GetString()!;

            var jobGrain = await FindAgentJobGrainAsync(sessionId);
            Assert.NotNull(jobGrain);
            var claim = await ClaimPreparedAgentJobAsync(jobId, runnerId, projectId, sessionId);

            await jobGrain!.MarkUnknownAsync(
                $"{AgentJobFailureReasons.ReportTimeout}: observation reconciliation");
            Assert.Equal(AgentJobStatus.Unknown, await jobGrain.GetStatusAsync());

            // Reconciliation: an authoritative terminal report from
            // the original Runner resolves the same Job and Turn to
            // Completed — no second dispatch, no new Input/Turn.
            var persistence = _fixture.Persistence.Checkpoint(sessionId);
            var report = await jobGrain!.ReportResultAsync(
                runnerId,
                claim.WorkId,
                new WorkResult(
                    Status: "completed",
                    Message: "reconciled",
                    Output: JSON.DeserializeElement("{}"),
                    ArtifactUploadIds: null,
                    ExitCode: 0));
            Assert.True(report.Accepted, "Reconciliation report was rejected");

            var observation = await ReadObservationAsync(projectId, jobId);
            Assert.NotNull(observation);
             var obs = observation!.Value.GetProperty("data");
            Assert.Equal("completed", obs.GetProperty("jobStatus").GetString());
            Assert.Equal("completed", obs.GetProperty("turnStatus").GetString());
            // Original identity preserved across reconciliation.
            Assert.Equal(sessionId, obs.GetProperty("sessionId").GetString());
            Assert.Equal(launchPayload.GetProperty("data").GetProperty("inputId").GetString(),
                obs.GetProperty("inputId").GetString());
            Assert.Equal(launchPayload.GetProperty("data").GetProperty("turnId").GetString(),
                obs.GetProperty("turnId").GetString());
        }
        finally
        {
            await DrainDispatchAsync(runnerId);
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Observation_AfterCompletedJob_LeavesAgentSessionUsable()
    {
        // Spec: a completed initial Job leaves its AgentSession usable;
        // the Session activity is independent of the Job verdict.
        var projectId = await CreateProjectAsync("obs-session-usable");
        var runnerId = $"obs-session-usable-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "obs-session-usable-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "completed first" });
            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;
            var jobId = launchPayload.GetProperty("data").GetProperty("jobId").GetString()!;

            var claim = await ClaimPreparedAgentJobAsync(jobId, runnerId, projectId, sessionId);
            var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(jobId);
            var persistence = _fixture.Persistence.Checkpoint(sessionId);
            await jobGrain.ReportResultAsync(
                runnerId,
                claim.WorkId,
                new WorkResult(
                    Status: "completed",
                    Message: "ok",
                    Output: JSON.DeserializeElement("{}"),
                    ArtifactUploadIds: null,
                    ExitCode: 0));

            var observation = await ReadObservationAsync(projectId, jobId);
            Assert.NotNull(observation);
             var obs = observation!.Value.GetProperty("data");
            Assert.Equal("completed", obs.GetProperty("jobStatus").GetString());

            // Session must remain present and queryable; the read
            // surface still resolves it.
            using var session = await _fixture.Client.GetAsync(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}");
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_RejectsUnknownJobIdOnObservationRoute_Returns404()
    {
        var projectId = await CreateProjectAsync("obs-not-found");

        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agent-jobs/agent-job-launch-{Guid.NewGuid():N}/launch-observation");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<JsonElement?> ReadObservationAsync(string projectId, string jobId)
    {
        using var response = await _fixture.Client.GetAsync(
            $"/api/projects/{projectId}/agent-jobs/{jobId}/launch-observation");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
