using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

/// <summary>
/// API-boundary validation tests for the converged issue-level
/// <c>agentConfig</c> surface (#410 T-002 design D5). The route layer
/// invokes <see cref="Mohist.Server.Issue.Services.IssueModelMetadata.ValidateAgentConfig"/>
/// on the raw request body so the open-shape <c>agentConfig</c> field
/// cannot persist ACP/liveness keys.
/// </summary>
[Collection("MohistIntegration")]
public class IssueAgentConfigValidationApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public IssueAgentConfigValidationApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"{prefix}-{Guid.NewGuid():N}");
        return project.Id;
    }

    [Theory]
    [InlineData("type")]
    public async Task CreateIssue_WithForbiddenAgentConfigKey_Returns400(string forbiddenKey)
    {
        var projectId = await CreateProjectAsync("issue-create-forbidden");

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new
            {
                title = "Issue with forbidden agentConfig key",
                projectId = projectId,
                isDraft = true,
                agentConfig = new Dictionary<string, object?>
                {
                    [forbiddenKey] = "value",
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_agent_config", body.GetProperty("code").GetString());
        var error = body.GetProperty("error").GetString() ?? string.Empty;
        Assert.Contains($"agentConfig.{forbiddenKey}", error);
        Assert.Contains("model, variant", error);
    }

    [Fact]
    public async Task CreateIssue_WithModelAndVariantOnly_IsAccepted()
    {
        var projectId = await CreateProjectAsync("issue-create-converged");

        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{projectId}/issues",
            new
            {
                title = "Issue with converged agent config",
                projectId = projectId,
                isDraft = true,
                model = "openai/gpt-5.6",
                modelVariant = "xhigh",
            });

        Assert.NotNull(issue);
        Assert.True(issue.Number > 0);
    }

    [Theory]
    [InlineData("type")]
    public async Task PatchIssue_WithForbiddenAgentConfigKey_Returns400(string forbiddenKey)
    {
        var projectId = await CreateProjectAsync("issue-patch-forbidden");
        var issue = await _client.PostDataAsync<IssueDto>(
            $"/api/projects/{projectId}/issues",
            new { title = "Patch target issue", projectId = projectId, isDraft = true });

        using var response = await _client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/issues/{issue.Number}",
            new
            {
                agentConfig = new Dictionary<string, object?>
                {
                    [forbiddenKey] = "value",
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_agent_config", body.GetProperty("code").GetString());
        var error = body.GetProperty("error").GetString() ?? string.Empty;
        Assert.Contains($"agentConfig.{forbiddenKey}", error);
    }

    private sealed record ProjectDto(string Id);
    private sealed record IssueDto(int Number, string? Id, string ProjectId);
}
