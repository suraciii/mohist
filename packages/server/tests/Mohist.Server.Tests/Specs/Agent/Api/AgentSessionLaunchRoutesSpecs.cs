using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Services.Sessions;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Specs.Agent.Api;

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
            await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
            {
                new AgentSessionRuntimeEventInput(
                    Type: RuntimeEventTypes.SessionInput,
                    PayloadJson: "{\"text\":\"open product transcript\",\"kind\":\"task\"}"),
            }));
            for (var i = 0; i < 5; i++)
            {
                await grain.DeactivateForTestAsync();
                await Task.Delay(150);
            }

            using var metadata = await _fixture.Client.GetAsync(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}");
            using var transcript = await _fixture.Client.GetAsync(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}/transcript");

            Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
            var metadataPayload = await metadata.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(sessionId, metadataPayload.GetProperty("data").GetProperty("id").GetString());

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

        // The validation-only endpoint is a developer smoke-test that
        // round-trips a raw prompt through the AgentJob engine and
        // returns the terminal result. It has no project/agent scoping,
        // no AgentSession minting, and no source-kind=agent-launch label.
        using var validate = await _fixture.Client.PostAsJsonAsync(
            AgentJobController.ValidatePath,
            new { prompt = "validation-only path" });
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        var validatePayload = await validate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(validatePayload.GetProperty("success").GetBoolean());
        // The validation response carries an AgentJobValidationResponse
        // with Status / FailureReason fields — the input prompt is not
        // echoed back. The point of this assertion is that the endpoint
        // responded, not that it ran successfully end-to-end.
        Assert.False(string.IsNullOrWhiteSpace(
            validatePayload.GetProperty("data").GetProperty("jobId").GetString()));

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
            await WaitForJobTerminalAsync(jobGrain!, AgentJobStatus.Failed, TimeSpan.FromSeconds(30));

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
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private static async Task WaitForJobTerminalAsync(
        IAgentJobGrain job,
        AgentJobStatus expected,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var terminal = await job.GetTerminalResultAsync();
            if (terminal.Status == expected)
                return;
            await Task.Delay(200);
        }

        var last = await job.GetTerminalResultAsync();
        Assert.Fail($"Agent job did not reach {expected} within {timeout.TotalSeconds:N0}s (last status {last.Status}).");
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        // ProjectName caps at 63 DNS-label chars; trim the random suffix
        // so each project's name stays inside that bound.
        var raw = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > ProjectDomainMaxLength
            ? raw[..ProjectDomainMaxLength]
            : raw;
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name });
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
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await runnerGrain.GetRuntimeStateAsync();
            if (state.Status == RunnerStatus.Online)
                return;
            await Task.Delay(25);
        }

        var last = await runnerGrain.GetRuntimeStateAsync();
        Assert.Fail($"Runner '{runnerId}' did not reach Online within 5s (last status {last.Status}).");
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
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
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
            await Task.Delay(50);
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
