using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue")]
public class IssueMetricsApiSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;

    public IssueMetricsApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _fixture = fixture;
    }

    [Theory]
    [InlineData("cumulative-flow")]
    [InlineData("cumulative-flow?range=30d")]
    public async Task CumulativeFlowEndpoint_Removed_ReturnsNotFound(string queryString)
    {
        var project = await CreateProjectAsync($"cumulative-flow-removed-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/{queryString}");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CompletionMetrics_UnsupportedBucket_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync($"metrics-bad-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=month");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task QualityMetrics_UnknownProject_ReturnsNotFound()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj-quality-unknown-{Guid.NewGuid():N}/issues/metrics/quality");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeliveryTimeMetrics_UnknownProject_ReturnsNotFound()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj-dt-unknown-{Guid.NewGuid():N}/issues/metrics/delivery-time");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StageDurationMetrics_UnknownProject_ReturnsNotFound()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj-sd-unknown-{Guid.NewGuid():N}/issues/metrics/stage-duration");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("completion?bucket=day&range=bad")]
    [InlineData("completion?bucket=week&range=bad")]
    [InlineData("delivery-time?range=bad")]
    [InlineData("stage-duration?range=bad")]
    [InlineData("quality?range=bad")]
    [InlineData("approval-wait?range=bad")]
    public async Task RangeQuery_UnknownValue_ReturnsBadRequest(string queryString)
    {
        var project = await CreateProjectAsync($"range-bad-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/{queryString}");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<ProjectDto> CreateProjectAsync(string name)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name,
                repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "trunk" },
            },
            JsonOptions);
        response.EnsureSuccessStatusCode();
        var project = await ReadDataAsync<ProjectDto>(response);
        return project;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(JsonOptions);
        if (envelope is null) throw new InvalidOperationException("Empty API response");
        if (!envelope.Success) throw new InvalidOperationException(envelope.Error ?? "API request failed");
        return envelope.Data!;
    }

    private sealed record ApiEnvelope<T>(bool Success, T? Data, string? Error = null, string? Code = null, object? Details = null);
    private sealed record ProjectDto(string Id, string Name);
}