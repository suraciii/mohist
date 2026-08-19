using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Workspace.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workspace;

public class WorkspaceEntityApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly string _projectId;

    public WorkspaceEntityApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _projectId = CreateProjectAsync().GetAwaiter().GetResult();
    }

    private async Task<string> CreateProjectAsync()
    {
        var raw = $"weapi-{Guid.NewGuid():N}".ToLowerInvariant();
        var name = raw.Length > 63 ? raw[..63] : raw;
        using var create = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "server", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        create.EnsureSuccessStatusCode();
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("CreateProject returned no id");
    }

    private async Task<JsonElement> CreateWorkspaceAsync(string name)
    {
        using var create = await _fixture.Client.PostAsJsonAsync(
            $"/api/projects/{_projectId}/workspaces",
            new { name, repos = new[] { "server" } });
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
    }

    private async Task SeedBoundSessionAsync(string workspaceName, bool active)
    {
        var sessionId = $"agent-session-ws-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            "runner-a",
            "opencode",
            WorkDir: null,
            Metadata: new AgentSessionMetadata()
                .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, _projectId)
                .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "agent-launch")
                .WithLabel(GenericAgentSessionMetadata.AgentId, "agent-ws")
                .WithLabel(AgentSessionMetadata.WorkspaceNameKey, workspaceName)));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-session-1"));
        if (active)
        {
            var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
            await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
                new AgentSessionRuntimeEventInput[]
                {
                    new(RuntimeEventTypes.SessionActivity, "{\"activity\":\"active\"}")
                },
                "runtime-session-1"));
            await persistence.WaitAsync();
        }
    }

    private async Task<JsonElement> ListAsync()
    {
        using var response = await _fixture.Client.GetAsync($"/api/projects/{_projectId}/workspaces");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
    }

    private JsonElement FindWorkspace(JsonElement list, string name)
    {
        foreach (var item in list.EnumerateArray())
        {
            if (item.GetProperty("name").GetString() == name)
                return item;
        }
        throw new Xunit.Sdk.XunitException($"Workspace '{name}' not found in list response");
    }

    [Fact]
    public async Task List_ReturnsNameOriginStatusHomeAndBoundSessionCount()
    {
        var name = $"pay-{Guid.NewGuid():N}";
        await CreateWorkspaceAsync(name);
        await SeedBoundSessionAsync(name, active: false);

        var list = await ListAsync();

        var workspace = FindWorkspace(list, name);
        Assert.Equal("active", workspace.GetProperty("status").GetString());
        Assert.Equal("manual", workspace.GetProperty("origin").GetProperty("kind").GetString());
        Assert.Equal(["server"], workspace.GetProperty("repositories")
            .EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.Equal(1, workspace.GetProperty("boundSessionCount").GetInt32());
        Assert.False(workspace.TryGetProperty("home", out _));
        Assert.NotNull(workspace.GetProperty("createdAt").GetString());
        Assert.False(workspace.TryGetProperty("archivedAt", out _));
    }

    [Fact]
    public async Task List_ArchivedWorkspaceCarriesArchivedAtAndZeroBoundSessions()
    {
        var name = $"arch-{Guid.NewGuid():N}";
        await CreateWorkspaceAsync(name);
        using var close = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/workspaces/{name}/close", null);
        close.EnsureSuccessStatusCode();

        var list = await ListAsync();

        var workspace = FindWorkspace(list, name);
        Assert.Equal("archived", workspace.GetProperty("status").GetString());
        Assert.Equal(0, workspace.GetProperty("boundSessionCount").GetInt32());
        Assert.NotNull(workspace.GetProperty("archivedAt").GetString());
    }

    [Fact]
    public async Task Detail_ReturnsBoundSessions()
    {
        var name = $"det-{Guid.NewGuid():N}";
        await CreateWorkspaceAsync(name);
        await SeedBoundSessionAsync(name, active: true);

        using var response = await _fixture.Client.GetAsync($"/api/projects/{_projectId}/workspaces/{name}");
        response.EnsureSuccessStatusCode();
        var detail = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        var sessions = detail.GetProperty("sessions").EnumerateArray().ToList();
        Assert.NotEmpty(sessions);
        Assert.All(sessions, session =>
        {
            Assert.NotNull(session.GetProperty("id").GetString());
            Assert.NotNull(session.GetProperty("createdAt").GetString());
        });
        Assert.Equal(sessions.Count, detail.GetProperty("boundSessionCount").GetInt32());
    }

    [Fact]
    public async Task Close_ActiveBoundSession_ReturnsConflictWithNextStepHint()
    {
        var name = $"close-{Guid.NewGuid():N}";
        await CreateWorkspaceAsync(name);
        await SeedBoundSessionAsync(name, active: true);

        using var response = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/workspaces/{name}/close", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("workspace_has_active_sessions", body.GetProperty("code").GetString());
        Assert.Contains("active bound session", body.GetProperty("error").GetString());
        Assert.Contains("mo session list --workspace", body.GetProperty("details").GetProperty("hint").GetString());
    }

    [Fact]
    public async Task Close_NoBoundSessions_ArchivesWorkspace()
    {
        var name = $"closeok-{Guid.NewGuid():N}";
        await CreateWorkspaceAsync(name);

        using var response = await _fixture.Client.PostAsync(
            $"/api/projects/{_projectId}/workspaces/{name}/close", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("archived", data.GetProperty("status").GetString());
        Assert.NotNull(data.GetProperty("archivedAt").GetString());
    }
}
