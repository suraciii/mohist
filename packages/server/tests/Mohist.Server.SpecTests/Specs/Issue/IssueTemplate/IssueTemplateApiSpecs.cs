using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.IssueTemplate;

[Collection("IntegrationIssue3")]
public class IssueTemplateApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public IssueTemplateApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Get_NonexistentTemplate_ReturnsNotFound()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"it-404-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync($"/api/issue-templates/nonexistent?projectId={project.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task List_ExcludesBuiltinsWhenDisabled()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"it-disabled-{Guid.NewGuid():N}");

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var profile = await db.ProjectWorkflowProfiles.FirstOrDefaultAsync(x => x.ProjectId == project.Id);
        if (profile is null)
        {
            profile = new ProjectWorkflowProfile { ProjectId = project.Id };
            db.ProjectWorkflowProfiles.Add(profile);
        }
        profile.DisableDefaultIssueTemplate = true;
        await db.SaveChangesAsync();

        var list = await _client.GetDataAsync<List<JsonElement>>($"/api/issue-templates?projectId={project.Id}");

        Assert.DoesNotContain(list, t => t.GetProperty("id").GetString() == "feature");
        Assert.DoesNotContain(list, t => t.GetProperty("id").GetString() == "bug");
        Assert.DoesNotContain(list, t => t.GetProperty("id").GetString() == "refactor");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DisabledBuiltIn_CanBeShadowedByProjectCustomTemplate()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"it-shadow-{Guid.NewGuid():N}");

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
        {
            ProjectId = project.Id,
            DisableDefaultIssueTemplate = true,
        });
        db.ProjectIssueTemplates.Add(new ProjectIssueTemplateRow
        {
            ProjectId = project.Id,
            Name = "feature",
            Template = JsonSerializer.Serialize(new
            {
                Id = "feature",
                Name = "Custom Feature",
                About = "Project feature template",
                Sections = new[]
                {
                    new { Title = "S", Guidance = "g", Placeholder = "p" },
                },
            }),
        });
        await db.SaveChangesAsync();

        var list = await _client.GetDataAsync<List<JsonElement>>($"/api/issue-templates?projectId={project.Id}");
        var listed = Assert.Single(list, t => t.GetProperty("id").GetString() == "feature");
        Assert.Equal("custom", listed.GetProperty("source").GetString());

        var response = await _client.GetAsync($"/api/issue-templates/feature?projectId={project.Id}");
        response.EnsureSuccessStatusCode();
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("Custom Feature", data.GetProperty("name").GetString());
        Assert.Equal("Project feature template", data.GetProperty("description").GetString());
        Assert.Equal("custom", data.GetProperty("source").GetString());

        var aliasResponse = await _client.GetAsync($"/api/issue-templates/mohist/default?projectId={project.Id}");
        aliasResponse.EnsureSuccessStatusCode();
        var aliasData = (await aliasResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(data.GetRawText(), aliasData.GetRawText());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DisabledDefault_DoesNotAffectOtherProjects()
    {
        var projectA = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"it-a-{Guid.NewGuid():N}");
        var projectB = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"it-b-{Guid.NewGuid():N}");

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var profileA = new ProjectWorkflowProfile { ProjectId = projectA.Id, DisableDefaultIssueTemplate = true };
        db.ProjectWorkflowProfiles.Add(profileA);
        await db.SaveChangesAsync();

        var listA = await _client.GetDataAsync<List<JsonElement>>($"/api/issue-templates?projectId={projectA.Id}");
        var listB = await _client.GetDataAsync<List<JsonElement>>($"/api/issue-templates?projectId={projectB.Id}");

        Assert.DoesNotContain(listA, t => t.GetProperty("id").GetString() == "feature");
        Assert.Contains(listB, t => t.GetProperty("id").GetString() == "feature");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task List_WithoutProjectId_ReturnsBadRequest()
    {
        using var response = await _client.GetAsync("/api/issue-templates");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record ProjectDto(string Id, string Name);
}
