using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

public class IssueLabelsApiSpecs
{
    private readonly HttpClient _client;

    public IssueLabelsApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

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

    [Fact]
    public async Task ListLabels_Returns200JsonArray()
    {
        var project = await CreateProjectAsync("list-labels-shape");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/labels");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
    }

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
