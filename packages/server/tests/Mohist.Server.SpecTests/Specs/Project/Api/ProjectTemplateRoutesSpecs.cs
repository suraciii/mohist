using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Project.Api;

[Collection("IntegrationRunner")]
public class ProjectTemplateRoutesSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public ProjectTemplateRoutesSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ListEffectiveProjectTemplates_MergesSystemAndOverrideWithSourceLabels()
    {
        var project = await CreateProjectAsync();

        await UpsertOverrideAsync(project.Id, "proposal", new
        {
            displayName = "Project Proposal",
            description = "Override description",
            tags = new[] { "project-override" },
            stage = "plan",
            body = "# Project proposal body",
        });

        var entries = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project.Id}/templates");

        Assert.Equal(JsonValueKind.Array, entries.GetProperty("data").ValueKind);

        var rows = entries.GetProperty("data").EnumerateArray()
            .Select(item => new
            {
                Key = item.GetProperty("key").GetString(),
                Source = item.GetProperty("source").GetString(),
                Body = item.GetProperty("body").GetString(),
            })
            .ToDictionary(r => r.Key!, StringComparer.Ordinal);

        Assert.Equal("project-override", rows["proposal"].Source);
        Assert.Equal("# Project proposal body", rows["proposal"].Body);
        Assert.Equal("system", rows["build"].Source);
        Assert.False((rows["build"].Body ?? "").StartsWith("---\n"), "Body should not start with YAML frontmatter");
        Assert.StartsWith("Read the current Mohist issue details", rows["build"].Body);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetSingleEffectiveTemplate_PrefersProjectOverride()
    {
        var project = await CreateProjectAsync();

        await UpsertOverrideAsync(project.Id, "build", new
        {
            displayName = "Custom Build",
            description = "Project-level build template",
            tags = Array.Empty<string>(),
            stage = (string?)null,
            body = "# Overridden build body",
        });

        var entry = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project.Id}/templates/build");

        Assert.True(entry.GetProperty("success").GetBoolean());
        Assert.Equal("project-override", entry.GetProperty("data").GetProperty("source").GetString());
        Assert.Equal("# Overridden build body", entry.GetProperty("data").GetProperty("body").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetSingleEffectiveTemplate_FallsBackToSystemWhenNoOverrideExists()
    {
        var project = await CreateProjectAsync();

        var entry = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project.Id}/templates/build");

        Assert.True(entry.GetProperty("success").GetBoolean());
        Assert.Equal("system", entry.GetProperty("data").GetProperty("source").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetOverride_ReturnsNotFoundWhenNoRowExists()
    {
        var project = await CreateProjectAsync();

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/templates/proposal/override");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal("not_found", payload.GetProperty("code").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task GetOverride_ReturnsRowWhenOverrideExists()
    {
        var project = await CreateProjectAsync();

        await UpsertOverrideAsync(project.Id, "proposal", new
        {
            displayName = "Project Proposal",
            description = "Override description",
            tags = new[] { "x" },
            stage = "plan",
            body = "# Body B",
        });

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/templates/proposal/override");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("proposal", payload.GetProperty("data").GetProperty("key").GetString());
        Assert.Equal("# Body B", payload.GetProperty("data").GetProperty("body").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PutOverride_CreatesRow()
    {
        var project = await CreateProjectAsync();

        using var response = await _client.PutAsJsonAsync(
            $"/api/projects/{project.Id}/templates/proposal/override",
            new
            {
                displayName = "New Proposal",
                description = "Created via spec",
                tags = new[] { "plan" },
                stage = "plan",
                body = "# Created",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("proposal", payload.GetProperty("data").GetProperty("key").GetString());
        Assert.Equal("# Created", payload.GetProperty("data").GetProperty("body").GetString());

    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PutOverride_UpdatesExistingRow()
    {
        var project = await CreateProjectAsync();

        await UpsertOverrideAsync(project.Id, "proposal", new
        {
            displayName = "Initial",
            description = "Initial description",
            tags = Array.Empty<string>(),
            stage = "plan",
            body = "# Initial body",
        });

        using var response = await _client.PutAsJsonAsync(
            $"/api/projects/{project.Id}/templates/proposal/override",
            new
            {
                displayName = "Updated",
                description = "Updated description",
                tags = new[] { "plan", "review" },
                stage = "plan",
                body = "# Updated body",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var overrideRow = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project.Id}/templates/proposal/override");
        Assert.Equal("# Updated body", overrideRow.GetProperty("data").GetProperty("body").GetString());
        Assert.Equal("Updated", overrideRow.GetProperty("data").GetProperty("displayName").GetString());

    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PutOverride_WithEmptyBody_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync();

        using var response = await _client.PutAsJsonAsync(
            $"/api/projects/{project.Id}/templates/proposal/override",
            new
            {
                displayName = "Bad",
                description = string.Empty,
                tags = Array.Empty<string>(),
                stage = (string?)null,
                body = string.Empty,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("bad_request", payload.GetProperty("code").GetString());

        using var followUp = await _client.GetAsync(
            $"/api/projects/{project.Id}/templates/proposal/override");
        Assert.Equal(HttpStatusCode.NotFound, followUp.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task PutOverride_WithMissingBody_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync();

        using var response = await _client.PutAsJsonAsync(
            $"/api/projects/{project.Id}/templates/proposal/override",
            new
            {
                displayName = "No body",
                description = string.Empty,
                tags = Array.Empty<string>(),
                stage = (string?)null,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task DeleteOverride_RemovesRow()
    {
        var project = await CreateProjectAsync();

        await UpsertOverrideAsync(project.Id, "proposal", new
        {
            displayName = "To delete",
            description = string.Empty,
            tags = Array.Empty<string>(),
            stage = (string?)null,
            body = "# Will be removed",
        });

        using var response = await _client.DeleteAsync(
            $"/api/projects/{project.Id}/templates/proposal/override");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var followUp = await _client.GetAsync(
            $"/api/projects/{project.Id}/templates/proposal/override");
        Assert.Equal(HttpStatusCode.NotFound, followUp.StatusCode);

    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task DeleteOverride_IsIdempotentWhenRowDoesNotExist()
    {
        var project = await CreateProjectAsync();

        using var response = await _client.DeleteAsync(
            $"/api/projects/{project.Id}/templates/proposal/override");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task Preview_RendersOverrideBodyWithProvidedVariables()
    {
        var project = await CreateProjectAsync();

        await UpsertOverrideAsync(project.Id, "proposal", new
        {
            displayName = "Preview proposal",
            description = "Preview test",
            tags = Array.Empty<string>(),
            stage = "plan",
            body = "Hello ${{ issue.number }} from ${{ project.name }}",
        });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/templates/proposal/preview",
            new
            {
                variables = new
                {
                    issue = new { number = 42 },
                    project = new { name = "Mohist" },
                },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("success").GetBoolean());

        var data = payload.GetProperty("data");
        Assert.Equal("Hello 42 from Mohist", data.GetProperty("rendered").GetString());

        var missing = data.GetProperty("missingVariables")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Empty(missing);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var name = $"template-routes-{Guid.NewGuid():N}";
        return await _client.PostDataAsync<ProjectDto>(
            "/api/projects",
            new { name, path = "/mohist-tests/projects/template-routes", baseBranch = "main" });
    }

    private async Task UpsertOverrideAsync(string projectId, string key, object body)
    {
        using var response = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/templates/{key}/override",
            body);
        response.EnsureSuccessStatusCode();
    }

    private sealed record ProjectDto(string Id);
}
