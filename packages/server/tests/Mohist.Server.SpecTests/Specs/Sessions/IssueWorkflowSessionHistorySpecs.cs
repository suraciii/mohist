using System.Net;
using System.Net.Http.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Route contract for the historical workflow-session lookup at
/// <c>GET /api/projects/{projectRef}/issues/{number}/sessions/{name}</c>
/// and its transcript projection. The lookup rules (terminal-issue
/// fallback, active-run precedence, newest-by-CreatedAt selection,
/// runtime-id transcript filter) live in
/// <c>IssueWorkflowSessionHistoryQuerierSpecs</c>.
/// </summary>
[Collection("IntegrationSessions")]
public class IssueWorkflowSessionHistorySpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public IssueWorkflowSessionHistorySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReadRoutes_ReturnNotFoundWhenNoMatchingSessionExists()
    {
        var projectId = await CreateProjectAsync($"history-missing-{Guid.NewGuid():N}");
        const int issueNumber = 465;

        using var metadata = await _fixture.Client.GetAsync(SessionPath(projectId, issueNumber, "missing"));
        using var transcript = await _fixture.Client.GetAsync($"{SessionPath(projectId, issueNumber, "missing")}/transcript");

        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, transcript.StatusCode);
    }

    [Fact]
    public async Task ReadRoutes_RequireIssueProjectAndWorkflowSourceBoundaries()
    {
        var projectId = await CreateProjectAsync($"history-boundaries-{Guid.NewGuid():N}");
        var otherProjectId = await CreateProjectAsync($"history-other-project-{Guid.NewGuid():N}");
        const int issueNumber = 462;

        using var response = await _fixture.Client.GetAsync(SessionPath(projectId, issueNumber, "plan"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReadRoutes_CommandsReturn404ForHistoricalSession()
    {
        var projectId = await CreateProjectAsync($"history-commands-{Guid.NewGuid():N}");
        const int issueNumber = 464;
        const string sessionName = "plan";

        var basePath = $"/api/projects/{projectId}/issues/{issueNumber}/sessions/{sessionName}";
        using var compact = await _fixture.Client.PostAsync($"{basePath}/compact", content: null);
        using var reset = await _fixture.Client.PostAsync($"{basePath}/reset", content: null);
        using var cancel = await _fixture.Client.PostAsync($"{basePath}/cancel", content: null);

        Assert.Equal(HttpStatusCode.NotFound, compact.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    private async Task<string> CreateProjectAsync(string name)
    {
        using var response = await _fixture.Client.PostAsJsonAsync("/api/projects", new
        {
            name,
            repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "main" },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("data").GetProperty("id").GetString()!;
    }

    private static string SessionPath(string projectId, int issueNumber, string sessionName) =>
        $"/api/projects/{projectId}/issues/{issueNumber}/sessions/{sessionName}";
}