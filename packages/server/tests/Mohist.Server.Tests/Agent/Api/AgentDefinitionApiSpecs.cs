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
        string UpdatedAt);
}
