using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class AgentUsageTimeseriesApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentUsageTimeseriesApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    private DateTime Today => _fixture.TimeProvider.GetUtcNow().UtcDateTime.Date;

    [Fact]
    public async Task GetUsage_ReturnsSevenDailyBucketsWithCorrectStructure()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.NotEqual(default, response.RangeFrom);
        Assert.NotEqual(default, response.RangeTo);
        Assert.Equal("day", response.BucketGranularity);
        Assert.Equal(7, response.Buckets.Count);
        Assert.Equal(response.Buckets, response.Buckets.OrderBy(b => b.BucketStart).ToList());
    }

    [Fact]
    public async Task GetUsage_UnknownProjectReturnsNotFound()
    {
        using var response = await _client.GetAsync($"/api/projects/unknown-project-{Guid.NewGuid():N}/agent/usage");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetUsage_UnknownRange_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync();
        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/agent/usage?range=bad");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
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
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetUsage_Range90d_BucketGranularityReportsWeekly()
    {
        // The existing `bucketGranularity` field reports the chosen
        // granularity: day for 7d/30d, week for 90d. Asserted
        // explicitly so the wire contract is documented.
        var project = await CreateProjectAsync();

        var r7 = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage?range=7d");
        var r30 = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage?range=30d");
        var r90 = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage?range=90d");

        Assert.Equal("day", r7.BucketGranularity);
        Assert.Equal("day", r30.BucketGranularity);
        Assert.Equal("week", r90.BucketGranularity);
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

    private async Task InsertSessionAsync(
        string projectId,
        DateTime createdAt,
        long inputTokens = 0,
        long outputTokens = 0,
        long totalTokens = 0,
        double costAmount = 0,
        string? costCurrency = null,
        string? agentSessionId = null)
    {
        var session = new AgentSession
        {
            Id = $"session-{Guid.NewGuid():N}",
            Runtime = new AgentSessionRuntime("runner-test", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: createdAt,
                UsageSummary: new AgentUsageSummary(
                    InputTokens: inputTokens,
                    OutputTokens: outputTokens,
                    TotalTokens: totalTokens,
                    CostAmount: costAmount,
                    CostCurrency: costCurrency),
                AgentRuntimeSessionId: agentSessionId),
            Metadata = new AgentSessionMetadata(
                Labels: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                })
        };

        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = session.Id,
            State = JsonSerializer.Serialize(session, AgentSessionJson.JsonOptions),
            CreatedAt = createdAt,
            Status = agentSessionId is null ? "opened" : "bound",
            AgentSessionId = agentSessionId,
            RunnerId = "runner-test",
        });
        await db.SaveChangesAsync();
    }

    private sealed record ProjectDto(string Id, string Name);

    private sealed record UsageTimeseriesResponseDto(
        DateTime RangeFrom,
        DateTime RangeTo,
        string BucketGranularity,
        IReadOnlyList<UsageBucketResponseDto> Buckets);

    private sealed record UsageBucketResponseDto(
        DateTime BucketStart,
        DateTime BucketEnd,
        long InputTokens,
        long OutputTokens,
        long TotalTokens,
        double CostAmount,
        string? CostCurrency);

}
