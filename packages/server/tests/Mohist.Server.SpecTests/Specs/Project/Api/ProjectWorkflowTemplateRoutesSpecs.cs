using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Project.Api;

[Collection("IntegrationRunner")]
public class ProjectWorkflowTemplateRoutesSpecs
{
    private readonly HttpClient _client;

    public ProjectWorkflowTemplateRoutesSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task WorkflowTemplateCrudSupportsSlashTemplateIds()
    {
        var project = await CreateProjectAsync();
        const string templateId = "team/browser-acceptance";
        var encodedTemplateId = Uri.EscapeDataString(templateId);
        const string yaml = """
            id: team/browser-acceptance
            stages:
              - stage: check
                tasks: []
                checks: []
            """;

        using var create = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/workflow-templates", new { yaml });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var encodedDetail = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project.Id}/workflow-templates/{encodedTemplateId}");
        Assert.Equal(templateId, encodedDetail.GetProperty("data").GetProperty("templateId").GetString());

        var pathDetail = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project.Id}/workflow-templates/{templateId}");
        Assert.Equal(templateId, pathDetail.GetProperty("data").GetProperty("definition").GetProperty("id").GetString());

        using var update = await _client.PutAsJsonAsync(
            $"/api/projects/{project.Id}/workflow-templates/{encodedTemplateId}", new { yaml });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using var delete = await _client.DeleteAsync(
            $"/api/projects/{project.Id}/workflow-templates/{encodedTemplateId}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        return await _client.PostDataAsync<ProjectDto>(
            "/api/projects",
            new
            {
                name = $"workflow-template-routes-{Guid.NewGuid():N}",
                repository = new
                {
                    name = "templates",
                    gitUrl = "git@example.com:templates.git",
                    baseBranch = "main",
                },
            });
    }

    private sealed record ProjectDto(string Id);
}
