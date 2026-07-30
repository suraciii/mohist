using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
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
public class AgentSessionLaunchRoutesSpecs : AgentSessionLaunchRoutesTestSupport
{
    public AgentSessionLaunchRoutesSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Launch_NeedsSetup_ReturnsGapsBeforeCreatingSessionOrJob()
    {
        var projectId = await CreateProjectAsync("launch-readiness-gate");
        using var created = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents",
            new
            {
                name = "malformed-model-agent",
                instructions = "run the task",
                agentConfig = new { model = "malformed" },
            });
        created.EnsureSuccessStatusCode();
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var agentId = createdBody.GetProperty("data").GetProperty("id").GetString()!;

        using var view = await _fixture.Client.GetAsync($"/api/projects/{projectId}/agents/{agentId}");
        var viewBody = await view.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Needs setup", viewBody.GetProperty("data").GetProperty("readiness").GetProperty("conclusion").GetString());

        var sessionsBefore = await CountAgentLaunchSessionsAsync(projectId);
        var jobsBefore = await CountJobsAsync(projectId, agentId);
        using var launch = await LaunchAsync(projectId, agentId, new { prompt = "do it" });
        Assert.Equal(HttpStatusCode.Conflict, launch.StatusCode);
        var launchBody = await launch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_needs_setup", launchBody.GetProperty("code").GetString());
        Assert.Contains("model-reference-malformed", launchBody.GetProperty("details").GetProperty("gaps").EnumerateArray().Select(g => g.GetProperty("code").GetString()));
        Assert.Equal(sessionsBefore, await CountAgentLaunchSessionsAsync(projectId));
        Assert.Equal(jobsBefore, await CountJobsAsync(projectId, agentId));
    }

    private async Task<int> CountJobsAsync(string projectId, string agentId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<AgentJobQuerier>();
        return (await jobs.ListByAgentAsync(projectId, agentId, limit: 200)).Count;
    }

    [Fact]
    public async Task Launch_ResolvesAgent_ComposesSnapshot_MintsSession_Returns201_WithIdentityAndStatus()
    {
        var projectId = await CreateProjectAsync("launch-201");
        var runnerId = $"launch-201-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "reviewer");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
                {
                    prompt = "Refactor the auth module",
                });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(payload.GetProperty("success").GetBoolean());
            var data = payload.GetProperty("data");

            var sessionId = data.GetProperty("sessionId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            // The launch surfaces BOTH the AgentJob identity and the
            // AgentSession identity; jobId is the grain key minted by
            // the launcher, no id translation.
            var jobId = data.GetProperty("jobId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(jobId));
            Assert.StartsWith("agent-job-launch-", jobId!, StringComparison.Ordinal);
            Assert.Equal(agent.Id, data.GetProperty("agentId").GetString());
            Assert.Equal("reviewer", data.GetProperty("agentName").GetString());
            Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("status").GetString()));
            Assert.Equal(
                $"/api/projects/{projectId}/agent-sessions/{sessionId}/transcript",
                data.GetProperty("transcriptUrl").GetString());
            Assert.Equal(
                $"/api/projects/{projectId}/agent-jobs/{jobId}",
                data.GetProperty("jobUrl").GetString());

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

    [Fact]
    public async Task Launch_GenericSession_IsReadableByProductMetadataAndTranscriptRoutes()
    {
        var projectId = await CreateProjectAsync("launch-read-session");
        var runnerId = $"launch-read-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "readable-agent");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "open product transcript" });

            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = launchPayload.GetProperty("data").GetProperty("sessionId").GetString()!;

            var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-launch-read"));
            var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
            await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(new[]
            {
                new AgentSessionRuntimeEventInput(
                    Type: RuntimeEventTypes.SessionInput,
                    PayloadJson: "{\"text\":\"open product transcript\",\"kind\":\"task\"}"),
            }, "runtime-launch-read"));
            await persistence.WaitAsync();

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

    [Fact]
    public async Task Launch_RecordsContextRefs_OnSessionMetadata_AsPromptContextOnly()
    {
        var projectId = await CreateProjectAsync("launch-ctx");
        var runnerId = $"launch-ctx-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "ctx-agent");
        var issueNumber = await CreateIssueAsync(projectId, "Context issue");
        var epicNumber = await CreateEpicAsync(projectId, "Context epic");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
                {
                    prompt = "look at the issue",
                    context = new
                    {
                        issueNumber,
                        epicNumber,
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
            Assert.Equal(issueNumber.ToString(), record!.Session.Metadata.Label(GenericAgentSessionMetadata.IssueNumber));
            Assert.Equal(epicNumber.ToString(), record.Session.Metadata.Label(GenericAgentSessionMetadata.EpicNumber));
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
        // Agent config runtime is pi; no override in the request body.
        // The resolved backend must be pi.
        var projectId = await CreateProjectAsync("launch-runtime-from-config");
        var runnerId = $"launch-runtime-from-config-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "config-runtime-agent", runtime: "pi");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "execute on pi via config" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;

            var sessionGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            var sessionInfo = await sessionGrain.GetAsync();
            Assert.NotNull(sessionInfo);
            Assert.Equal("pi", sessionInfo!.Runtime);

            var snapshot = await PollDispatchForSessionAsync(runnerId, sessionId);
            var polledDispatch = await PollDispatchEnvelopeAsync(runnerId, snapshot.WorkId!);
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
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "default runtime" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;

            var sessionGrain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
            var sessionInfo = await sessionGrain.GetAsync();
            Assert.NotNull(sessionInfo);
            Assert.Equal("opencode", sessionInfo!.Runtime);

            var snapshot = await PollDispatchForSessionAsync(runnerId, sessionId);
            var polledDispatch = await PollDispatchEnvelopeAsync(runnerId, snapshot.WorkId!);
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
        // Issue-452 design D1: AgentConfigSchema.Validate rejects an
        // unknown runtime on the Agent CRUD write surface.
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
        // Snapshot fixation (issue-452 D2): editing the Agent's runtime
        // config after launch must not change the in-flight job's
        // runtime. The dispatch envelope and the session must remain on
        // the launch-time runtime.
        var projectId = await CreateProjectAsync("launch-snapshot-fixed");
        var runnerId = $"launch-snapshot-fixed-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "snapshot-agent", runtime: "pi");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new { prompt = "snapshot" });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;

            var snapshot = await PollDispatchForSessionAsync(runnerId, sessionId);
            var firstDispatch = await PollDispatchEnvelopeAsync(runnerId, snapshot.WorkId!);
            Assert.Equal("pi", ReadRuntimeFromDispatch(firstDispatch));

            // Edit the Agent's runtime to opencode while the job is
            // still in flight.
            await PatchAgentRuntimeAsync(projectId, agent.Id, "opencode");

            // The in-flight dispatch already pinned pi; the runner
            // envelope still carries pi. Re-poll to confirm.
            var sessionInfo = await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId).GetAsync();
            Assert.Equal("pi", sessionInfo!.Runtime);
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    private async Task<JsonElement> PollDispatchEnvelopeAsync(string runnerId, string workId)
    {
        for (var i = 0; i < 50; i++)
        {
            using var poll = await _fixture.Client.PostAsync($"/api/runner/{runnerId}/poll", content: null);
            var dispatches = await poll.ReadDispatchElementsAsync();
            foreach (var data in dispatches)
            {
                if (string.Equals(data.GetProperty("workId").GetString(), workId, StringComparison.Ordinal))
                    return data;
                await DrainDispatchElementAsync(runnerId, data);
            }
        }

        throw new InvalidOperationException($"No polled dispatch for workId '{workId}'");
    }

    private static string ReadRuntimeFromDispatch(JsonElement dispatch)
    {
        // WorkDispatchResponse.With is a serialized JSON string carrying
        // the agent-job payload (prompt/instructions/model/variant/runtime).
        // Parse the inner object and read the runtime field.
        var withJson = dispatch.GetProperty("with").GetString();
        Assert.False(string.IsNullOrWhiteSpace(withJson));
        using var doc = JsonDocument.Parse(withJson!);
        return doc.RootElement.GetProperty("runtime").GetString()!;
    }
}
