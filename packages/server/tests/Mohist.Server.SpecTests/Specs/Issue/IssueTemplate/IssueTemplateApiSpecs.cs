using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.IssueTemplate;

/// <summary>
/// Route-level contract specs for <c>/api/issue-templates</c>: list builtins,
/// get full template body, the <c>mohist/default</c> alias, 404 unknown id,
/// and 400 missing projectId. The disable/shadow/cross-project-isolation
/// calculation matrix lives in <c>IssueTemplateRegistrySpecs</c>.
/// </summary>
public class IssueTemplateApiSpecs
{
    private readonly HttpClient _client;

    public IssueTemplateApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task List_IncludesBuiltinTemplates()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"it-list-{Guid.NewGuid():N}");

        var list = await _client.GetDataAsync<List<JsonElement>>($"/api/issue-templates?projectId={project.Id}");

        Assert.NotEmpty(list);
        var feature = Assert.Single(list, t => t.GetProperty("id").GetString() == "feature");
        Assert.False(string.IsNullOrEmpty(feature.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrEmpty(feature.GetProperty("description").GetString()));
        Assert.Equal("builtin", feature.GetProperty("source").GetString());

        // The removed fields should not appear
        Assert.False(feature.TryGetProperty("about", out _));
        Assert.False(feature.TryGetProperty("suitableFor", out _));
        Assert.False(feature.TryGetProperty("isDefault", out _));
    }

    [Fact]
    public async Task Get_Feature_ReturnsFullTemplateWithBody()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"it-get-{Guid.NewGuid():N}");

        var response = await _client.GetAsync($"/api/issue-templates/feature?projectId={project.Id}");
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();

        var data = envelope.GetProperty("data");
        Assert.Equal("feature", data.GetProperty("id").GetString());
        Assert.Equal("Feature", data.GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("description").GetString()));

        var body = data.GetProperty("body").GetString();
        Assert.False(string.IsNullOrEmpty(body));
        Assert.Contains("## User Voice", body);
        Assert.Contains("## Non-Goals", body);

        // The removed fields should not appear
        Assert.False(data.TryGetProperty("about", out _));
        Assert.False(data.TryGetProperty("suitableFor", out _));
        Assert.False(data.TryGetProperty("isDefault", out _));
        Assert.False(data.TryGetProperty("defaults", out _));
        Assert.False(data.TryGetProperty("sections", out _));
    }

    [Fact]
    public async Task Get_AliasMohistDefault_ReturnsFeature()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"it-alias-{Guid.NewGuid():N}");

        var responseCanonical = await _client.GetAsync($"/api/issue-templates/feature?projectId={project.Id}");
        responseCanonical.EnsureSuccessStatusCode();
        var canonical = (await responseCanonical.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        var responseAlias = await _client.GetAsync($"/api/issue-templates/mohist/default?projectId={project.Id}");
        responseAlias.EnsureSuccessStatusCode();
        var alias = (await responseAlias.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

        Assert.Equal(canonical.GetRawText(), alias.GetRawText());
    }

    [Fact]
    public async Task Get_NonexistentTemplate_ReturnsNotFound()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"it-404-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync($"/api/issue-templates/nonexistent?projectId={project.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_WithoutProjectId_ReturnsBadRequest()
    {
        using var response = await _client.GetAsync("/api/issue-templates");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record ProjectDto(string Id, string Name);
}
