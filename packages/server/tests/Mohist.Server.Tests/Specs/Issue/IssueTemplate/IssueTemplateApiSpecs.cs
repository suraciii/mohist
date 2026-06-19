using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Issue.IssueTemplate;

[Collection("MohistIntegration")]
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
    public async Task List_IncludesDefaultTemplate()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"it-list-{Guid.NewGuid():N}" });

        var list = await _client.GetDataAsync<List<JsonElement>>($"/api/issue-templates?projectId={project.Id}");

        Assert.NotEmpty(list);
        var defaultTemplate = list.Single(t => t.GetProperty("id").GetString() == "mohist/default");
        Assert.False(string.IsNullOrEmpty(defaultTemplate.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrEmpty(defaultTemplate.GetProperty("about").GetString()));
        Assert.True(defaultTemplate.GetProperty("suitableFor").GetArrayLength() > 0);
        Assert.True(defaultTemplate.GetProperty("isDefault").GetBoolean());
        Assert.Equal("builtin", defaultTemplate.GetProperty("source").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Get_ReturnsFullTemplateWithSections()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"it-get-{Guid.NewGuid():N}" });

        var response = await _client.GetAsync($"/api/issue-templates/mohist/default?projectId={project.Id}");
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();

        var data = envelope.GetProperty("data");
        Assert.Equal("mohist/default", data.GetProperty("id").GetString());
        Assert.Equal("Mohist Default", data.GetProperty("name").GetString());

        var sections = data.GetProperty("sections");
        Assert.Equal(5, sections.GetArrayLength());

        var expectedTitles = new[] { "User Voice", "Product Shape", "Domain Model", "Acceptance Criteria", "Non-Goals" };
        for (var i = 0; i < expectedTitles.Length; i++)
        {
            var section = sections[i];
            Assert.Equal(expectedTitles[i], section.GetProperty("title").GetString());
            Assert.False(string.IsNullOrEmpty(section.GetProperty("guidance").GetString()));
            Assert.False(string.IsNullOrEmpty(section.GetProperty("placeholder").GetString()));
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task Get_NonexistentTemplate_ReturnsNotFound()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"it-404-{Guid.NewGuid():N}" });

        using var response = await _client.GetAsync($"/api/issue-templates/nonexistent?projectId={project.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task List_ExcludesDefaultWhenDisabled()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"it-disabled-{Guid.NewGuid():N}" });

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

        Assert.DoesNotContain(list, t => t.GetProperty("id").GetString() == "mohist/default");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DisabledDefault_DoesNotAffectOtherProjects()
    {
        var projectA = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"it-a-{Guid.NewGuid():N}" });
        var projectB = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"it-b-{Guid.NewGuid():N}" });

        var dbFactory = _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var profileA = new ProjectWorkflowProfile { ProjectId = projectA.Id, DisableDefaultIssueTemplate = true };
        db.ProjectWorkflowProfiles.Add(profileA);
        await db.SaveChangesAsync();

        var listA = await _client.GetDataAsync<List<JsonElement>>($"/api/issue-templates?projectId={projectA.Id}");
        var listB = await _client.GetDataAsync<List<JsonElement>>($"/api/issue-templates?projectId={projectB.Id}");

        Assert.DoesNotContain(listA, t => t.GetProperty("id").GetString() == "mohist/default");
        Assert.Contains(listB, t => t.GetProperty("id").GetString() == "mohist/default");
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
