using System.Net;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

/// <summary>
/// Route-level contract specs for <c>/api/projects/&#123;projectRef&#125;/inbox</c>:
/// 404 for unknown item (mark-read / archive) and unknown project. The list
/// ordering / archived-exclusion / empty-project behaviour and the
/// mark-read / mark-all-read / archive mutations live in
/// <c>InboxQuerierSpecs</c>.
/// </summary>
[Collection("IntegrationApi")]
public class InboxApiSpecs
{
    private readonly HttpClient _client;

    public InboxApiSpecs(MohistIntegrationFixture fixture)
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
    public async Task MarkRead_UnknownItemId_Returns404()
    {
        var projectId = await CreateProjectAsync("inbox-unknown");

        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/inbox/inb_does_not_exist/read",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Archive_UnknownItemId_Returns404()
    {
        var projectId = await CreateProjectAsync("inbox-arc-unknown");

        using var response = await _client.PostAsync(
            $"/api/projects/{projectId}/inbox/inb_does_not_exist/archive",
            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_UnknownProject_Returns404()
    {
        using var response = await _client.GetAsync(
            "/api/projects/proj_does_not_exist/inbox");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
