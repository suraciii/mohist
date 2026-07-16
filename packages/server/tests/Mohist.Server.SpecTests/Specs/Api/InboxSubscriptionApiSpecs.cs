using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class InboxSubscriptionApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public InboxSubscriptionApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task Get_NoStoredPreferences_ReturnsAllFourEnabled()
    {
        var projectId = await CreateProjectAsync("sub-default");

        var sub = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/inbox/subscription");

        Assert.True(sub.GetProperty("workflow_failed").GetBoolean());
        Assert.True(sub.GetProperty("approval_requested").GetBoolean());
        Assert.True(sub.GetProperty("issue_started").GetBoolean());
        Assert.True(sub.GetProperty("issue_completed").GetBoolean());
    }

    [Fact]
    public async Task Put_PersistsAndReRead_ReturnsUpdatedState()
    {
        var projectId = await CreateProjectAsync("sub-update");

        var body = new Dictionary<string, bool>
        {
            ["workflow_failed"] = true,
            ["approval_requested"] = false,
            ["issue_started"] = true,
            ["issue_completed"] = false,
        };

        var putResult = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/inbox/subscription", body);
        putResult.EnsureSuccessStatusCode();

        var sub = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/inbox/subscription");

        Assert.True(sub.GetProperty("workflow_failed").GetBoolean());
        Assert.False(sub.GetProperty("approval_requested").GetBoolean());
        Assert.True(sub.GetProperty("issue_started").GetBoolean());
        Assert.False(sub.GetProperty("issue_completed").GetBoolean());
    }

    [Fact]
    public async Task Put_UnknownKey_ReturnsBadRequest()
    {
        var projectId = await CreateProjectAsync("sub-unknown-key");

        var body = new Dictionary<string, bool>
        {
            ["workflow_failed"] = true,
            ["approval_requested"] = true,
            ["issue_started"] = true,
            ["issue_completed"] = true,
            ["bogus_kind"] = false,
        };

        using var response = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/inbox/subscription", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var sub = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/inbox/subscription");

        Assert.True(sub.GetProperty("workflow_failed").GetBoolean());
        Assert.True(sub.GetProperty("approval_requested").GetBoolean());
        Assert.True(sub.GetProperty("issue_started").GetBoolean());
        Assert.True(sub.GetProperty("issue_completed").GetBoolean());
    }

    [Fact]
    public async Task Put_MissingKey_ReturnsBadRequest()
    {
        var projectId = await CreateProjectAsync("sub-missing-key");

        var body = new Dictionary<string, bool>
        {
            ["workflow_failed"] = true,
            ["approval_requested"] = true,
            ["issue_started"] = true,
        };

        using var response = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/inbox/subscription", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var sub = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/inbox/subscription");

        Assert.True(sub.GetProperty("workflow_failed").GetBoolean());
        Assert.True(sub.GetProperty("approval_requested").GetBoolean());
        Assert.True(sub.GetProperty("issue_started").GetBoolean());
        Assert.True(sub.GetProperty("issue_completed").GetBoolean());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("42")]
    public async Task Put_NonObjectBody_ReturnsBadRequestAndPersistsNothing(string json)
    {
        var projectId = await CreateProjectAsync("sub-non-object");

        using var response = await PutJsonAsync(
            $"/api/projects/{projectId}/inbox/subscription",
            json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertDefaultSubscriptionAsync(projectId);
    }

    [Fact]
    public async Task Put_NonBooleanProperty_ReturnsBadRequestAndPersistsNothing()
    {
        var projectId = await CreateProjectAsync("sub-non-bool");
        var json = """
            {
              "workflow_failed": "yes",
              "approval_requested": true,
              "issue_started": true,
              "issue_completed": true
            }
            """;

        using var response = await PutJsonAsync(
            $"/api/projects/{projectId}/inbox/subscription",
            json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertDefaultSubscriptionAsync(projectId);
    }

    [Fact]
    public async Task Subscription_ProjectIsolation_ScopedByProject()
    {
        var projectA = await CreateProjectAsync("sub-iso-a");
        var projectB = await CreateProjectAsync("sub-iso-b");

        var body = new Dictionary<string, bool>
        {
            ["workflow_failed"] = true,
            ["approval_requested"] = false,
            ["issue_started"] = false,
            ["issue_completed"] = true,
        };

        var putResult = await _client.PutAsJsonAsync(
            $"/api/projects/{projectA}/inbox/subscription", body);
        putResult.EnsureSuccessStatusCode();

        var subA = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectA}/inbox/subscription");
        Assert.False(subA.GetProperty("approval_requested").GetBoolean());
        Assert.True(subA.GetProperty("issue_completed").GetBoolean());

        var subB = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectB}/inbox/subscription");
        Assert.True(subB.GetProperty("workflow_failed").GetBoolean());
        Assert.True(subB.GetProperty("approval_requested").GetBoolean());
        Assert.True(subB.GetProperty("issue_started").GetBoolean());
        Assert.True(subB.GetProperty("issue_completed").GetBoolean());
    }

    [Fact]
    public async Task Subscription_UnknownProject_Returns404()
    {
        using var response = await _client.GetAsync(
            "/api/projects/proj_does_not_exist/inbox/subscription");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<JsonElement>(
            "/api/projects",
            $"{prefix}-{Guid.NewGuid():N}");
        return project.GetProperty("id").GetString()!;
    }

    private async Task AssertDefaultSubscriptionAsync(string projectId)
    {
        var sub = await _client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/inbox/subscription");

        Assert.True(sub.GetProperty("workflow_failed").GetBoolean());
        Assert.True(sub.GetProperty("approval_requested").GetBoolean());
        Assert.True(sub.GetProperty("issue_started").GetBoolean());
        Assert.True(sub.GetProperty("issue_completed").GetBoolean());
    }

    private async Task<HttpResponseMessage> PutJsonAsync(string url, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _client.PutAsync(url, content);
    }
}
