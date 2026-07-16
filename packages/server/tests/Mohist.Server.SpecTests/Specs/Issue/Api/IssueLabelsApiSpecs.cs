using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue")]
public class IssueLabelsApiSpecs
{
    private readonly HttpClient _client;

    public IssueLabelsApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithKeyValueLabels_PersistsAndReturnsMap()
    {
        var project = await CreateProjectAsync("create-kv");

        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Key value issue",
                projectId = project.Id,
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stream"] = "frontend",
                    ["module"] = "auth",
                },
            });

        Assert.Equal("frontend", issue.Labels["stream"]);
        Assert.Equal("auth", issue.Labels["module"]);

        var detail = await _client.GetDataAsync<IssueDto>($"/api/projects/{project.Id}/issues/{issue.Number}");
        Assert.Equal("frontend", detail.Labels["stream"]);
        Assert.Equal("auth", detail.Labels["module"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithInvalidLabelKey_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync("invalid-key");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Bad key",
                projectId = project.Id,
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Stream"] = "frontend",
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Stream", body, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CreateIssue_WithEmptyLabelValue_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync("empty-value");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Empty value",
                projectId = project.Id,
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stream"] = "",
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("non-empty", body, StringComparison.OrdinalIgnoreCase);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task UpdateIssue_WithKeyValueLabels_FullReplacesMap()
    {
        var project = await CreateProjectAsync("update-replace");

        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Replace labels",
                projectId = project.Id,
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stream"] = "frontend",
                    ["old"] = "stale",
                },
            });

        var updated = await _client.PatchDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            new
            {
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["module"] = "auth",
                },
            });

        Assert.False(updated.Labels.ContainsKey("stream"));
        Assert.False(updated.Labels.ContainsKey("old"));
        Assert.Equal("auth", updated.Labels["module"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task UpdateIssue_WithInvalidLabelKey_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync("update-invalid");

        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new { title = "Has labels", projectId = project.Id });

        using var response = await _client.PatchAsync(
            $"/api/projects/{project.Id}/issues/{issue.Number}",
            JsonContent.Create(new
            {
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bad key"] = "x",
                },
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListLabels_ReturnsDistinctSortedKeysFromProjectIssues()
    {
        var project = await CreateProjectAsync("list-labels");

        await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "A",
                projectId = project.Id,
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stream"] = "frontend",
                    ["module"] = "auth",
                },
            });
        await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "B",
                projectId = project.Id,
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stream"] = "backend",
                    ["priority"] = "p1",
                },
            });
        await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "C",
                projectId = project.Id,
                labels = new Dictionary<string, string>(StringComparer.Ordinal),
            });

        var labels = await _client.GetDataAsync<string[]>($"/api/projects/{project.Id}/labels");

        Assert.Equal(new[] { "module", "priority", "stream" }, labels);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListIssues_FilterByKeyValueLabel_OnlyReturnsMatching()
    {
        var project = await CreateProjectAsync("filter-kv");

        var frontendIssue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Frontend",
                projectId = project.Id,
                labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "frontend" },
            });
        await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Backend",
                projectId = project.Id,
                labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "backend" },
            });

        var listed = await _client.GetDataAsync<IssueDto[]>(
            $"/api/projects/{project.Id}/issues?label=stream=frontend");

        var item = Assert.Single(listed);
        Assert.Equal(frontendIssue.Number, item.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ListIssues_FilterByMultipleKeyValueLabels_OnlyReturnsIssuesMatchingAll()
    {
        var project = await CreateProjectAsync("filter-kv-multi");

        var matching = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Frontend auth",
                projectId = project.Id,
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stream"] = "frontend",
                    ["module"] = "auth",
                },
            });
        await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{project.Id}/issues",
            new
            {
                title = "Frontend docs",
                projectId = project.Id,
                labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["stream"] = "frontend",
                    ["module"] = "docs",
                },
            });

        var listed = await _client.GetDataAsync<IssueDto[]>(
            $"/api/projects/{project.Id}/issues?label=stream=frontend&label=module=auth");

        var item = Assert.Single(listed);
        Assert.Equal(matching.Number, item.Number);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Theory]
    [InlineData("frontend", "key=value")]
    [InlineData("=frontend", "required")]
    [InlineData("Stream=frontend", "must match")]
    [InlineData("stream=", "non-empty")]
    public async Task ListIssues_WithMalformedLabelFilter_ReturnsBadRequest(string label, string expectedMessage)
    {
        var project = await CreateProjectAsync("filter-invalid");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues?label={Uri.EscapeDataString(label)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_label", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedMessage, body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ProjectDto> CreateProjectAsync(string prefix)
    {
        var project = await _client.PostDataAsync<ProjectDto>(
            "/api/projects",
            new
            {
                name = $"labels-{prefix}-{Guid.NewGuid():N}",
                repository = new
                {
                    name = "main",
                    gitUrl = $"file://{Guid.NewGuid():N}",
                    baseBranch = "main",
                },
            });
        return project;
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(
        int Number,
        string Id,
        Dictionary<string, string> Labels);
}
