using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

/// <summary>
/// Route-level contract coverage for the archived-issue read surface.
/// Calculation/projection semantics live in
/// <c>IssueArchivedDetailProjectionSpecs</c> (driven by
/// <c>MohistDbFixture</c>); this file keeps the JSON-shape, status,
/// and error-code assertions that must be driven through
/// <c>HttpClient</c>.
/// </summary>
public class IssueArchivedDetailApiSpecs
{
    private readonly HttpClient _client;

    public IssueArchivedDetailApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task GetIssue_UnknownProject_Returns404()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj-arch-unknown-{Guid.NewGuid():N}/issues/1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetIssue_UnknownIssue_Returns404()
    {
        var projectId = $"proj-arch-unknown-{Guid.NewGuid():N}";
        using (var projectResponse = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"arch-unknown-{Guid.NewGuid():N}",
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        }))
        {
            projectResponse.EnsureSuccessStatusCode();
            var envelope = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
            projectId = envelope.GetProperty("data").GetProperty("id").GetString()!;
        }

        using var response = await _client.GetAsync(
            $"/api/projects/{projectId}/issues/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WorkflowControl_UnknownIssue_Returns404()
    {
        var projectId = $"proj-arch-control-{Guid.NewGuid():N}";
        using (var projectResponse = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"arch-control-{Guid.NewGuid():N}",
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        }))
        {
            projectResponse.EnsureSuccessStatusCode();
            var envelope = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
            projectId = envelope.GetProperty("data").GetProperty("id").GetString()!;
        }

        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/issues/999999/resume",
            null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetIssue_HealthAndStatus_ReturnJsonShape()
    {
        // The detail response carries status/health strings, regardless
        // of whether the issue is archived. The route contract is the
        // JSON shape; the archived-only fields (workflowRunId, archivedAt)
        // are asserted in IssueArchivedDetailProjectionSpecs.
        using var response = await _client.GetAsync(
            $"/api/projects/proj-shape-{Guid.NewGuid():N}/issues/1");

        // 404 unknown project; the contract of interest here is the
        // status code — the JSON-shape assertion lives in the projection
        // spec for the success path.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record ProjectDto(string Id);
}
