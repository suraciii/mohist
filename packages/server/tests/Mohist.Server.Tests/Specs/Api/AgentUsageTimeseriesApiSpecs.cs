using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Services.Sessions;
using Xunit;

namespace Mohist.Server.Tests.Specs.Api;

[Collection("MohistIntegration")]
public class AgentUsageTimeseriesApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentUsageTimeseriesApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetUsage_ReturnsSevenDailyBucketsWithCorrectStructure()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddDays(-2).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.NotEqual(default, response.RangeFrom);
        Assert.NotEqual(default, response.RangeTo);
        Assert.Equal("day", response.BucketGranularity);
        Assert.Equal(7, response.Buckets.Count);
        Assert.Equal(response.Buckets, response.Buckets.OrderBy(b => b.BucketStart).ToList());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetUsage_BucketTotalsSumAdditiveFields()
    {
        var project = await CreateProjectAsync();
        var bucketDay = DateTime.UtcNow.Date.AddDays(-1);
        await InsertSessionAsync(project.Id, bucketDay.AddHours(8),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");
        await InsertSessionAsync(project.Id, bucketDay.AddHours(12),
            inputTokens: 200, outputTokens: 80, totalTokens: 280, costAmount: 0.05, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        var bucket = response.Buckets.Single(b => b.BucketStart.Date == bucketDay.Date);
        Assert.Equal(300, bucket.InputTokens);
        Assert.Equal(130, bucket.OutputTokens);
        Assert.Equal(430, bucket.TotalTokens);
        Assert.Equal(0.07, bucket.CostAmount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetUsage_CompletedSessionsAreIncluded()
    {
        var project = await CreateProjectAsync();
        var bucketDay = DateTime.UtcNow.Date.AddDays(-3);
        await InsertSessionAsync(project.Id, bucketDay.AddHours(10),
            inputTokens: 500, outputTokens: 200, totalTokens: 700, costAmount: 0.10, costCurrency: "USD",
            agentSessionId: "some-runtime-id");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        var bucket = response.Buckets.Single(b => b.BucketStart.Date == bucketDay.Date);
        Assert.Equal(500, bucket.InputTokens);
        Assert.Equal(200, bucket.OutputTokens);
        Assert.Equal(700, bucket.TotalTokens);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetUsage_EmptyBucketHasZeroTotals()
    {
        var project = await CreateProjectAsync();
        var dayWithSession = DateTime.UtcNow.Date.AddDays(-1);
        await InsertSessionAsync(project.Id, dayWithSession.AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        var dayWithoutSession = DateTime.UtcNow.Date.AddDays(-5);
        var emptyBucket = response.Buckets.Single(b => b.BucketStart.Date == dayWithoutSession.Date);
        Assert.Equal(0, emptyBucket.InputTokens);
        Assert.Equal(0, emptyBucket.OutputTokens);
        Assert.Equal(0, emptyBucket.TotalTokens);
        Assert.Equal(0, emptyBucket.CostAmount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetUsage_SessionWithoutUsageIsSkipped()
    {
        var project = await CreateProjectAsync();
        await InsertSessionWithoutUsageAsync(project.Id, DateTime.UtcNow.Date.AddDays(-2).AddHours(10));

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        var bucket = response.Buckets.Single(b => b.BucketStart.Date == DateTime.UtcNow.Date.AddDays(-2));
        Assert.Equal(0, bucket.InputTokens);
        Assert.Equal(0, bucket.OutputTokens);
        Assert.Equal(0, bucket.TotalTokens);
        Assert.Equal(0, bucket.CostAmount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetUsage_CostCurrencyIsEchoed()
    {
        var project = await CreateProjectAsync();
        var bucketDay = DateTime.UtcNow.Date.AddDays(-1);
        await InsertSessionAsync(project.Id, bucketDay.AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "EUR");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        var bucket = response.Buckets.Single(b => b.BucketStart.Date == bucketDay.Date);
        Assert.Equal("EUR", bucket.CostCurrency);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetUsage_UnknownProjectReturnsNotFound()
    {
        using var response = await _client.GetAsync($"/api/projects/unknown-project-{Guid.NewGuid():N}/agent/usage");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetUsage_SessionsOutsideRangeAreNotIncluded()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddDays(-10),
            inputTokens: 999, outputTokens: 999, totalTokens: 1998, costAmount: 9.99, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        var totalInput = response.Buckets.Sum(b => b.InputTokens);
        Assert.Equal(0, totalInput);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetUsage_RangeToBoundaryIsExclusive()
    {
        var project = await CreateProjectAsync();
        var rangeTo = DateTime.UtcNow.Date.AddDays(1);
        await InsertSessionAsync(project.Id, rangeTo,
            inputTokens: 999, outputTokens: 999, totalTokens: 1998, costAmount: 9.99, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.Equal(response.RangeTo, rangeTo);
        var totalInput = response.Buckets.Sum(b => b.InputTokens);
        Assert.Equal(0, totalInput);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var name = $"usage-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name });
        await _client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = "main",
            gitUrl = $"file://{Guid.NewGuid():N}",
            baseBranch = "main",
            isDefault = true,
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
        db.AgentSessionLabels.Add(new AgentSessionLabelRow
        {
            SessionId = session.Id,
            Key = AgentSessionQueryMetadataKeys.ProjectId,
            Value = projectId,
        });
        await db.SaveChangesAsync();
    }

    private async Task InsertSessionWithoutUsageAsync(string projectId, DateTime createdAt)
    {
        var session = new AgentSession
        {
            Id = $"session-{Guid.NewGuid():N}",
            Runtime = new AgentSessionRuntime("runner-test", null),
            Settings = new AgentSessionSettings("test-model"),
            Status = new AgentSessionStatusSnapshot(
                CreatedAt: createdAt,
                UsageSummary: null),
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
            Status = "opened",
            RunnerId = "runner-test",
        });
        db.AgentSessionLabels.Add(new AgentSessionLabelRow
        {
            SessionId = session.Id,
            Key = AgentSessionQueryMetadataKeys.ProjectId,
            Value = projectId,
        });
        await db.SaveChangesAsync();
    }

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);

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
