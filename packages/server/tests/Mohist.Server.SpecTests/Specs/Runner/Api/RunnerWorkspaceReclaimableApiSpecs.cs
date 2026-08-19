using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Api;

/// <summary>
/// Runner-scoped reclaimability observation for the named-workspace
/// cleanup guard: the runner asks the server whether a workspace it
/// materialized may be reclaimed (archived, or no active bound session).
/// </summary>
public sealed class RunnerWorkspaceReclaimableApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public RunnerWorkspaceReclaimableApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Reclaimable_ActiveWorkspaceWithActiveBoundSession_ReportsActiveAndCountsSession()
    {
        var (projectId, workspaceName) = await CreateProjectAndWorkspaceAsync("reclaim-active");
        await CreateBoundSessionAsync(projectId, workspaceName, "runtime-active", AgentSessionActivity.Unknown);

        using var response = await _fixture.Client.GetAsync(
            $"/api/runner/runner-1/workspaces/{projectId}/{workspaceName}/reclaimable");

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        Assert.Equal("active", data.GetProperty("status").GetString());
        Assert.Equal(1, data.GetProperty("activeBoundSessions").GetInt32());
    }

    [Fact]
    public async Task Reclaimable_ActiveWorkspaceWithoutActiveBoundSessions_ReportsZeroCount()
    {
        var (projectId, workspaceName) = await CreateProjectAndWorkspaceAsync("reclaim-idle");
        await CreateBoundSessionAsync(projectId, workspaceName, "runtime-idle", AgentSessionActivity.Idle);

        using var response = await _fixture.Client.GetAsync(
            $"/api/runner/runner-1/workspaces/{projectId}/{workspaceName}/reclaimable");

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        Assert.Equal("active", data.GetProperty("status").GetString());
        Assert.Equal(0, data.GetProperty("activeBoundSessions").GetInt32());
    }

    [Fact]
    public async Task Reclaimable_ActiveWorkspaceWithoutAnySession_ReportsZeroCount()
    {
        var (projectId, workspaceName) = await CreateProjectAndWorkspaceAsync("reclaim-empty");

        using var response = await _fixture.Client.GetAsync(
            $"/api/runner/runner-1/workspaces/{projectId}/{workspaceName}/reclaimable");

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        Assert.Equal("active", data.GetProperty("status").GetString());
        Assert.Equal(0, data.GetProperty("activeBoundSessions").GetInt32());
    }

    [Fact]
    public async Task Reclaimable_ArchivedWorkspace_ReportsArchived()
    {
        var (projectId, workspaceName) = await CreateProjectAndWorkspaceAsync("reclaim-archived");
        using (var response = await _fixture.Client.PostAsync(
                   $"/api/projects/{projectId}/workspaces/{workspaceName}/close",
                   content: null))
        {
            response.EnsureSuccessStatusCode();
        }

        using var reclaimable = await _fixture.Client.GetAsync(
            $"/api/runner/runner-1/workspaces/{projectId}/{workspaceName}/reclaimable");

        reclaimable.EnsureSuccessStatusCode();
        var payload = await reclaimable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("archived", payload.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Reclaimable_UnknownWorkspace_ReturnsNotFound()
    {
        using var response = await _fixture.Client.GetAsync(
            $"/api/runner/runner-1/workspaces/unknown-project/never-created/reclaimable");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", payload.GetProperty("code").GetString());
    }

    private async Task<(string ProjectId, string WorkspaceName)> CreateProjectAndWorkspaceAsync(string prefix)
    {
        var raw = $"{prefix}-{Guid.NewGuid():N}".ToLowerInvariant();
        var projectName = raw.Length > 63 ? raw[..63] : raw;
        using var createProject = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name = projectName,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        createProject.EnsureSuccessStatusCode();
        var projectBody = await createProject.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectBody.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("CreateProject returned no id");

        var workspaceName = $"ws-{Guid.NewGuid():N}";
        using var createWorkspace = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{projectId}/workspaces",
            new { name = workspaceName, repositories = new[] { "main" } });
        createWorkspace.EnsureSuccessStatusCode();
        return (projectId, workspaceName);
    }

    private async Task<string> CreateBoundSessionAsync(
        string projectId,
        string workspaceName,
        string runtimeSessionId,
        AgentSessionActivity activity)
    {
        var sessionId = $"session-reclaim-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            "runner-1",
            "opencode",
            WorkDir: "/work",
            Metadata: new AgentSessionMetadata()
                .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, projectId)
                .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "agent-launch")
                .WithLabel(GenericAgentSessionMetadata.AgentId, "agent-reclaim")
                .WithLabel(AgentSessionQueryMetadataKeys.WorkspaceName, workspaceName)));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId));
        if (activity != AgentSessionActivity.Idle)
        {
            var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
            await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
                new[]
                {
                    new AgentSessionRuntimeEventInput(
                        RuntimeEventTypes.SessionActivity,
                        $"{{\"activity\":\"{activity.ToString().ToLowerInvariant()}\"}}")
                },
                runtimeSessionId));
            await persistence.WaitAsync();
        }
        return sessionId;
    }
}
