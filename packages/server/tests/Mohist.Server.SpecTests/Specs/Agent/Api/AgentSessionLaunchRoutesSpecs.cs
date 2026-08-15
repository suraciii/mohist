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
using Mohist.Server.TestSupport;
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
        var workspacesBefore = await CountWorkspacesAsync(projectId);
        using var launch = await LaunchAsync(projectId, agentId, new { prompt = "do it" });
        Assert.Equal(HttpStatusCode.Conflict, launch.StatusCode);
        var launchBody = await launch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_needs_setup", launchBody.GetProperty("code").GetString());
        Assert.Contains("model-reference-malformed", launchBody.GetProperty("details").GetProperty("gaps").EnumerateArray().Select(g => g.GetProperty("code").GetString()));
        Assert.Equal(sessionsBefore, await CountAgentLaunchSessionsAsync(projectId));
        Assert.Equal(jobsBefore, await CountJobsAsync(projectId, agentId));
        Assert.Equal(workspacesBefore, await CountWorkspacesAsync(projectId));
    }

    private async Task<int> CountWorkspacesAsync(string projectId)
    {
        using var response = await _fixture.Client.GetAsync($"/api/projects/{projectId}/workspaces");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetArrayLength();
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
        string? jobId = null;

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
            jobId = data.GetProperty("jobId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(jobId));
            Assert.StartsWith("agent-job-launch-", jobId!, StringComparison.Ordinal);
            Assert.Equal(agent.Id, data.GetProperty("agentId").GetString());
            Assert.Equal("reviewer", data.GetProperty("agentName").GetString());
            Assert.Equal(agent.Id, data.GetProperty("targetId").GetString());
            Assert.Equal("web", data.GetProperty("origin").GetString());
            var workspaceId = data.GetProperty("workspaceId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(workspaceId));
            using var projectResponse = await _fixture.Client.GetAsync($"/api/projects/{projectId}");
            projectResponse.EnsureSuccessStatusCode();
            var projectPayload = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
            var projectName = projectPayload.GetProperty("data").GetProperty("name").GetString();
            Assert.False(string.IsNullOrWhiteSpace(projectName));
            using var workspacesResponse = await _fixture.Client.GetAsync($"/api/projects/{projectId}/workspaces");
            var workspacesPayload = await workspacesResponse.Content.ReadFromJsonAsync<JsonElement>();
            var persistedWorkspace = workspacesPayload.GetProperty("data")
                .EnumerateArray()
                .Single(workspace => string.Equals(workspace.GetProperty("name").GetString(), workspaceId, StringComparison.Ordinal));
            Assert.Equal(workspaceId, persistedWorkspace.GetProperty("name").GetString());
            Assert.Equal("active", persistedWorkspace.GetProperty("status").GetString());
            Assert.Equal("web", persistedWorkspace.GetProperty("origin").GetProperty("kind").GetString());
            Assert.Equal(sessionId, persistedWorkspace.GetProperty("origin").GetProperty("conversationId").GetString());
            Assert.Equal(
                $"/{Uri.EscapeDataString(projectName!)}/sessions/{Uri.EscapeDataString(sessionId!)}",
                data.GetProperty("sessionUrl").GetString());
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
            await CleanupLaunchedAgentJobAsync(runnerId, jobId);
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_GenericSession_IsReadableByProductMetadataAndTranscriptRoutes()
    {
        var projectId = await CreateProjectAsync("launch-read-session");
        var agent = await CreateAgentAsync(projectId, "readable-agent");
        var workspaceName = "launch-read";
        await CreateWorkspaceAsync(projectId, workspaceName);
        string? jobId = null;

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(
                projectId,
                agent.Id,
                new
                {
                    prompt = "open product transcript",
                    context = new { workspace = workspaceName },
                });

            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var launchPayload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var launchData = launchPayload.GetProperty("data");
            var sessionId = launchData.GetProperty("sessionId").GetString()!;
            jobId = launchData.GetProperty("jobId").GetString()!;

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
            await CleanupLaunchedAgentJobAsync(null, jobId);
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
        await CreateWorkspaceAsync(projectId, "pay");
        string? jobId = null;

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
                {
                    prompt = "look at the issue",
                    context = new
                    {
                        issueNumber,
                        epicNumber,
                        repository = "main",
                        workspacePath = "/tmp/launch-ctx",
                        workspace = "pay",
                    },
                });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = payload.GetProperty("data");
            var sessionId = payload.GetProperty("data").GetProperty("sessionId").GetString()!;
            jobId = payload.GetProperty("data").GetProperty("jobId").GetString();
            Assert.Equal("pay", data.GetProperty("workspaceId").GetString());

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
            Assert.Equal("main", record.Session.Metadata.Label(GenericAgentSessionMetadata.Repository));
            Assert.Equal("/tmp/launch-ctx", record.Session.Metadata.Label(GenericAgentSessionMetadata.WorkspacePath));
            Assert.Equal("pay", record.Session.Metadata.Label(GenericAgentSessionMetadata.WorkspaceName));
            Assert.Equal("web", record.Session.Metadata.Label(GenericAgentSessionMetadata.Origin));
            Assert.Equal(agent.Id, record.Session.Metadata.Label(GenericAgentSessionMetadata.TargetId));

            Assert.Null(record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.WorkflowRunId));
            Assert.Null(record.Session.Metadata.Label(AgentSessionQueryMetadataKeys.SessionName));
            Assert.Equal(sessionId, record.Session.Id);
        }
        finally
        {
            await CleanupLaunchedAgentJobAsync(runnerId, jobId);
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }
}
