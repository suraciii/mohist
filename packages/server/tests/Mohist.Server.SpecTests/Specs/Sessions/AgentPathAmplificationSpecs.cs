using System.Net;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Route contract for the path-amplification surface behind
/// <c>/api/projects/{ref}/agent/{status|activity}</c> and the alias
/// <c>/api/agent/{status|activity}</c> family. The amplification values
/// (candidates, processed, transcript-records, database/downstream
/// calls) live in <c>AgentPathAmplificationQuerierSpecs</c>.
/// </summary>
[Collection("IntegrationSessions")]
public sealed class AgentPathAmplificationSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentPathAmplificationSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Theory]
    [InlineData("status")]
    [InlineData("activity")]
    public async Task Alias_WithoutNonblankSelector_ReturnsBadRequest(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/agent/{path}?projectId=%20%20");
        request.Headers.Add("X-Mohist-Project", "  ");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("No active project", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("status", true)]
    [InlineData("activity", false)]
    [InlineData("activity", true)]
    public async Task Alias_AcceptsTrimmedProjectIdOrName(string path, bool useName)
    {
        var project = await CreateProjectAsync($"alias-{path}-{useName}");
        var selector = useName ? project.Name : project.Id;

        using var response = await _client.GetAsync($"/api/agent/{path}?projectId={Uri.EscapeDataString($"  {selector}  ")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("activity")]
    public async Task Blank_Query_FallsBackToTrimmedHeader(string path)
    {
        var project = await CreateProjectAsync($"header-{path}");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/agent/{path}?projectId=%20");
        request.Headers.Add("X-Mohist-Project", $"  {project.Name}  ");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("activity")]
    public async Task First_Nonblank_Query_WinsConflictingHeader(string path)
    {
        var selected = await CreateProjectAsync($"query-{path}");
        var header = await CreateProjectAsync($"header-conflict-{path}");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/agent/{path}?projectId=%20&projectId={Uri.EscapeDataString(selected.Id)}");
        request.Headers.Add("X-Mohist-Project", header.Id);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("activity")]
    public async Task Alias_And_Canonical_Routes_Use_SameHandler(string path)
    {
        var project = await CreateProjectAsync($"parity-{path}");

        using var canonical = await _client.GetAsync($"/api/projects/{project.Id}/agent/{path}");
        using var alias = await _client.GetAsync($"/api/agent/{path}?projectId={Uri.EscapeDataString(project.Id)}");

        Assert.Equal(HttpStatusCode.OK, canonical.StatusCode);
        Assert.Equal(HttpStatusCode.OK, alias.StatusCode);
        var canonicalBody = await ReadDataAsync(canonical);
        var aliasBody = await ReadDataAsync(alias);
        Assert.True(JsonElement.DeepEquals(canonicalBody, aliasBody));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("activity")]
    public async Task Alias_Unknown_Project_Returns_NotFound(string path)
    {
        using var response = await _client.GetAsync($"/api/agent/{path}?projectId=proj_unknown_{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<ProjectDto> CreateProjectAsync(string suffix) =>
        await _client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>(
            "/api/projects",
            $"amp-{suffix}-{Guid.NewGuid():N}");

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var envelope = await JsonSerializer.DeserializeAsync<JsonElement>(await response.Content.ReadAsStreamAsync());
        return envelope.GetProperty("data");
    }

    private sealed record ProjectDto(string Id, string Name);
}