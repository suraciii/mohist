using Mohist.Server.SpecTests.Support;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Api;

[Collection("MohistIntegration")]
public class AgentDefinitionApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentDefinitionApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task Create_ReturnsCreatedActiveAgent()
    {
        var project = await CreateProjectAsync("agent-create");

        using var response = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/agents", NewAgent("reviewer"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadDataAsync<AgentDto>();
        Assert.StartsWith("agent_", created.Id);
        Assert.Equal(project.Id, created.ProjectId);
        Assert.Equal("reviewer", created.Name);
        Assert.Equal("active", created.Status);
        Assert.NotEqual(default, DateTimeOffset.Parse(created.CreatedAt));
        Assert.NotEqual(default, DateTimeOffset.Parse(created.UpdatedAt));
    }

    [Fact]
    public async Task Create_RequiresResolvedProjectAndRejectsDuplicateName()
    {
        var project = await CreateProjectAsync("agent-create-conflict");
        await _client.PostDataAsync<AgentDto>($"/api/projects/{project.Id}/agents", NewAgent("same"));

        using var missingProject = await _client.PostAsJsonAsync($"/api/projects/{Guid.NewGuid():N}/agents", NewAgent("orphan"));
        using var duplicate = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/agents", NewAgent("same"));
        var list = await _client.GetDataAsync<AgentDto[]>($"/api/projects/{project.Id}/agents?all=true");

        Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Single(list);
    }

    [Fact]
    public async Task List_FiltersByStatusAndProject()
    {
        var firstProject = await CreateProjectAsync("agent-list-a");
        var secondProject = await CreateProjectAsync("agent-list-b");
        var active = await _client.PostDataAsync<AgentDto>($"/api/projects/{firstProject.Id}/agents", NewAgent("active-agent"));
        var archived = await _client.PostDataAsync<AgentDto>($"/api/projects/{firstProject.Id}/agents", NewAgent("archived-agent"));
        await _client.PostDataAsync<AgentDto>($"/api/projects/{secondProject.Id}/agents", NewAgent("other-project"));
        await _client.DeleteAsync($"/api/projects/{firstProject.Id}/agents/{archived.Id}");

        var defaultList = await _client.GetDataAsync<AgentDto[]>($"/api/projects/{firstProject.Id}/agents");
        var allList = await _client.GetDataAsync<AgentDto[]>($"/api/projects/{firstProject.Id}/agents?all=true");
        var archivedList = await _client.GetDataAsync<AgentDto[]>($"/api/projects/{firstProject.Id}/agents?status=archived");

        Assert.Equal([active.Id], defaultList.Select(agent => agent.Id).ToArray());
        Assert.Contains(allList, agent => agent.Id == active.Id);
        Assert.Contains(allList, agent => agent.Id == archived.Id);
        Assert.DoesNotContain(allList, agent => agent.Name == "other-project");
        var onlyArchived = Assert.Single(archivedList);
        Assert.Equal(archived.Id, onlyArchived.Id);
    }

    [Fact]
    public async Task Show_ReturnsArchivedByIdAndRejectsUnknownOrCrossProject()
    {
        var firstProject = await CreateProjectAsync("agent-show-a");
        var secondProject = await CreateProjectAsync("agent-show-b");
        var created = await _client.PostDataAsync<AgentDto>($"/api/projects/{firstProject.Id}/agents", NewAgent("show-me"));
        await _client.DeleteAsync($"/api/projects/{firstProject.Id}/agents/{created.Id}");

        var shown = await _client.GetDataAsync<AgentDto>($"/api/projects/{firstProject.Id}/agents/{created.Id}");
        using var unknown = await _client.GetAsync($"/api/projects/{firstProject.Id}/agents/agent_{Guid.NewGuid():N}");
        using var crossProject = await _client.GetAsync($"/api/projects/{secondProject.Id}/agents/{created.Id}");

        Assert.Equal("archived", shown.Status);
        Assert.Equal(created.Id, shown.Id);
        Assert.False(string.IsNullOrWhiteSpace(shown.CreatedAt));
        Assert.False(string.IsNullOrWhiteSpace(shown.UpdatedAt));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossProject.StatusCode);
    }

    [Fact]
    public async Task Patch_UpdatesMutableFieldsAndRejectsImmutableUnknownAndRenameConflict()
    {
        var project = await CreateProjectAsync("agent-patch");
        var first = await _client.PostDataAsync<AgentDto>($"/api/projects/{project.Id}/agents", NewAgent("first"));
        var second = await _client.PostDataAsync<AgentDto>($"/api/projects/{project.Id}/agents", NewAgent("second"));
        var before = DateTimeOffset.Parse(first.UpdatedAt);
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));

        var patched = await _client.PatchDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{first.Id}", new
        {
            name = "first-renamed",
            description = "after",
            instructions = "new instructions",
            agentConfig = new { model = "openai/gpt-5.5" },
            skills = new[] { "review", "debug" },
            maxConcurrentRuns = 3
        });
        using var immutable = await _client.PatchAsJsonAsync($"/api/projects/{project.Id}/agents/{first.Id}", new { id = "agent_nope" });
        using var conflict = await _client.PatchAsJsonAsync($"/api/projects/{project.Id}/agents/{first.Id}", new { name = second.Name });
        using var unknown = await _client.PatchAsJsonAsync($"/api/projects/{project.Id}/agents/agent_{Guid.NewGuid():N}", new { name = "missing" });
        var afterConflict = await _client.GetDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{first.Id}");

        Assert.Equal("first-renamed", patched.Name);
        Assert.Equal("after", patched.Description);
        Assert.Equal("new instructions", patched.Instructions);
        Assert.Equal(["review", "debug"], patched.Skills);
        Assert.Equal(3, patched.MaxConcurrentRuns);
        Assert.Equal("openai/gpt-5.5", patched.AgentConfig!.Value.GetProperty("model").GetString());
        Assert.True(DateTimeOffset.Parse(patched.UpdatedAt) > before);
        Assert.Equal(HttpStatusCode.BadRequest, immutable.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("first-renamed", afterConflict.Name);
    }

    [Fact]
    public async Task Patch_ClearsExplicitNullOptionalFields()
    {
        var project = await CreateProjectAsync("agent-patch-clear");
        var created = await _client.PostDataAsync<AgentDto>($"/api/projects/{project.Id}/agents", NewAgent("clear-me"));

        var patched = await _client.PatchDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{created.Id}", new
        {
            description = (string?)null,
            agentConfig = (object?)null,
            skills = (string[]?)null,
            maxConcurrentRuns = (int?)null
        });

        Assert.Equal(string.Empty, patched.Description);
        Assert.Null(patched.AgentConfig);
        Assert.Empty(patched.Skills);
        Assert.Null(patched.MaxConcurrentRuns);
    }

    [Fact]
    public async Task Delete_ArchivesAndKeepsNameOccupiedWithProjectIsolation()
    {
        var firstProject = await CreateProjectAsync("agent-delete-a");
        var secondProject = await CreateProjectAsync("agent-delete-b");
        var created = await _client.PostDataAsync<AgentDto>($"/api/projects/{firstProject.Id}/agents", NewAgent("delete-me"));
        var before = DateTimeOffset.Parse(created.UpdatedAt);
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));

        using var crossProject = await _client.DeleteAsync($"/api/projects/{secondProject.Id}/agents/{created.Id}");
        var archived = await DeleteDataAsync<AgentDto>($"/api/projects/{firstProject.Id}/agents/{created.Id}");
        using var recreate = await _client.PostAsJsonAsync($"/api/projects/{firstProject.Id}/agents", NewAgent("delete-me"));
        using var unknown = await _client.DeleteAsync($"/api/projects/{firstProject.Id}/agents/agent_{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, crossProject.StatusCode);
        Assert.Equal("archived", archived.Status);
        Assert.True(DateTimeOffset.Parse(archived.UpdatedAt) > before);
        Assert.Equal(HttpStatusCode.Conflict, recreate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Unarchive_ReversesArchiveAndAdvancesUpdatedAt()
    {
        var project = await CreateProjectAsync("agent-unarchive-a");
        var created = await _client.PostDataAsync<AgentDto>($"/api/projects/{project.Id}/agents", NewAgent("restore-me"));
        await DeleteDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{created.Id}");
        var archivedShown = await _client.GetDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{created.Id}");
        var archivedAt = DateTimeOffset.Parse(archivedShown.UpdatedAt);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var unarchived = await _client.PostDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{created.Id}/unarchive");

        Assert.Equal(created.Id, unarchived.Id);
        Assert.Equal("active", unarchived.Status);
        Assert.True(DateTimeOffset.Parse(unarchived.UpdatedAt) > archivedAt);

        var reShown = await _client.GetDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{created.Id}");
        Assert.Equal("active", reShown.Status);

        var defaultList = await _client.GetDataAsync<AgentDto[]>($"/api/projects/{project.Id}/agents");
        Assert.Contains(defaultList, agent => agent.Id == created.Id);
    }

    [Fact]
    public async Task Unarchive_IsNoOpForActiveAgentAndReturnsNotFoundForUnknown()
    {
        var project = await CreateProjectAsync("agent-unarchive-b");
        var created = await _client.PostDataAsync<AgentDto>($"/api/projects/{project.Id}/agents", NewAgent("already-active"));
        var before = DateTimeOffset.Parse(created.UpdatedAt);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var result = await _client.PostDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{created.Id}/unarchive");

        Assert.Equal("active", result.Status);
        Assert.Equal(before, DateTimeOffset.Parse(result.UpdatedAt));

        using var unknown = await _client.PostAsync($"/api/projects/{project.Id}/agents/agent_{Guid.NewGuid():N}/unarchive", null);
        using var crossProject = await _client.PostAsync($"/api/projects/{Guid.NewGuid():N}/agents/{created.Id}/unarchive", null);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossProject.StatusCode);
    }

    [Fact]
    public async Task Unarchive_DoesNotAlterPatchSemanticsForStatusField()
    {
        var project = await CreateProjectAsync("agent-unarchive-c");
        var created = await _client.PostDataAsync<AgentDto>($"/api/projects/{project.Id}/agents", NewAgent("status-ignored"));
        var before = DateTimeOffset.Parse(created.UpdatedAt);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var patched = await _client.PatchDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{created.Id}", new
        {
            status = "archived",
            description = "still active",
        });

        Assert.Equal("active", patched.Status);
        Assert.Equal("still active", patched.Description);
        Assert.True(DateTimeOffset.Parse(patched.UpdatedAt) > before);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("livenessQuietThresholdMs")]
    [InlineData("probeTimeoutMs")]
    [InlineData("sessionStartTimeoutMs")]
    [InlineData("compaction")]
    public async Task CreateAgent_WithForbiddenAgentConfigKey_Returns400(string forbiddenKey)
    {
        var project = await CreateProjectAsync("agent-create-forbidden");

        var agentConfig = new Dictionary<string, object?>
        {
            [forbiddenKey] = "value",
        };
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents",
            new
            {
                name = $"agent-{forbiddenKey}",
                description = "agent description",
                instructions = "instructions",
                agentConfig,
                skills = Array.Empty<string>(),
                maxConcurrentRuns = 1,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_agent_config", body.GetProperty("code").GetString());
        var error = body.GetProperty("error").GetString() ?? string.Empty;
        Assert.Contains($"agentConfig.{forbiddenKey}", error);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("livenessQuietThresholdMs")]
    [InlineData("probeTimeoutMs")]
    public async Task PatchAgent_WithForbiddenAgentConfigKey_Returns400(string forbiddenKey)
    {
        var project = await CreateProjectAsync("agent-patch-forbidden");
        var created = await _client.PostDataAsync<AgentDto>(
            $"/api/projects/{project.Id}/agents", NewAgent("patch-target"));

        var agentConfig = new Dictionary<string, object?>
        {
            [forbiddenKey] = "value",
        };
        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/agents/{created.Id}",
            new
            {
                agentConfig,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_agent_config", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_WithUnknownAllowedSubagentAgentId_Returns400WithCapabilityReferenceCode()
    {
        var project = await CreateProjectAsync("agent-capability-ref-create");
        var missingAgentId = $"agent_{Guid.NewGuid():N}";

        using var response = await _client.PostAsJsonAsync($"/api/projects/{project.Id}/agents", new
        {
            name = "with-missing-subagent",
            description = "agent description",
            instructions = "instructions",
            agentConfig = new { model = "openai/gpt-5.6" },
            skills = new[] { "coding" },
            maxConcurrentRuns = 1,
            allowedSubagentAgentIds = new[] { missingAgentId },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("agent_capability_reference", body.GetProperty("code").GetString());
        Assert.Contains(missingAgentId, body.GetProperty("error").GetString());
        Assert.Equal(missingAgentId, body.GetProperty("details").GetProperty("agentId").GetString());
    }

    [Fact]
    public async Task Patch_WithUnknownAllowedSubagentAgentId_Returns400WithCapabilityReferenceCode()
    {
        var project = await CreateProjectAsync("agent-capability-ref-patch");
        var created = await _client.PostDataAsync<AgentDto>(
            $"/api/projects/{project.Id}/agents", NewAgent("patch-target"));
        var missingAgentId = $"agent_{Guid.NewGuid():N}";

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/agents/{created.Id}",
            new { allowedSubagentAgentIds = new[] { missingAgentId } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("agent_capability_reference", body.GetProperty("code").GetString());
        Assert.Contains(missingAgentId, body.GetProperty("error").GetString());
        Assert.Equal(missingAgentId, body.GetProperty("details").GetProperty("agentId").GetString());
    }

    [Fact]
    public async Task CreateAndPatch_WithExistingAllowedSubagentAgentIds_Succeeds()
    {
        var project = await CreateProjectAsync("agent-capability-ref-ok");
        var target = await _client.PostDataAsync<AgentDto>(
            $"/api/projects/{project.Id}/agents", NewAgent("sub-target"));

        var parent = await _client.PostDataAsync<AgentDto>(
            $"/api/projects/{project.Id}/agents",
            new
            {
                name = "sub-parent",
                description = "agent description",
                instructions = "instructions",
                agentConfig = new { model = "openai/gpt-5.6" },
                skills = new[] { "coding" },
                maxConcurrentRuns = 1,
                allowedSubagentAgentIds = new[] { target.Id },
            });
        Assert.Equal([target.Id], parent.AllowedSubagentAgentIds!);

        var patched = await _client.PatchDataAsync<AgentDto>(
            $"/api/projects/{project.Id}/agents/{parent.Id}",
            new { allowedSubagentAgentIds = new[] { target.Id, parent.Id } });
        Assert.Equal([target.Id, parent.Id], patched.AllowedSubagentAgentIds!);
    }

    private async Task<ProjectDto> CreateProjectAsync(string prefix) =>
        await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"{prefix}-{Guid.NewGuid():N}");

    private static object NewAgent(string name) => new
    {
        name,
        description = "agent description",
        instructions = $"instructions for {name}",
        agentConfig = new { model = "openai/gpt-5.6" },
        skills = new[] { "coding" },
        maxConcurrentRuns = 1
    };

    private async Task<T> DeleteDataAsync<T>(string path)
    {
        using var response = await _client.DeleteAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.ReadDataAsync<T>();
    }

    private sealed record ProjectDto(string Id);
    private sealed record AgentDto(
        string Id,
        string ProjectId,
        string Name,
        string Description,
        string Instructions,
        JsonElement? AgentConfig,
        string[] Skills,
        int? MaxConcurrentRuns,
        string Status,
        string CreatedAt,
        string UpdatedAt,
        string[]? AllowedSubagentAgentIds);
}
