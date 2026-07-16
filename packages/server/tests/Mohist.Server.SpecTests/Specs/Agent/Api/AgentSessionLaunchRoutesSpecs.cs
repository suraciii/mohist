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
public class AgentSessionLaunchRoutesSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentSessionLaunchRoutesSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Launch_ResolvesAgent_ComposesSnapshot_MintsSession_Returns201_WithIdentityAndStatus()
    {
        var projectId = await CreateProjectAsync("launch-201");
        var runnerId = $"launch-201-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "reviewer");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
                new
                {
                    prompt = "Refactor the auth module",
                });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(payload.GetProperty("success").GetBoolean());
            var data = payload.GetProperty("data");

            var sessionId = data.GetProperty("sessionId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            Assert.Equal(agent.Id, data.GetProperty("agentId").GetString());
            Assert.Equal("reviewer", data.GetProperty("agentName").GetString());
            Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("status").GetString()));
            Assert.Equal(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}/transcript",
                data.GetProperty("transcriptUrl").GetString());

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

            Assert.Equal("reviewer", record.Session.Metadata.Label(GenericAgentSessionMetadata.AgentName));
            Assert.Equal(projectId, record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId));

            var snapshot = await FindAgentJobSnapshotAsync(sessionId!);
            Assert.NotNull(snapshot);
            Assert.Equal(runnerId, snapshot!.RunnerId);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.CurrentWorkId));
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Launch_GenericSession_IsReadableByProductMetadataAndTranscriptRoutes()
    {
        var projectId = await CreateProjectAsync("launch-read-session");
        var runnerId = $"launch-read-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "readable-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
                new { prompt = "open product transcript" });

            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;

            var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-launch-read"));
            await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
            {
                new AgentSessionRuntimeEventInput(
                    Type: RuntimeEventTypes.SessionInput,
                    PayloadJson: "{\"text\":\"open product transcript\",\"kind\":\"task\"}"),
            }, "runtime-launch-read"));
            await grain.FlushForTestAsync();

            using var metadata = await _fixture.Client.GetAsync(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}");
            using var transcript = await _fixture.Client.GetAsync(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}/transcript");

            Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
            var metadataPayload = await metadata.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(sessionId, metadataPayload.GetProperty("data").GetProperty("sessionId").GetString());
            Assert.Equal(agent.Id, metadataPayload.GetProperty("data").GetProperty("agentId").GetString());
            Assert.Equal("readable-agent", metadataPayload.GetProperty("data").GetProperty("agentName").GetString());

            Assert.Equal(HttpStatusCode.OK, transcript.StatusCode);
            var transcriptPayload = await transcript.Content.ReadFromJsonAsync<JsonElement>();
            var transcriptData = transcriptPayload.GetProperty("data");
            Assert.True(transcriptData.GetProperty("turns").GetArrayLength() >= 1);
            Assert.Equal("open product transcript", transcriptData.GetProperty("turns")[0].GetProperty("user").GetProperty("text").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task Launch_RecordsContextRefs_OnSessionMetadata_AsPromptContextOnly()
    {
        var projectId = await CreateProjectAsync("launch-ctx");
        var runnerId = $"launch-ctx-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "ctx-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.PostAsJsonAsync(
                $"/api/projects/{projectId}/agents/{agent.Id}/sessions",
                new
                {
                    prompt = "look at the issue",
                    context = new
                    {
                        issueNumber = 42,
                        epicNumber = "epic-7",
                        repository = "feature-repo",
                        workspacePath = "/tmp/launch-ctx",
                    },
                });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;

            var query = await GetAgentSessionQueryAsync();
            var record = await query.FirstByLabelsAsync(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                    [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                });

            Assert.NotNull(record);
            Assert.Equal("42", record!.Session.Metadata.Label(GenericAgentSessionMetadata.IssueNumber));
            Assert.Equal("epic-7", record.Session.Metadata.Label(GenericAgentSessionMetadata.EpicNumber));
            Assert.Equal("feature-repo", record.Session.Metadata.Label(GenericAgentSessionMetadata.Repository));
            Assert.Equal("/tmp/launch-ctx", record.Session.Metadata.Label(GenericAgentSessionMetadata.WorkspacePath));

            Assert.Null(record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.WorkflowRunId));
            Assert.Null(record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.SessionName));
            Assert.Equal(sessionId, record.Session.Id);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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
                    Output: "{}",
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    private static async Task WaitForJobTerminalAsync(
        IAgentJobGrain job,
        AgentJobStatus expected,
        TimeSpan timeout,
        Func<Task>? advance = null)
        => await TestWait.ForAsync(
            () => job.GetTerminalResultAsync(),
            t => t.Status == expected,
            timeout,
            TimeSpan.FromMilliseconds(200),
            $"Agent job to reach {expected}",
            advance);

    private async Task<PollSnapshot> PollDispatchForSessionAsync(string runnerId, string expectedSessionId)
    {
        var attempts = 50;
        for (var i = 0; i < attempts; i++)
        {
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            var dispatches = await poll.ReadDispatchElementsAsync();
            PollSnapshot? match = null;
            var others = new List<JsonElement>();
            foreach (var data in dispatches)
            {
                var polledSessionId = data.TryGetProperty("agentSessionId", out var sessionIdElement)
                    && sessionIdElement.ValueKind != JsonValueKind.Null
                    ? sessionIdElement.GetString()
                    : null;
                if (match is null && polledSessionId == expectedSessionId)
                {
                    var workId = data.GetProperty("workId").GetString() ?? string.Empty;
                    var agentJobId = data.TryGetProperty("agentJobId", out var agentJobIdElement)
                        && agentJobIdElement.ValueKind != JsonValueKind.Null
                        ? agentJobIdElement.GetString()
                        : null;
                    var projectId = data.TryGetProperty("projectId", out var projectIdElement)
                        && projectIdElement.ValueKind != JsonValueKind.Null
                        ? projectIdElement.GetString()
                        : null;
                    var ownerKind = data.TryGetProperty("ownerKind", out var ownerKindElement)
                        && ownerKindElement.ValueKind != JsonValueKind.Null
                        ? ownerKindElement.GetString()
                        : null;
                    match = new PollSnapshot(
                        WorkflowRunId: data.GetProperty("workflowRunId").GetString() ?? string.Empty,
                        WorkId: workId,
                        AgentJobId: agentJobId,
                        ProjectId: projectId,
                        AgentSessionId: polledSessionId,
                        OwnerKind: ownerKind);
                }
                else
                {
                    others.Add(data);
                }
            }

            foreach (var other in others)
                await DrainDispatchElementAsync(runnerId, other);

            if (match is not null) return match;
        }

        throw new InvalidOperationException($"No polled dispatch carrying AgentSessionId='{expectedSessionId}' after {attempts} attempts");
    }

    private async Task DrainDispatchAsync(string runnerId)
    {
        for (var i = 0; i < 30; i++)
        {
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            var dispatches = await poll.ReadDispatchElementsAsync();
            if (dispatches.Count == 0) return;
            foreach (var data in dispatches)
                await DrainDispatchElementAsync(runnerId, data);
        }
    }

    private async Task DrainDispatchElementAsync(string runnerId, JsonElement data)
    {
        var workId = data.GetProperty("workId").GetString();
        var ownerKind = data.TryGetProperty("ownerKind", out var ownerKindElement)
            && ownerKindElement.ValueKind != JsonValueKind.Null
            ? ownerKindElement.GetString()
            : null;

        if (!string.Equals(ownerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
            return;

        var agentJobId = data.TryGetProperty("agentJobId", out var agentJobIdElement)
            && agentJobIdElement.ValueKind != JsonValueKind.Null
            ? agentJobIdElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(agentJobId) || string.IsNullOrWhiteSpace(workId))
            return;

        var jobGrain = _fixture.Grains.GetGrain<IAgentJobGrain>(agentJobId!);
        var report = await jobGrain.ReportResultAsync(
            runnerId,
            workId!,
            new WorkResult(
                Status: "completed",
                Message: "drained",
                Output: "{}",
                ArtifactUploadIds: null,
                ExitCode: 0));
        Assert.True(report.Accepted, "AgentJob rejected drain report");
    }

    private sealed record PollSnapshot(
        string WorkflowRunId,
        string WorkId,
        string? AgentJobId,
        string? ProjectId,
        string? AgentSessionId,
        string? OwnerKind);

    private async Task<string> CreateProjectAsync(string prefix)
    {
        // ProjectName caps at 63 DNS-label chars; trim the random suffix
        // so each project's name stays inside that bound.
        var raw = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > ProjectDomainMaxLength
            ? raw[..ProjectDomainMaxLength]
            : raw;
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"CreateProject '{name}' failed: {(int)response.StatusCode} {body}");
        }
        var bodyElement = await response.Content.ReadFromJsonAsync<JsonElement>();
        return bodyElement.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"CreateProject '{name}' returned no id");
    }

    private const int ProjectDomainMaxLength = 63;

    private async Task<AgentRef> CreateAgentAsync(string projectId, string name)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name,
                description = $"description for {name}",
                instructions = $"instructions for {name}",
                agentConfig = new { type = "opencode" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new AgentRef(body.GetProperty("data").GetProperty("id").GetString()!, name);
    }

    private async Task RegisterRunnerAndAwaitOnlineAsync(string runnerId, string projectId)
    {
        await _fixture.Client.PostOkAsync($"/api/runner/{runnerId}/register", new
        {
            capabilities = new[] { "spec/*" },
            hostname = $"{runnerId}-host",
            projectId,
        });
        await _fixture.Client.PatchOkAsync($"/api/runner/{runnerId}", new { slots = 2 });

        var runnerGrain = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
        await TestWait.ForAsync(
            () => runnerGrain.GetRuntimeStateAsync(),
            s => s.Status == RunnerStatus.Online,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            $"Runner '{runnerId}' to reach Online");
    }

    private async Task<int> CountAgentLaunchSessionsAsync(string projectId)
    {
        var query = await GetAgentSessionQueryAsync();
        var records = await query.ListByLabelsAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
            });
        return records.Count;
    }

    private static async Task<IReadOnlyList<JsonElement>> LoadSessionClosedPayloadsAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        string sessionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var turnIds = await db.AgentSessionTranscriptTurns
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.Id)
            .ToArrayAsync();
        var payloads = await db.AgentSessionTranscriptParts
            .AsNoTracking()
            .Where(p => turnIds.Contains(p.TurnId) && p.Type == TranscriptPartTypes.SessionClosed)
            .OrderBy(p => p.Sequence)
            .Select(p => p.PayloadJson)
            .ToArrayAsync();
        return payloads.Select(payload => JsonSerializer.Deserialize<JsonElement>(payload)).ToArray();
    }

    private async Task<AgentSessionQuery> GetAgentSessionQueryAsync()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<AgentSessionQuery>();
    }

    /// <summary>
    /// Looks up the AgentJob grain that owns the dispatched work for the
    /// given generic session id. The launch endpoint mints a fresh
    /// <c>agent-job-launch-{guid}</c> key per launch, so this probes the
    /// runner registry to find the active work item and recovers the key.
    /// Polls until the runner picks up the dispatch (the grain's
    /// <see cref="AgentJobGrain.TryDispatchAsync"/> runs asynchronously
    /// after <c>SubmitAsync</c> returns, so the work may not be visible
    /// on the runner's active list the instant the 201 is observed).
    /// </summary>
    private async Task<IAgentJobGrain?> FindAgentJobGrainAsync(string sessionId)
    {
        return await TestWait.ForAsync(
            ProbeAgentJobGrainAsync,
            job => job is not null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(50),
            $"agent job grain for session '{sessionId}'");
    }

    private async Task<IAgentJobGrain?> ProbeAgentJobGrainAsync()
    {
        var registry = _fixture.Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var runners = await registry.ListRunnerIdsAsync();
        foreach (var runnerId in runners)
        {
            var runner = _fixture.Grains.GetGrain<IRunnerGrain>(runnerId);
            var state = await runner.GetRuntimeStateAsync();
            foreach (var work in state.ActiveWorks)
            {
                if (string.Equals(work.OwnerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal))
                {
                    var job = _fixture.Grains.GetGrain<IAgentJobGrain>(work.OwnerId);
                    var snapshot = await job.GetRuntimeSnapshotAsync();
                    if (snapshot.CurrentWorkId == work.WorkId)
                        return job;
                }
            }
        }

        return null;
    }

    private async Task<AgentJobRuntimeSnapshot?> FindAgentJobSnapshotAsync(string sessionId)
    {
        var job = await FindAgentJobGrainAsync(sessionId);
        return job is null ? null : await job.GetRuntimeSnapshotAsync();
    }

    private sealed record AgentRef(string Id, string Name);
}
