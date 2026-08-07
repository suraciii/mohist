using System.Net;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

/// <summary>
/// Route-level contract specs for
/// <c>GET /api/projects/&#123;projectRef&#125;/agent/usage</c>: 404 unknown
/// project, 400 unknown range, and accepted range values returning 200. The
/// timeseries structure (bucket count, ordering, day/week granularity) is
/// the <c>AgentUsageReporter</c> calculation matrix and lives in
/// <c>AgentUsageTimeseriesQuerierSpecs</c>.
/// </summary>
[Collection("IntegrationApi")]
public class AgentUsageTimeseriesApiSpecs
{
    private readonly HttpClient _client;

    public AgentUsageTimeseriesApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var name = $"usage-{Guid.NewGuid():N}";
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", name);
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
        });
        return project;
    }

    [Fact]
    public async Task GetUsage_UnknownProjectReturnsNotFound()
    {
        using var response = await _client.GetAsync($"/api/projects/unknown-project-{Guid.NewGuid():N}/agent/usage");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetUsage_UnknownRange_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync();
        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/agent/usage?range=bad");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("7d")]
    [InlineData("30d")]
    [InlineData("90d")]
    public async Task GetUsage_AcceptedRangeValues_AllReturnOk(string range)
    {
        var project = await CreateProjectAsync();
        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/agent/usage?range={range}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record ProjectDto(string Id, string Name);
}
