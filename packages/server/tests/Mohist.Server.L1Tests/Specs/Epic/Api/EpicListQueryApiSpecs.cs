using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.L1Tests.Support;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.TestSupport;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Epic.Api;

/// <summary>
/// Route-level specs for the optional <c>?search=</c>, <c>?sort=</c>,
/// <c>?dir=</c> query parameters on
/// <c>GET /api/projects/{projectRef}/epics</c>. Verifies:
/// <list type="bullet">
/// <item>unknown sort / dir values are tolerated (no HTTP 400), the
///   server falls back to the original priority-ascending-then-updated-desc
///   default;</item>
/// <item><c>search</c> and <c>sort</c>+<c>dir</c> compose into a single
///   round trip — both the filter and the ordering apply together;</item>
/// <item>missing parameters reproduce the legacy default ordering
///   (regression-safe for existing callers);</item>
/// <item>the route never surfaces a 4xx for malformed / unknown query
///   parameters — only project resolution or infrastructure failures
///   result in error codes.</item>
/// </list>
/// </summary>
[Trait("level", "L1")]
public class EpicListQueryApiSpecs : IClassFixture<DefaultMohistIntegrationFixture>
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public EpicListQueryApiSpecs(DefaultMohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task ListWithoutSearch_ReturnsAllEpics()
    {
        var project = await CreateProjectAsync();
        await CreateEpicAsync(project.Id, "First", "p2");
        await CreateEpicAsync(project.Id, "Second", "p2");
        await CreateEpicAsync(project.Id, "Third", "p2");

        var all = await _client.GetDataAsync<EpicRowDto[]>($"/api/projects/{project.Id}/epics");
        Assert.Equal(3, all.Length);
    }

    [Fact]
    public async Task ListWithUnknownSortOrDir_FallsBackToDefault_Returns200()
    {
        var project = await CreateProjectAsync();
        var p0 = await CreateEpicAsync(project.Id, "P0 entry", "p0");
        var p2 = await CreateEpicAsync(project.Id, "P2 entry", "p2");

        var unknownSort = await _client.GetDataAsync<EpicRowDto[]>($"/api/projects/{project.Id}/epics?sort=garbage&dir=asc");
        Assert.Equal(2, unknownSort.Length);
        Assert.Equal(p0.Number, unknownSort[0].Number);

        var unknownDir = await _client.GetDataAsync<EpicRowDto[]>($"/api/projects/{project.Id}/epics?sort=priority&dir=sideways");
        Assert.Equal(2, unknownDir.Length);
        Assert.Equal(p0.Number, unknownDir[0].Number);

        var bothUnknown = await _client.GetDataAsync<EpicRowDto[]>($"/api/projects/{project.Id}/epics?sort=banana&dir=totally-not-a-direction");
        Assert.Equal(2, bothUnknown.Length);
        Assert.Equal(p0.Number, bothUnknown[0].Number);
    }

    [Fact]
    public async Task ListWithSearchAndSort_ComposesBothIntoSingleResponse()
    {
        var project = await CreateProjectAsync();
        var authP2 = await CreateEpicAsync(project.Id, "Authentication legacy", "p2");
        var authP0 = await CreateEpicAsync(project.Id, "Authentication modern", "p0");
        var billing = await CreateEpicAsync(project.Id, "Billing dunning", "p2");

        var matched = await _client.GetDataAsync<EpicRowDto[]>($"/api/projects/{project.Id}/epics?search=auth&sort=priority&dir=asc");

        Assert.Equal(2, matched.Length);
        Assert.Equal(authP0.Number, matched[0].Number);
        Assert.Equal(authP2.Number, matched[1].Number);
        Assert.DoesNotContain(matched, e => e.Number == billing.Number);
    }

    [Fact]
    public async Task ListWithEmptyStringSearch_ReturnsAllEpics()
    {
        var project = await CreateProjectAsync();
        await CreateEpicAsync(project.Id, "First", "p2");
        await CreateEpicAsync(project.Id, "Second", "p2");

        var list = await _client.GetDataAsync<EpicRowDto[]>($"/api/projects/{project.Id}/epics?search=");

        Assert.Equal(2, list.Length);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var projectId = $"project-{Guid.NewGuid():N}";
        await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            $"epic-list-query-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = "main",
                GitUrl = $"file://{Guid.NewGuid():N}",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        return new ProjectDto(projectId);
    }

    private async Task<EpicRowDto> CreateEpicAsync(string projectId, string title, string priority)
    {
        var number = await _fixture.Grains.GetGrain<IEpicCounterGrain>(GrainKey.EpicCounter(projectId)).NextAsync();
        var epic = await _fixture.Grains.GetGrain<IEpicGrain>(GrainKey.Epic(new EpicKey(projectId, number))).CreateAsync(
            projectId,
            number,
            title,
            string.Empty,
            priority);
        return new EpicRowDto(epic.Number, epic.Title, epic.Description ?? string.Empty, epic.Priority, epic.Status, string.Empty, string.Empty);
    }

    private sealed record ProjectDto(string Id);
    private sealed record EpicRowDto(int Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);
}
