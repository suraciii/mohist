using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Issue.Domain;
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
    public async Task GetUsage_BucketTotalsSumAdditiveFields()
    {
        var project = await CreateProjectAsync();
        var bucketDay = Today.AddDays(-1);
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

    [Fact]
    public async Task GetUsage_CompletedSessionsAreIncluded()
    {
        var project = await CreateProjectAsync();
        var bucketDay = Today.AddDays(-3);
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

    [Fact]
    public async Task GetUsage_EmptyBucketHasZeroTotals()
    {
        var project = await CreateProjectAsync();
        var dayWithSession = Today.AddDays(-1);
        await InsertSessionAsync(project.Id, dayWithSession.AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        var dayWithoutSession = Today.AddDays(-5);
        var emptyBucket = response.Buckets.Single(b => b.BucketStart.Date == dayWithoutSession.Date);
        Assert.Equal(0, emptyBucket.InputTokens);
        Assert.Equal(0, emptyBucket.OutputTokens);
        Assert.Equal(0, emptyBucket.TotalTokens);
        Assert.Equal(0, emptyBucket.CostAmount);
    }

    [Fact]
    public async Task GetUsage_SessionWithoutUsageIsSkipped()
    {
        var project = await CreateProjectAsync();
        await InsertSessionWithoutUsageAsync(project.Id, Today.AddDays(-2).AddHours(10));

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        var bucket = response.Buckets.Single(b => b.BucketStart.Date == Today.AddDays(-2));
        Assert.Equal(0, bucket.InputTokens);
        Assert.Equal(0, bucket.OutputTokens);
        Assert.Equal(0, bucket.TotalTokens);
        Assert.Equal(0, bucket.CostAmount);
    }

    [Fact]
    public async Task GetUsage_CostCurrencyIsEchoed()
    {
        var project = await CreateProjectAsync();
        var bucketDay = Today.AddDays(-1);
        await InsertSessionAsync(project.Id, bucketDay.AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "EUR");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        var bucket = response.Buckets.Single(b => b.BucketStart.Date == bucketDay.Date);
        Assert.Equal("EUR", bucket.CostCurrency);
    }

    [Fact]
    public async Task GetUsage_UnknownProjectReturnsNotFound()
    {
        using var response = await _client.GetAsync($"/api/projects/unknown-project-{Guid.NewGuid():N}/agent/usage");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetUsage_SessionsOutsideRangeAreNotIncluded()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-10),
            inputTokens: 999, outputTokens: 999, totalTokens: 1998, costAmount: 9.99, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        var totalInput = response.Buckets.Sum(b => b.InputTokens);
        Assert.Equal(0, totalInput);
    }

    [Fact]
    public async Task GetUsage_RangeToBoundaryIsExclusive()
    {
        var project = await CreateProjectAsync();
        var rangeTo = Today.AddDays(1);
        await InsertSessionAsync(project.Id, rangeTo,
            inputTokens: 999, outputTokens: 999, totalTokens: 1998, costAmount: 9.99, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.Equal(response.RangeTo, rangeTo);
        var totalInput = response.Buckets.Sum(b => b.InputTokens);
        Assert.Equal(0, totalInput);
    }

    [Fact]
    public async Task GetUsage_CumulativeSeriesHasSameLengthAsBuckets()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.NotNull(response.CumulativeCostPerShip);
        Assert.Equal(response.Buckets.Count, response.CumulativeCostPerShip.Count);
    }

    [Fact]
    public async Task GetUsage_CumulativeSeriesComputesPrefixSums()
    {
        var project = await CreateProjectAsync();

        // Use the API's own RangeFrom to align session creation with window.
        // First request establishes the window boundaries.
        var emptyResponse = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");
        var rangeFrom = emptyResponse.RangeFrom;

        // Create sessions at known bucket positions relative to rangeFrom.
        // Bucket 2: session at rangeFrom + 2 days, cost 0.02
        await InsertSessionAsync(project.Id, rangeFrom.AddDays(2).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");
        // Bucket 6: session at rangeFrom + 6 days, cost 0.05
        await InsertSessionAsync(project.Id, rangeFrom.AddDays(6).AddHours(10),
            inputTokens: 200, outputTokens: 80, totalTokens: 280, costAmount: 0.05, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.NotNull(response.CumulativeCostPerShip);
        Assert.Equal(7, response.CumulativeCostPerShip.Count);

        // No pre-window data: preWindowSpend = 0, preWindowSamples = 0.
        // No shipped issues: cumulativeShipped = 0 for all days.
        // Day 0-1: no sessions yet → cumulativeSamples=0 → CumulativeCost=null
        for (var i = 0; i < 2; i++)
        {
            Assert.Null(response.CumulativeCostPerShip[i].CumulativeCost);
            Assert.Null(response.CumulativeCostPerShip[i].Currency);
            Assert.Equal(0, response.CumulativeCostPerShip[i].CumulativeShippedCount);
            Assert.Null(response.CumulativeCostPerShip[i].CostPerShip);
        }

        // Day 2: first session (0.02) → cumulativeSamples=1, cumulativeCost=0.02
        Assert.Equal(0.02, response.CumulativeCostPerShip[2].CumulativeCost);
        Assert.Equal("USD", response.CumulativeCostPerShip[2].Currency);
        Assert.Equal(0, response.CumulativeCostPerShip[2].CumulativeShippedCount);
        Assert.Null(response.CumulativeCostPerShip[2].CostPerShip);

        // Day 3-5: no new sessions → cumulative stays at 0.02
        for (var i = 3; i <= 5; i++)
        {
            Assert.Equal(0.02, response.CumulativeCostPerShip[i].CumulativeCost);
        }

        // Day 6: second session (0.05) → cumulativeCost = 0.02 + 0.05 = 0.07
        Assert.Equal(0.07, response.CumulativeCostPerShip[6].CumulativeCost);
    }

    [Fact]
    public async Task GetUsage_CumulativeShippedCountsIssuesOnOrBeforeDayEnd()
    {
        var project = await CreateProjectAsync();

        // Use the API's own RangeFrom to determine window boundaries.
        var emptyResponse = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");
        var rangeFrom = emptyResponse.RangeFrom;

        // Issue at rangeFrom + 2 days (bucket index 2)
        await InsertDoneIssueAsync(project.Id, 1, "Done mid",
            rangeFrom.AddDays(2).AddHours(12));

        // Issue at rangeFrom + 5 days (bucket index 5)
        await InsertDoneIssueAsync(project.Id, 2, "Done late",
            rangeFrom.AddDays(5).AddHours(8));

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.NotNull(response.CumulativeCostPerShip);
        Assert.Equal(7, response.CumulativeCostPerShip.Count);

        // No pre-window issues: preWindowShipped = 0
        // Day 0-1: no window issues yet → cumulative shipped = 0
        Assert.Equal(0, response.CumulativeCostPerShip[0].CumulativeShippedCount);
        Assert.Equal(0, response.CumulativeCostPerShip[1].CumulativeShippedCount);

        // Day 2: issue at rangeFrom+2 → cumulative shipped = 1
        Assert.Equal(1, response.CumulativeCostPerShip[2].CumulativeShippedCount);

        // Day 3-4: no new issues → cumulative shipped = 1
        Assert.Equal(1, response.CumulativeCostPerShip[3].CumulativeShippedCount);
        Assert.Equal(1, response.CumulativeCostPerShip[4].CumulativeShippedCount);

        // Day 5: issue at rangeFrom+5 → cumulative shipped = 2
        Assert.Equal(2, response.CumulativeCostPerShip[5].CumulativeShippedCount);
        Assert.Equal(2, response.CumulativeCostPerShip[6].CumulativeShippedCount);
    }

    [Fact]
    public async Task GetUsage_CumulativeCostPerShipIsNullWhenNoShipped()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.NotNull(response.CumulativeCostPerShip);

        // No issues exist, so cumulativeShipped = 0 → CostPerShip = null for all days
        foreach (var point in response.CumulativeCostPerShip)
        {
            Assert.Null(point.CostPerShip);
            Assert.Equal(0, point.CumulativeShippedCount);
        }
    }

    [Fact]
    public async Task GetUsage_CumulativeZeroCostWithShippedIsGenuineZero()
    {
        var project = await CreateProjectAsync();

        // Session with zero cost but non-null usage
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(10),
            inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 0, costCurrency: "USD");

        // A shipped issue
        await InsertDoneIssueAsync(project.Id, 1, "Done issue",
            Today.AddDays(-1).AddHours(12));

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.NotNull(response.CumulativeCostPerShip);

        // Find a day where shipped > 0
        var dayWithShip = response.CumulativeCostPerShip
            .FirstOrDefault(p => p.CumulativeShippedCount > 0);

        Assert.NotNull(dayWithShip);
        // Cost is 0, shipped > 0 → CostPerShip should be 0 (not null)
        Assert.Equal(0.0, dayWithShip.CostPerShip);
    }

    [Fact]
    public async Task GetUsage_CumulativeCostPerShipIsZeroWhenShippedExistsButNoUsageSamples()
    {
        var project = await CreateProjectAsync();

        var emptyResponse = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");
        var rangeFrom = emptyResponse.RangeFrom;

        await InsertDoneIssueAsync(project.Id, 1, "Done without usage",
            rangeFrom.AddDays(1).AddHours(12));

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.NotNull(response.CumulativeCostPerShip);

        var shippedPoints = response.CumulativeCostPerShip
            .Where(point => point.CumulativeShippedCount > 0)
            .ToArray();

        Assert.NotEmpty(shippedPoints);
        foreach (var point in shippedPoints)
        {
            Assert.Equal(0, point.CumulativeCost);
            Assert.Null(point.Currency);
            Assert.Equal(0, point.CostPerShip);
        }
    }

    [Fact]
    public async Task GetUsage_CumulativeSeriesIncludesPreWindowHistory()
    {
        var project = await CreateProjectAsync();

        var emptyResponse = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");
        var rangeFrom = emptyResponse.RangeFrom;

        await InsertSessionAsync(project.Id, rangeFrom.AddDays(-2).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.03, costCurrency: "USD");
        await InsertDoneIssueAsync(project.Id, 1, "Done before window",
            rangeFrom.AddDays(-1).AddHours(12));

        await InsertSessionAsync(project.Id, rangeFrom.AddDays(1).AddHours(10),
            inputTokens: 200, outputTokens: 80, totalTokens: 280, costAmount: 0.02, costCurrency: "USD");
        await InsertDoneIssueAsync(project.Id, 2, "Done in window",
            rangeFrom.AddDays(2).AddHours(8));

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.NotNull(response.CumulativeCostPerShip);
        Assert.Equal(0.03, response.CumulativeCostPerShip[0].CumulativeCost);
        Assert.Equal(1, response.CumulativeCostPerShip[0].CumulativeShippedCount);
        Assert.Equal(0.03, response.CumulativeCostPerShip[0].CostPerShip);

        Assert.Equal(0.05, response.CumulativeCostPerShip[1].CumulativeCost);
        Assert.Equal(1, response.CumulativeCostPerShip[1].CumulativeShippedCount);
        Assert.Equal(0.05, response.CumulativeCostPerShip[1].CostPerShip);

        Assert.Equal(0.05, response.CumulativeCostPerShip[2].CumulativeCost);
        Assert.Equal(2, response.CumulativeCostPerShip[2].CumulativeShippedCount);
        Assert.Equal(0.025, response.CumulativeCostPerShip[2].CostPerShip);

        Assert.Equal(0.02, response.Buckets.Sum(bucket => bucket.CostAmount));
    }

    [Fact]
    public async Task GetUsage_CumulativeZeroSampleProject_ReturnsDefinedEmptyResult()
    {
        var project = await CreateProjectAsync();
        // No sessions, no issues

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.NotNull(response.CumulativeCostPerShip);
        Assert.Equal(7, response.CumulativeCostPerShip.Count);

        foreach (var point in response.CumulativeCostPerShip)
        {
            Assert.Null(point.CumulativeCost);
            Assert.Null(point.Currency);
            Assert.Equal(0, point.CumulativeShippedCount);
            Assert.Null(point.CostPerShip);
        }
    }

    [Fact]
    public async Task GetUsage_Range7d_Daily7Buckets()
    {
        // `range=7d` ⇒ day(7). Recorded in design.md D5.
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage?range=7d");

        Assert.Equal("day", response.BucketGranularity);
        Assert.Equal(7, response.Buckets.Count);
        Assert.Equal(TimeSpan.FromDays(7), response.RangeTo - response.RangeFrom);
    }

    [Fact]
    public async Task GetUsage_Range30d_Daily30Buckets()
    {
        // `range=30d` ⇒ day(30). Recorded in design.md D5.
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-15).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage?range=30d");

        Assert.Equal("day", response.BucketGranularity);
        Assert.Equal(30, response.Buckets.Count);
        Assert.Equal(TimeSpan.FromDays(30), response.RangeTo - response.RangeFrom);
    }

    [Fact]
    public async Task GetUsage_Range90d_Weekly13Buckets()
    {
        // `range=90d` ⇒ week(ceil(90/7)=13). Recorded in design.md D5.
        // The bucketGranularity field reports the chosen granularity
        // (week) and the cumulative-cost-per-ship sub-series follows
        // the same bucket grid.
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-20).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.05, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage?range=90d");

        Assert.Equal("week", response.BucketGranularity);
        Assert.Equal(13, response.Buckets.Count);
        Assert.Equal(TimeSpan.FromDays(90), response.RangeTo - response.RangeFrom);
        Assert.NotNull(response.CumulativeCostPerShip);
        Assert.Equal(response.Buckets.Count, response.CumulativeCostPerShip!.Count);
    }

    [Fact]
    public async Task GetUsage_OmittedRange_Reproduces7Day7BucketDaily()
    {
        // Omit-equality witness: omitting `range` reproduces today's
        // fixed 7-day / 7-bucket daily timeseries — the Dashboard
        // back-compat default.
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var response = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.Equal("day", response.BucketGranularity);
        Assert.Equal(7, response.Buckets.Count);
        Assert.Equal(TimeSpan.FromDays(7), response.RangeTo - response.RangeFrom);
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
        await db.SaveChangesAsync();
    }

    private sealed record ProjectDto(string Id, string Name);

    private sealed record UsageTimeseriesResponseDto(
        DateTime RangeFrom,
        DateTime RangeTo,
        string BucketGranularity,
        IReadOnlyList<UsageBucketResponseDto> Buckets,
        IReadOnlyList<CumulativeCostPerShipPointResponseDto>? CumulativeCostPerShip = null);

    private sealed record UsageBucketResponseDto(
        DateTime BucketStart,
        DateTime BucketEnd,
        long InputTokens,
        long OutputTokens,
        long TotalTokens,
        double CostAmount,
        string? CostCurrency);

    private sealed record CumulativeCostPerShipPointResponseDto(
        DateTime DayEnd,
        double? CumulativeCost,
        string? Currency,
        int CumulativeShippedCount,
        double? CostPerShip);

    private async Task InsertDoneIssueAsync(string projectId, int number, string title, DateTime completedAt)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = IssueStatus.Done,
            CompletedAt = completedAt,
        };

        await using var db = await _fixture.Services
            .GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }
}
