using Mohist.Server.SpecTests.Support;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Api;

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
[Collection("MohistIntegration2")]
public class EpicListQueryApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public EpicListQueryApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task ListWithSearch_FiltersByTitleSubstringCaseInsensitive()
    {
        var project = await CreateProjectAsync();
        await CreateEpicAsync(project.Id, "Authentication overhaul", "p2");
        await CreateEpicAsync(project.Id, "Billing dunning", "p2");
        await CreateEpicAsync(project.Id, "OAuth integration", "p2");

        var matchAll = await _client.GetDataAsync<EpicRowDto[]>($"/api/projects/{project.Id}/epics?search=auth");
        Assert.Equal(2, matchAll.Length);
        Assert.Contains(matchAll, e => e.Title == "Authentication overhaul");
        Assert.Contains(matchAll, e => e.Title == "OAuth integration");

        var mixedCase = await _client.GetDataAsync<EpicRowDto[]>($"/api/projects/{project.Id}/epics?search=AuTh");
        Assert.Equal(2, mixedCase.Length);
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
    public async Task ListWithSortPriorityAsc_OrdersP0BeforeP2()
    {
        var project = await CreateProjectAsync();
        var p2 = await CreateEpicAsync(project.Id, "Should be second (p2)", "p2");
        var p0 = await CreateEpicAsync(project.Id, "Should be first (p0)", "p0");

        var list = await _client.GetDataAsync<EpicRowDto[]>($"/api/projects/{project.Id}/epics?sort=priority&dir=asc");

        Assert.Equal(2, list.Length);
        Assert.Equal(p0.Number, list[0].Number);
        Assert.Equal("p0", list[0].Priority);
        Assert.Equal(p2.Number, list[1].Number);
        Assert.Equal("p2", list[1].Priority);
    }

    [Fact]
    public async Task ListWithoutSort_KeepsLegacyDefaultOrdering()
    {
        var project = await CreateProjectAsync();
        var p2Earlier = await CreateEpicAsync(project.Id, "P2 created first", "p2");
        var p2Later = await CreateEpicAsync(project.Id, "P2 created later", "p2");

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await _client.PatchAsJsonAsync(
            $"/api/projects/{project.Id}/epics/{p2Later.Number}",
            new { title = "P2 renamed for bump" });

        var list = await _client.GetDataAsync<EpicRowDto[]>($"/api/projects/{project.Id}/epics");

        Assert.Equal(2, list.Length);
        Assert.Equal(p2Later.Number, list[0].Number);
        Assert.Equal(p2Earlier.Number, list[1].Number);
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
    public async Task ListWithSearch_WildcardCharactersAreLiteralTitleSubstrings()
    {
        var project = await CreateProjectAsync();
        var percent = await CreateEpicAsync(project.Id, "Progress 100%", "p2");
        var underscore = await CreateEpicAsync(project.Id, "Auth_token", "p2");
        await CreateEpicAsync(project.Id, "Plain title", "p2");

        var percentMatches = await _client.GetDataAsync<EpicRowDto[]>($"/api/projects/{project.Id}/epics?search=%25");
        Assert.Single(percentMatches);
        Assert.Equal(percent.Number, percentMatches[0].Number);

        var underscoreMatches = await _client.GetDataAsync<EpicRowDto[]>($"/api/projects/{project.Id}/epics?search=_");
        Assert.Single(underscoreMatches);
        Assert.Equal(underscore.Number, underscoreMatches[0].Number);
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
        var project = await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", $"epic-list-query-{Guid.NewGuid():N}");
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            setDefault = true,
        });
        return project;
    }

    private async Task<EpicRowDto> CreateEpicAsync(string projectId, string title, string priority)
    {
        var epic = await _client.PostDataAsync<EpicRowDto>($"/api/projects/{projectId}/epics", new { title, description = string.Empty, priority });
        return epic;
    }

    private sealed record ProjectDto(string Id);
    private sealed record EpicRowDto(int Number, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);
}
