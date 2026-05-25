using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class ApiContractSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public ApiContractSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("/api/projects/current")]
    [InlineData("/api/questions")]
    [InlineData("/api/questions/question-1")]
    [InlineData("/api/opencode/models")]
    [InlineData("/api/issues/1/agent-session")]
    [InlineData("/api/agent/session-status")]
    public async Task RemovedLegacyApi_WhenRequested_ReturnsNotFound(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/questions/question-1/reply")]
    [InlineData("/api/questions/question-1/expire")]
    [InlineData("/api/settings/system/rebuild")]
    [InlineData("/api/issues/1/messages")]
    public async Task RemovedLegacyApiPost_WhenRequested_ReturnsNotFound(string path)
    {
        using var response = await _fixture.Client.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task IssueRebaseApi_QueuesWorkflowTask()
    {
        var projectName = $"proj-{Guid.NewGuid():N}";
        var projectResponse = await _fixture.Client.PostAsJsonAsync("/api/projects", new { name = projectName, path = "/tmp/project", baseBranch = "trunk" });
        var projectJson = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = projectJson.GetProperty("data").GetProperty("id").GetString();
        var issueResponse = await _fixture.Client.PostAsJsonAsync("/api/issues", new { title = "Needs rebase", projectId });
        var issueJson = await issueResponse.Content.ReadFromJsonAsync<JsonElement>();
        var number = issueJson.GetProperty("data").GetProperty("number").GetInt32();

        await _fixture.Client.PostAsJsonAsync($"/api/issues/{number}/start?projectId={projectId}", new { });
        using var response = await _fixture.Client.PostAsJsonAsync($"/api/issues/{number}/rebase?projectId={projectId}", new { baseBranch = "trunk" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = payload.GetProperty("data");
        Assert.Equal("queued", data.GetProperty("status").GetString());
        Assert.Equal("trunk", data.GetProperty("baseBranch").GetString());
        Assert.StartsWith("rebase-", data.GetProperty("taskId").GetString());
    }
}
