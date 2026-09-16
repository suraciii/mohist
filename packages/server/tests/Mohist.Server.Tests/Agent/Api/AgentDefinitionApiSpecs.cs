using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Tests.Support;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.TestSupport;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.Tests.Agent.Api;

[Trait("level", "L1")]
public class AgentDefinitionApiSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentDefinitionApiSpecs(DefaultMohistIntegrationFixture fixture)
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
        Assert.Equal("Review changes", created.Purpose);
        Assert.Equal(["repo:read", "issue:write"], created.Permissions);
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
        Assert.Single(list, agent => agent.Origin == "project");
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

        // The list read merges the unshadowed built-in Workflow Agents, so
        // the stored Project Agents are the entries marked `project`.
        var storedDefaults = defaultList.Where(agent => agent.Origin == "project").ToArray();
        Assert.Equal([active.Id], storedDefaults.Select(agent => agent.Id).ToArray());
        var builtInNames = defaultList
            .Where(agent => agent.Origin == "built-in")
            .Select(agent => agent.Name)
            .ToArray();
        Assert.Equal(3, builtInNames.Length);
        Assert.Contains(BuiltInAgentCatalog.MohistPlannerName, builtInNames);
        Assert.Contains(BuiltInAgentCatalog.MohistBuilderName, builtInNames);
        Assert.Contains(BuiltInAgentCatalog.MohistReviewerName, builtInNames);
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

        var patched = await _client.PatchDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{first.Id}", new
        {
            name = "first-renamed",
            description = "after",
            purpose = "Validate the release plan",
            instructions = "new instructions",
            agentConfig = new { model = "openai/gpt-5.5" },
            skills = new[] { "review", "debug" },
            permissions = new[] { "repo:read", "artifact:publish" },
            maxConcurrentRuns = 3
        });
        using var immutable = await _client.PatchAsJsonAsync($"/api/projects/{project.Id}/agents/{first.Id}", new { id = "agent_nope" });
        using var conflict = await _client.PatchAsJsonAsync($"/api/projects/{project.Id}/agents/{first.Id}", new { name = second.Name });
        using var unknown = await _client.PatchAsJsonAsync($"/api/projects/{project.Id}/agents/agent_{Guid.NewGuid():N}", new { name = "missing" });
        var afterConflict = await _client.GetDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{first.Id}");

        Assert.Equal("first-renamed", patched.Name);
        Assert.Equal("after", patched.Description);
        Assert.Equal("Validate the release plan", patched.Purpose);
        Assert.Equal("new instructions", patched.Instructions);
        Assert.Equal(["review", "debug"], patched.Skills);
        Assert.Equal(["repo:read", "artifact:publish"], patched.Permissions);
        Assert.Equal(3, patched.MaxConcurrentRuns);
        Assert.Equal("openai/gpt-5.5", patched.AgentConfig!.Value.GetProperty("model").GetString());
        Assert.NotEqual(default, DateTimeOffset.Parse(patched.UpdatedAt));
        Assert.Equal(HttpStatusCode.BadRequest, immutable.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("first-renamed", afterConflict.Name);
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
    public async Task List_MergesUnshadowedBuiltInWorkflowAgents()
    {
        var project = await CreateProjectAsync("agent-list-builtins");

        var list = await _client.GetDataAsync<AgentDto[]>($"/api/projects/{project.Id}/agents");

        var builtIns = list.Where(agent => agent.Origin == "built-in").ToArray();
        Assert.Equal(3, builtIns.Length);
        Assert.Contains(builtIns, agent => agent.Name == BuiltInAgentCatalog.MohistPlannerName);
        Assert.Contains(builtIns, agent => agent.Name == BuiltInAgentCatalog.MohistBuilderName);
        Assert.Contains(builtIns, agent => agent.Name == BuiltInAgentCatalog.MohistReviewerName);
        Assert.DoesNotContain(list, agent => agent.Name == BuiltInAgentCatalog.MohistSlackName);
        Assert.All(builtIns, agent =>
        {
            Assert.StartsWith("builtin:", agent.Id, StringComparison.Ordinal);
            Assert.Equal("active", agent.Status);
            Assert.False(agent.OverridesBuiltIn);
            Assert.False(string.IsNullOrWhiteSpace(agent.Instructions));
            Assert.Equal("pi", agent.EffectiveExecutionConfig?.GetProperty("runtime").GetString());
            Assert.False(
                agent.EffectiveExecutionConfig?.TryGetProperty("model", out var model) == true
                    && model.ValueKind != JsonValueKind.Null,
                "an unset Model stays unset in the effective configuration");
        });
    }

    [Fact]
    public async Task List_ProjectAgentShadowsBuiltInCaseInsensitively_AlsoWhileArchived()
    {
        var project = await CreateProjectAsync("agent-list-shadow");
        var shadow = await _client.PostDataAsync<AgentDto>(
            $"/api/projects/{project.Id}/agents",
            new
            {
                name = "Mohist/Planner",
                description = "project planner",
                instructions = "plan the work",
                agentConfig = new { model = "openai/gpt-5.6" },
            });

        var shadowed = await _client.GetDataAsync<AgentDto[]>($"/api/projects/{project.Id}/agents");
        var entry = Assert.Single(shadowed, agent => string.Equals(agent.Name, "Mohist/Planner", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(shadow.Id, entry.Id);
        Assert.Equal("project", entry.Origin);
        Assert.True(entry.OverridesBuiltIn);
        Assert.DoesNotContain(shadowed, agent =>
            agent.Origin == "built-in" && agent.Name == BuiltInAgentCatalog.MohistPlannerName);
        Assert.Equal(2, shadowed.Count(agent => agent.Origin == "built-in"));

        await _client.DeleteAsync($"/api/projects/{project.Id}/agents/{shadow.Id}");

        // An archived shadow remains the shadowing entry; the built-in never
        // silently reappears.
        var afterArchive = await _client.GetDataAsync<AgentDto[]>($"/api/projects/{project.Id}/agents?all=true");
        var archivedEntry = Assert.Single(afterArchive, agent => agent.Id == shadow.Id);
        Assert.Equal("archived", archivedEntry.Status);
        Assert.True(archivedEntry.OverridesBuiltIn);
        Assert.DoesNotContain(afterArchive, agent =>
            agent.Origin == "built-in" && agent.Name == BuiltInAgentCatalog.MohistPlannerName);
    }

    [Fact]
    public async Task ShowByName_ResolvesBuiltInsAndTheirShadow()
    {
        var project = await CreateProjectAsync("agent-show-builtin");

        var builtIn = await _client.GetDataAsync<AgentDto>(
            $"/api/projects/{project.Id}/agents/by-name/mohist/reviewer");
        Assert.Equal("builtin:mohist/reviewer", builtIn.Id);
        Assert.Equal("built-in", builtIn.Origin);
        Assert.False(builtIn.OverridesBuiltIn);
        Assert.Equal("pi", builtIn.AgentConfig?.GetProperty("runtime").GetString());
        Assert.False(builtIn.AgentConfig?.TryGetProperty("model", out _));
        Assert.Equal("pi", builtIn.EffectiveExecutionConfig?.GetProperty("runtime").GetString());

        using var unknown = await _client.GetAsync($"/api/projects/{project.Id}/agents/by-name/not-a-built-in");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var shadow = await _client.PostDataAsync<AgentDto>(
            $"/api/projects/{project.Id}/agents",
            new
            {
                name = "MOHIST/REVIEWER",
                instructions = "review the change",
                agentConfig = new { model = "openai/gpt-5.6" },
            });

        var shadowRead = await _client.GetDataAsync<AgentDto>(
            $"/api/projects/{project.Id}/agents/by-name/mohist/reviewer");
        Assert.Equal(shadow.Id, shadowRead.Id);
        Assert.Equal("project", shadowRead.Origin);
        Assert.True(shadowRead.OverridesBuiltIn);
    }

    [Fact]
    public async Task Override_MaterializesBuiltInDefinitionAndAppliesCallerChanges()
    {
        var project = await CreateProjectAsync("agent-override-create");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/overrides",
            new
            {
                name = BuiltInAgentCatalog.MohistBuilderName,
                agentConfig = new
                {
                    model = "openai/gpt-5.6-luna",
                    reasoningEffort = "xhigh",
                },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadDataAsync<AgentDto>();
        var builtIn = BuiltInAgentCatalog.Resolve(BuiltInAgentCatalog.MohistBuilderName);
        Assert.Equal("project", created.Origin);
        Assert.True(created.OverridesBuiltIn);
        Assert.Equal(builtIn.Instructions, created.Instructions);
        Assert.Equal(builtIn.Description, created.Description);
        Assert.Empty(created.Skills);
        Assert.Equal("pi", created.AgentConfig?.GetProperty("runtime").GetString());
        Assert.Equal("openai/gpt-5.6-luna", created.AgentConfig?.GetProperty("model").GetString());
        Assert.Equal("xhigh", created.AgentConfig?.GetProperty("reasoningEffort").GetString());

        var list = await _client.GetDataAsync<AgentDto[]>($"/api/projects/{project.Id}/agents");
        Assert.DoesNotContain(list, agent =>
            agent.Origin == "built-in" && agent.Name == BuiltInAgentCatalog.MohistBuilderName);
        Assert.Equal(2, list.Count(agent => agent.Origin == "built-in"));
    }

    [Fact]
    public async Task Override_ActiveSameNameAgentConflictsWithoutCreatingAnything()
    {
        var project = await CreateProjectAsync("agent-override-conflict");
        var existing = await _client.PostDataAsync<AgentDto>(
            $"/api/projects/{project.Id}/agents",
            new
            {
                name = BuiltInAgentCatalog.MohistPlannerName,
                instructions = "plan already here",
                agentConfig = new { model = "openai/gpt-5.6" },
            });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/overrides",
            new { name = BuiltInAgentCatalog.MohistPlannerName, agentConfig = new { model = "openai/gpt-5.6-mini" } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_override_conflict", payload.GetProperty("code").GetString());
        var list = await _client.GetDataAsync<AgentDto[]>($"/api/projects/{project.Id}/agents?all=true");
        Assert.Single(list, agent => agent.Origin == "project");
        var unchanged = await _client.GetDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{existing.Id}");
        Assert.Equal("openai/gpt-5.6", unchanged.AgentConfig?.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Override_ArchivedSameNameAgentNamesItsRepairWithoutFallingBack()
    {
        var project = await CreateProjectAsync("agent-override-archived");
        var existing = await _client.PostDataAsync<AgentDto>(
            $"/api/projects/{project.Id}/agents",
            new
            {
                name = BuiltInAgentCatalog.MohistReviewerName,
                instructions = "review already here",
            });
        await _client.DeleteAsync($"/api/projects/{project.Id}/agents/{existing.Id}");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/overrides",
            new { name = BuiltInAgentCatalog.MohistReviewerName, agentConfig = new { model = "openai/gpt-5.6" } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent_override_archived", payload.GetProperty("code").GetString());
        Assert.Equal(existing.Id, payload.GetProperty("details").GetProperty("agentId").GetString());
        var stored = await _client.GetDataAsync<AgentDto>($"/api/projects/{project.Id}/agents/{existing.Id}");
        Assert.Equal("archived", stored.Status);
    }

    [Fact]
    public async Task Override_UnknownNameIsNotFound()
    {
        var project = await CreateProjectAsync("agent-override-unknown");

        using var notBuiltIn = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/overrides",
            new { name = "not-a-built-in", agentConfig = new { model = "openai/gpt-5.6" } });
        using var slackManager = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/overrides",
            new { name = BuiltInAgentCatalog.MohistSlackName, agentConfig = new { model = "openai/gpt-5.6" } });
        using var noName = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/overrides",
            new { agentConfig = new { model = "openai/gpt-5.6" } });

        Assert.Equal(HttpStatusCode.NotFound, notBuiltIn.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, slackManager.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
    }

    [Fact]
    public async Task Override_InvalidCallerChangesAreRejectedWithoutCreatingAnything()
    {
        var project = await CreateProjectAsync("agent-override-invalid");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/agents/overrides",
            new { name = BuiltInAgentCatalog.MohistBuilderName, agentConfig = new { runtime = "fast" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var list = await _client.GetDataAsync<AgentDto[]>($"/api/projects/{project.Id}/agents?all=true");
        Assert.DoesNotContain(list, agent => agent.Origin == "project");
        Assert.Equal(3, list.Count(agent => agent.Origin == "built-in"));
    }

    private async Task<ProjectDto> CreateProjectAsync(string prefix)
    {
        var projectId = $"project-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            $"{prefix}-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = $"file://{Guid.NewGuid():N}",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        return new ProjectDto(projectId);
    }

    private static object NewAgent(string name) => new
    {
        name,
        description = "agent description",
        purpose = "Review changes",
        instructions = $"instructions for {name}",
        agentConfig = new { model = "openai/gpt-5.6" },
        skills = new[] { "coding" },
        permissions = new[] { "repo:read", "issue:write" },
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
        string? Purpose,
        string Instructions,
        JsonElement? AgentConfig,
        string[] Skills,
        string[] Permissions,
        int? MaxConcurrentRuns,
        string Status,
        string CreatedAt,
        string UpdatedAt,
        string? Origin,
        bool OverridesBuiltIn,
        JsonElement? EffectiveExecutionConfig);
}
