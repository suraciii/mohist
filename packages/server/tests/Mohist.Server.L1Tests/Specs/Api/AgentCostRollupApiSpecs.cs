using Mohist.Server.L1Tests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Api;

/// <summary>
/// Route contract for <c>GET /api/projects/{projectRef}/agent/cost</c>:
/// route resolution (404 unknown project), parameter validation (400
/// unknown range), and accepted ranges (200). The cost-rollup
/// calculation matrix (all-time total/today, done-issue count,
/// cost-per-ship, windowed current/previous) is owned by
/// <c>AgentCostRollupQuerierSpecs</c> and exercised without an HTTP
/// round-trip; see <see cref="Mohist.Server.L1Tests.Specs.AgentOps.AgentCostRollupQuerierSpecs"/>.
/// </summary>
public class AgentCostRollupApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentCostRollupApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task GetCost_UnknownProjectReturns404()
    {
        using var response = await _client.GetAsync($"/api/projects/unknown-project-{Guid.NewGuid():N}/agent/cost");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetCost_UnknownRange_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync();
        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/agent/cost?range=bad");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("7d")]
    [InlineData("30d")]
    [InlineData("90d")]
    public async Task GetCost_AcceptedRangeValues_AllReturnOk(string range)
    {
        var project = await CreateProjectAsync();
        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/agent/cost?range={range}");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var name = $"cost-{Guid.NewGuid():N}";
        return await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            name,
            repoName: "main",
            gitUrl: $"file://{Guid.NewGuid():N}");
    }

    private sealed record ProjectDto(string Id, string Name);
}
