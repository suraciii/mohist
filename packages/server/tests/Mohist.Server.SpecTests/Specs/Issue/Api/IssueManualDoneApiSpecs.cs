using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IssueLifecycle")]
public class IssueManualDoneApiSpecs
{
    private readonly HttpClient _client;

    public IssueManualDoneApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    [Fact]
    public async Task Done_OnUnknownProject_Returns404()
    {
        using var response = await _client.PostAsync(
            $"/api/projects/proj-does-not-exist-{Guid.NewGuid():N}/issues/1/done",
            null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Done_OnUnknownIssue_Returns404()
    {
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"manual-done-unknown-{Guid.NewGuid():N}");

        using var response = await _client.PostAsync(
            $"/api/projects/{project.Id}/issues/999999/done",
            null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record ProjectDto(string Id);
}