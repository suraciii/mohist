using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workspace.Domain;
using Mohist.Server.Workspace.Grains;
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
    public async Task Launch_WebWithoutWorkspace_Returns400WithoutCreatingWorkspace()
    {
        var projectId = await CreateProjectAsync("launch-workspace-required");
        var agent = await CreateAgentAsync(projectId, "workspace-required-agent");
        var workspacesBefore = await CountWorkspacesAsync(projectId);

        using var launch = await LaunchAsync(projectId, agent.Id, new { prompt = "requires an explicit scope" });

        Assert.Equal(HttpStatusCode.BadRequest, launch.StatusCode);
        var payload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("workspace_required", payload.GetProperty("code").GetString());
        Assert.Equal(workspacesBefore, await CountWorkspacesAsync(projectId));
    }

    [Fact]
    public async Task Launch_WebRoute_RejectsSpoofedCliOriginHeaderWithoutCreatingWorkspace()
    {
        var projectId = await CreateProjectAsync("launch-spoofed-cli-origin");
        var agent = await CreateAgentAsync(projectId, "spoofed-cli-origin-agent");
        var workspacesBefore = await CountWorkspacesAsync(projectId);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agents/{agent.Id}/sessions")
        {
            Content = JsonContent.Create(new { prompt = "the header is not a trusted caller" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-Mohist-Launch-Origin", "cli");

        using var launch = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, launch.StatusCode);
        var payload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("workspace_required", payload.GetProperty("code").GetString());
        Assert.Equal(workspacesBefore, await CountWorkspacesAsync(projectId));
    }

    [Fact]
    public async Task Launch_CliRoute_UsesServerOriginMetadataInsteadOfHeader()
    {
        var projectId = await CreateProjectAsync("launch-cli-origin");
        await CreateWorkspaceAsync(projectId, "cli-origin-workspace");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agents/unused-agent/sessions/cli")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-Mohist-Launch-Origin", "web");

        using var launch = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, launch.StatusCode);
        var payload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("input_required", payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Launch_RejectsRepositoryOutsideWorkspaceBeforeCreatingSession()
    {
        var projectId = await CreateProjectAsync("launch-workspace-repository-mismatch");
        var agent = await CreateAgentAsync(projectId, "workspace-repository-agent");
        await CreateWorkspaceAsync(projectId, "launch-scope", new[] { "main" });
        var sessionsBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var launch = await LaunchAsync(projectId, agent.Id, new
        {
            prompt = "use the selected scope",
            context = new { workspace = "launch-scope", repository = "other" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, launch.StatusCode);
        var payload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("repository_workspace_mismatch", payload.GetProperty("code").GetString());
        Assert.Equal(sessionsBefore, await CountAgentLaunchSessionsAsync(projectId));
    }

    [Fact]
    public async Task Launch_CliImplicitWorkspace_AcceptsMemberRepository_AndSnapshotsIt()
    {
        var projectId = await CreateProjectAsync("launch-cli-repository-member");
        var agent = await CreateAgentAsync(projectId, "cli-repository-agent");
        await CreateCliWorkspaceAsync(projectId, new[] { "main" });
        var runnerId = $"launch-cli-repository-runner-{Guid.NewGuid():N}";
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await LaunchCliAsync(projectId, agent.Id, new
            {
                prompt = "use the CLI workspace repository",
                context = new { repository = "main" },
            });

            Assert.Equal(HttpStatusCode.Created, launch.StatusCode);
            var payload = await launch.Content.ReadFromJsonAsync<JsonElement>();
            var data = payload.GetProperty("data");
            var sessionId = data.GetProperty("sessionId").GetString()!;
            var jobId = data.GetProperty("jobId").GetString()!;
            Assert.Equal("cli-current", data.GetProperty("workspaceId").GetString());

            var dispatch = await PollDispatchForSessionAsync(jobId, runnerId, sessionId);
            using var variables = JsonDocument.Parse(dispatch.Dispatch.GetProperty("variables").GetString()!);
            var repository = Assert.Single(
                variables.RootElement.GetProperty("workspace").GetProperty("repositories").EnumerateArray());
            Assert.Equal("main", repository.GetProperty("name").GetString());
        }
        finally
        {
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_CliImplicitWorkspace_RejectsRepositoryOutsideMembership()
    {
        var projectId = await CreateProjectAsync("launch-cli-repository-mismatch");
        var agent = await CreateAgentAsync(projectId, "cli-mismatch-agent");
        await CreateCliWorkspaceAsync(projectId, new[] { "main" });
        var sessionsBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var launch = await LaunchCliAsync(projectId, agent.Id, new
        {
            prompt = "do not use an unbound repository",
            context = new { repository = "other" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, launch.StatusCode);
        var payload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("repository_workspace_mismatch", payload.GetProperty("code").GetString());
        Assert.Equal(sessionsBefore, await CountAgentLaunchSessionsAsync(projectId));
    }

    [Fact]
    public async Task Launch_CliImplicitWorkspace_WithoutMembership_FailsClosedForRepository()
    {
        var projectId = await CreateProjectAsync("launch-cli-repository-unbound");
        var agent = await CreateAgentAsync(projectId, "cli-unbound-agent");
        await CreateCliWorkspaceAsync(projectId, []);
        var sessionsBefore = await CountAgentLaunchSessionsAsync(projectId);

        using var launch = await LaunchCliAsync(projectId, agent.Id, new
        {
            prompt = "a workspace without membership must not widen scope",
            context = new { repository = "main" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, launch.StatusCode);
        var payload = await launch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("repository_workspace_mismatch", payload.GetProperty("code").GetString());
        Assert.Equal(sessionsBefore, await CountAgentLaunchSessionsAsync(projectId));
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
        await CreateWorkspaceAsync(projectId, "launch-readiness-workspace");
        var workspacesBefore = await CountWorkspacesAsync(projectId);
        using var launch = await LaunchAsync(projectId, agentId, new
        {
            prompt = "do it",
            context = new { workspace = "launch-readiness-workspace" },
        });
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

    private async Task CreateCliWorkspaceAsync(string projectId, IReadOnlyList<string> repositories)
    {
        await _fixture.Grains.GetGrain<IWorkspaceGrain>(GrainKey.Workspace(projectId, "cli-current"))
            .CreateAsync(
                "cli-current",
                new WorkspaceOrigin.Cli(),
                repositories,
                DateTimeOffset.UnixEpoch);
    }

    private Task<HttpResponseMessage> LaunchCliAsync(string projectId, string agentId, object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/projects/{projectId}/agents/{agentId}/sessions/cli")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return _fixture.Client.SendAsync(request);
    }

    [Fact]
    public async Task Launch_ResolvesAgent_ComposesSnapshot_MintsSession_Returns201_WithIdentityAndStatus()
    {
        var projectId = await CreateProjectAsync("launch-201");
        var runnerId = $"launch-201-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "reviewer");
        await CreateWorkspaceAsync(projectId, "launch-201-workspace");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var response = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
                {
                    prompt = "Refactor the auth module",
                    context = new { workspace = "launch-201-workspace" },
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
            Assert.Equal("manual", persistedWorkspace.GetProperty("origin").GetProperty("kind").GetString());
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
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }

    [Fact]
    public async Task Launch_GenericSession_IsReadableByProductMetadataAndTranscriptRoutes()
    {
        var projectId = await CreateProjectAsync("launch-read-session");
        var runnerId = $"launch-read-runner-{Guid.NewGuid():N}";
        var agent = await CreateAgentAsync(projectId, "readable-agent");
        await CreateWorkspaceAsync(projectId, "launch-read-workspace");
        await RegisterRunnerAndAwaitOnlineAsync(runnerId, projectId);

        try
        {
            using var launch = await _fixture.Client.LaunchAgentSessionAsync(projectId, agent.Id, new
            {
                prompt = "open product transcript",
                context = new { workspace = "launch-read-workspace" },
            });

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
        await CreateWorkspaceAsync(projectId, "pay");

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
            await _fixture.Client.PostAsync($"/api/runner/{runnerId}/unregister", null);
        }
    }
}
