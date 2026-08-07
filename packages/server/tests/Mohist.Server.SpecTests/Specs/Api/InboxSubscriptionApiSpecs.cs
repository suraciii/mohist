using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

/// <summary>
/// Route-level contract specs for
/// <c>/api/projects/&#123;projectRef&#125;/inbox/subscription</c>: 400 for
/// unknown key / missing key / non-object body / non-boolean property, and
/// 404 for unknown project. The all-five-enabled default, the put round-trip
/// persistence, and project isolation live in
/// <c>InboxSubscriptionStoreSpecs</c>.
/// </summary>
[Collection("IntegrationApi")]
public class InboxSubscriptionApiSpecs
{
    private readonly HttpClient _client;

    public InboxSubscriptionApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<JsonElement>(
            "/api/projects",
            $"{prefix}-{Guid.NewGuid():N}");
        return project.GetProperty("id").GetString()!;
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
            ["agent_response_failed"] = true,
            ["bogus_kind"] = false,
        };

        using var response = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/inbox/subscription", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
            ["issue_completed"] = true,
        };

        using var response = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/inbox/subscription", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("42")]
    public async Task Put_NonObjectBody_ReturnsBadRequest(string json)
    {
        var projectId = await CreateProjectAsync("sub-non-object");

        using var response = await PutJsonAsync(
            $"/api/projects/{projectId}/inbox/subscription",
            json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_NonBooleanProperty_ReturnsBadRequest()
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
    }

    [Fact]
    public async Task Subscription_UnknownProject_Returns404()
    {
        using var response = await _client.GetAsync(
            "/api/projects/proj_does_not_exist/inbox/subscription");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PutJsonAsync(string url, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _client.PutAsync(url, content);
    }
}
