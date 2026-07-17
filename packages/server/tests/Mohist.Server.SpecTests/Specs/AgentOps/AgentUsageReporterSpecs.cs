using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Sessions.Services;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.AgentOps;

/// <summary>
/// Reporter-level specs for <see cref="AgentUsageReporter"/> (issue-327 T-004
/// / design D1, D2). Covers <see cref="AgentUsageReporter.GetCostWindowedAsync"/>,
/// <see cref="AgentUsageReporter.GetCostRollupAsync"/>, and
/// <see cref="AgentUsageReporter.GetUsageTimeseriesAsync"/>. Mirrors the
/// <see cref="IssueQuerierSpecs"/> adjacency/length/empty-result pattern,
/// driven by the integration fixture's pinned
/// <see cref="FakeTimeProvider"/> (2026-06-30 00:00 UTC). API-level coverage
/// lives in <c>AgentCostRollupApiSpecs</c> and <c>AgentUsageTimeseriesApiSpecs</c>;
/// these specs assert the reporter-level contract directly so reporting
/// regressions are caught without spinning a full route.
/// </summary>
[Collection("IntegrationSessions")]
public class AgentUsageReporterSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentUsageReporterSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private DateTime Today => _fixture.TimeProvider.GetUtcNow().UtcDateTime.Date;

    private AgentUsageReporter ResolveReporter()
    {
        var scope = _fixture.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentUsageReporter>();
    }

    [Fact]
    public async Task GetCostWindowedAsync_BothWindows30Days_AdjacentImmediatelyPreceding()
    {
        // Fixture `now` is 2026-06-30. Current window
        // [2026-06-01, 2026-07-01) (30 days inclusive of today).
        // Previous window [2026-05-02, 2026-06-01) (30 days immediately
        // preceding). Seed one session in each window and one outside
        // both windows.
        var project = await CreateProjectAsync();

        // Current window session.
        await InsertSessionAsync(project.Id, Today.AddDays(-3).AddHours(8),
            costAmount: 0.50, costCurrency: "USD");
        // Previous window session.
        await InsertSessionAsync(project.Id, Today.AddDays(-40).AddHours(8),
            costAmount: 0.25, costCurrency: "USD");
        // Outside both windows (>60d ago) — ignored.
        await InsertSessionAsync(project.Id, Today.AddDays(-80).AddHours(8),
            costAmount: 9.99, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetCostWindowedAsync(project.Id);

        // Current window: 1 session, $0.50.
        Assert.Equal(0.50, result.CurrentWindow.Spend.Amount);
        Assert.Equal(1, result.CurrentWindow.Spend.SampleCount);
        Assert.Equal("USD", result.CurrentWindow.Spend.Currency);

        // Previous window: 1 session, $0.25.
        Assert.Equal(0.25, result.PreviousWindow.Spend.Amount);
        Assert.Equal(1, result.PreviousWindow.Spend.SampleCount);
        Assert.Equal("USD", result.PreviousWindow.Spend.Currency);
    }

    [Fact]
    public async Task GetCostWindowedAsync_PerIssueCostIsWindowSpendDividedByCompletedIssueCount()
    {
        var project = await CreateProjectAsync();
        // Current window: $1.50 spend, 3 completed issues → $0.50.
        await InsertSessionAsync(project.Id, Today.AddDays(-1).AddHours(8),
            costAmount: 1.50, costCurrency: "USD");
        await InsertDoneIssueAsync(project.Id, number: 1, title: "d1",
            completedAt: Today.AddDays(-1).AddHours(9));
        await InsertDoneIssueAsync(project.Id, number: 2, title: "d2",
            completedAt: Today.AddDays(-2).AddHours(9));
        await InsertDoneIssueAsync(project.Id, number: 3, title: "d3",
            completedAt: Today.AddDays(-3).AddHours(9));

        // Previous window: $0.40 spend, 4 completed issues → $0.10.
        await InsertSessionAsync(project.Id, Today.AddDays(-40).AddHours(8),
            costAmount: 0.40, costCurrency: "USD");
        for (var i = 0; i < 4; i++)
        {
            await InsertDoneIssueAsync(project.Id, number: 100 + i, $"prev{i}",
                completedAt: Today.AddDays(-31 - i).AddHours(9));
        }

        var service = ResolveReporter();
        var result = await service.GetCostWindowedAsync(project.Id);

        Assert.Equal(0.50, result.CurrentWindow.PerIssueCost.Amount);
        Assert.Equal(1, result.CurrentWindow.PerIssueCost.SampleCount);
        Assert.Equal("USD", result.CurrentWindow.PerIssueCost.Currency);

        Assert.Equal(0.10, result.PreviousWindow.PerIssueCost.Amount);
        Assert.Equal(1, result.PreviousWindow.PerIssueCost.SampleCount);
    }

    [Fact]
    public async Task GetCostWindowedAsync_NoSessionsInWindow_YieldsEmptySpend()
    {
        // Only insert a session without usage (no spend recorded). Both
        // windows evaluate to the empty result (amount null, sampleCount 0),
        // structurally distinguishable from a genuine 0.0.
        var project = await CreateProjectAsync();
        await InsertSessionWithoutUsageAsync(project.Id, Today.AddDays(-1).AddHours(8));
        await InsertSessionWithoutUsageAsync(project.Id, Today.AddDays(-40).AddHours(8));

        var service = ResolveReporter();
        var result = await service.GetCostWindowedAsync(project.Id);

        Assert.Null(result.CurrentWindow.Spend.Amount);
        Assert.Equal(0, result.CurrentWindow.Spend.SampleCount);
        Assert.Null(result.PreviousWindow.Spend.Amount);
        Assert.Equal(0, result.PreviousWindow.Spend.SampleCount);
    }

    [Fact]
    public async Task GetCostWindowedAsync_GenuineZeroSpend_DistinctFromEmpty()
    {
        // One session with genuine 0.0 cost in the current window. Spend
        // must be the real computed value (amount 0.0 with sampleCount > 0),
        // not the empty result (amount null, sampleCount 0).
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(8),
            costAmount: 0.0, costCurrency: "USD");
        await InsertDoneIssueAsync(project.Id, number: 1, title: "d1",
            completedAt: Today.AddDays(-2).AddHours(9));

        var service = ResolveReporter();
        var result = await service.GetCostWindowedAsync(project.Id);

        Assert.Equal(0.0, result.CurrentWindow.Spend.Amount);
        Assert.Equal(1, result.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.0, result.CurrentWindow.PerIssueCost.Amount);
        Assert.Equal(1, result.CurrentWindow.PerIssueCost.SampleCount);
    }

    [Fact]
    public async Task GetCostWindowedAsync_EmptinessIndependentPerMetricPerWindow()
    {
        // Current window: spend present, but no completed issues
        // → spend is real, per-issue cost is empty.
        // Previous window: completed issues present, but no sessions
        // with usage → spend is empty, per-issue cost is empty (no
        // numerator to divide).
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-3).AddHours(8),
            costAmount: 1.00, costCurrency: "USD");
        await InsertDoneIssueAsync(project.Id, number: 1, title: "prev-done",
            completedAt: Today.AddDays(-40).AddHours(9));

        var service = ResolveReporter();
        var result = await service.GetCostWindowedAsync(project.Id);

        // Current window: spend is real, per-issue cost is empty.
        Assert.Equal(1.00, result.CurrentWindow.Spend.Amount);
        Assert.Equal(1, result.CurrentWindow.Spend.SampleCount);
        Assert.Null(result.CurrentWindow.PerIssueCost.Amount);
        Assert.Equal(0, result.CurrentWindow.PerIssueCost.SampleCount);

        // Previous window: both metrics are empty (no spend).
        Assert.Null(result.PreviousWindow.Spend.Amount);
        Assert.Equal(0, result.PreviousWindow.Spend.SampleCount);
        Assert.Null(result.PreviousWindow.PerIssueCost.Amount);
        Assert.Equal(0, result.PreviousWindow.PerIssueCost.SampleCount);
    }

    [Fact]
    public async Task GetCostWindowedAsync_WindowsAreIndependent_AsymmetricEmptyResults()
    {
        // Reverse asymmetry: previous window has spend, current window is
        // empty. Each window's spend and per-issue cost is evaluated
        // independently — the previous window's populated spend and
        // per-issue cost must NOT bleed into the current window's
        // empty result.
        var project = await CreateProjectAsync();
        // Previous window only.
        await InsertSessionAsync(project.Id, Today.AddDays(-40).AddHours(8),
            costAmount: 0.50, costCurrency: "USD");
        await InsertDoneIssueAsync(project.Id, number: 1, title: "prev-done",
            completedAt: Today.AddDays(-40).AddHours(9));

        var service = ResolveReporter();
        var result = await service.GetCostWindowedAsync(project.Id);

        // Current window: empty on both metrics.
        Assert.Null(result.CurrentWindow.Spend.Amount);
        Assert.Equal(0, result.CurrentWindow.Spend.SampleCount);
        Assert.Null(result.CurrentWindow.PerIssueCost.Amount);
        Assert.Equal(0, result.CurrentWindow.PerIssueCost.SampleCount);

        // Previous window: spend and per-issue cost populated.
        Assert.Equal(0.50, result.PreviousWindow.Spend.Amount);
        Assert.Equal(1, result.PreviousWindow.Spend.SampleCount);
        Assert.Equal(0.50, result.PreviousWindow.PerIssueCost.Amount);
        Assert.Equal(1, result.PreviousWindow.PerIssueCost.SampleCount);
    }

    [Fact]
    public async Task GetCostWindowedAsync_WindowDays7_CurrentAndPreviousEachCover7Days()
    {
        var project = await CreateProjectAsync();

        await InsertSessionAsync(project.Id, Today.AddDays(-1).AddHours(8),
            costAmount: 0.30, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-8).AddHours(8),
            costAmount: 0.10, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-20).AddHours(8),
            costAmount: 9.99, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetCostWindowedAsync(project.Id, windowDays: 7);

        Assert.Equal(0.30, result.CurrentWindow.Spend.Amount);
        Assert.Equal(1, result.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.10, result.PreviousWindow.Spend.Amount);
        Assert.Equal(1, result.PreviousWindow.Spend.SampleCount);
    }

    [Fact]
    public async Task GetCostWindowedAsync_WindowDays90_CurrentAndPreviousEachCover90Days()
    {
        var project = await CreateProjectAsync();

        await InsertSessionAsync(project.Id, Today.AddDays(-60).AddHours(8),
            costAmount: 1.00, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-120).AddHours(8),
            costAmount: 0.50, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-200).AddHours(8),
            costAmount: 9.99, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetCostWindowedAsync(project.Id, windowDays: 90);

        Assert.Equal(1.00, result.CurrentWindow.Spend.Amount);
        Assert.Equal(1, result.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.50, result.PreviousWindow.Spend.Amount);
        Assert.Equal(1, result.PreviousWindow.Spend.SampleCount);
    }

    [Fact]
    public async Task GetCostWindowedAsync_OmittedWindowDays_Reproduces30DayWindow()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-10).AddHours(8),
            costAmount: 0.40, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-40).AddHours(8),
            costAmount: 0.20, costCurrency: "USD");

        var service = ResolveReporter();
        var omit = await service.GetCostWindowedAsync(project.Id);

        Assert.Equal(0.40, omit.CurrentWindow.Spend.Amount);
        Assert.Equal(1, omit.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.20, omit.PreviousWindow.Spend.Amount);
        Assert.Equal(1, omit.PreviousWindow.Spend.SampleCount);
    }

    [Fact]
    public async Task GetCostRollupAsync_NoSessions_YieldsEmptyMetrics()
    {
        var project = await CreateProjectAsync();

        var service = ResolveReporter();
        var result = await service.GetCostRollupAsync(project.Id);

        Assert.Null(result.TotalCost.Amount);
        Assert.Equal(0, result.TotalCost.SampleCount);
        Assert.Null(result.TodayCost.Amount);
        Assert.Equal(0, result.TodayCost.SampleCount);
    }

    [Fact]
    public async Task GetCostRollupAsync_TotalAndTodayPartitionedByCreatedAtDate()
    {
        var project = await CreateProjectAsync();
        // Total cost: $0.02 + $0.05 + $0.10 = $0.17 across 3 samples.
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(8),
            costAmount: 0.02, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-5).AddHours(8),
            costAmount: 0.05, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddHours(10),
            costAmount: 0.10, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetCostRollupAsync(project.Id);

        // Total cost covers all three samples.
        Assert.Equal(0.17, result.TotalCost.Amount);
        Assert.Equal(3, result.TotalCost.SampleCount);
        Assert.Equal("USD", result.TotalCost.Currency);

        // Today bucket contains only the in-today session.
        Assert.Equal(0.10, result.TodayCost.Amount);
        Assert.Equal(1, result.TodayCost.SampleCount);
        Assert.Equal("USD", result.TodayCost.Currency);
    }

    [Fact]
    public async Task GetCostRollupAsync_SessionsWithoutUsageAreSkipped()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-1).AddHours(8),
            costAmount: 0.05, costCurrency: "USD");
        await InsertSessionWithoutUsageAsync(project.Id, Today.AddDays(-2).AddHours(8));

        var service = ResolveReporter();
        var result = await service.GetCostRollupAsync(project.Id);

        Assert.Equal(0.05, result.TotalCost.Amount);
        Assert.Equal(1, result.TotalCost.SampleCount);
        Assert.Null(result.TodayCost.Amount);
        Assert.Equal(0, result.TodayCost.SampleCount);
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_NoSessions_ReturnsSevenEmptyBuckets()
    {
        var project = await CreateProjectAsync();

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id);

        Assert.Equal(7, result.Buckets.Count);
        Assert.Equal("day", result.BucketGranularity);
        Assert.NotEqual(default, result.RangeFrom);
        Assert.NotEqual(default, result.RangeTo);
        foreach (var bucket in result.Buckets)
        {
            Assert.Equal(0, bucket.InputTokens);
            Assert.Equal(0, bucket.OutputTokens);
            Assert.Equal(0, bucket.TotalTokens);
            Assert.Equal(0.0, bucket.CostAmount);
            Assert.Null(bucket.CostCurrency);
        }
        Assert.NotNull(result.CumulativeCostPerShip);
        Assert.Equal(7, result.CumulativeCostPerShip!.Count);
        foreach (var point in result.CumulativeCostPerShip)
        {
            Assert.Null(point.CumulativeCost);
            Assert.Null(point.Currency);
            Assert.Equal(0, point.CumulativeShippedCount);
            Assert.Null(point.CostPerShip);
        }
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_OmittedWindowDays_Reproduces7Day7BucketDailyWindow()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.05, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id);

        Assert.Equal("day", result.BucketGranularity);
        Assert.Equal(7, result.Buckets.Count);
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_WindowDays7_Daily7Buckets()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-1).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id, windowDays: 7);

        Assert.Equal("day", result.BucketGranularity);
        Assert.Equal(7, result.Buckets.Count);
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_WindowDays30_Daily30Buckets()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-5).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id, windowDays: 30);

        Assert.Equal("day", result.BucketGranularity);
        Assert.Equal(30, result.Buckets.Count);
        Assert.Equal(TimeSpan.FromDays(30), result.RangeTo - result.RangeFrom);
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_WindowDays90_Weekly13Buckets()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-20).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.05, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id, windowDays: 90);

        Assert.Equal("week", result.BucketGranularity);
        Assert.Equal(13, result.Buckets.Count);
        Assert.Equal(TimeSpan.FromDays(90), result.RangeTo - result.RangeFrom);
        for (var i = 0; i < result.Buckets.Count - 1; i++)
        {
            Assert.Equal(TimeSpan.FromDays(7), result.Buckets[i].BucketEnd - result.Buckets[i].BucketStart);
        }
        var last = result.Buckets[^1];
        Assert.Equal(result.RangeTo, last.BucketEnd);
        Assert.NotNull(result.CumulativeCostPerShip);
        Assert.Equal(result.Buckets.Count, result.CumulativeCostPerShip!.Count);
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_SessionLandsInExpectedBucket()
    {
        var project = await CreateProjectAsync();
        var bucketDay = Today.AddDays(-2);
        await InsertSessionAsync(project.Id, bucketDay.AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id);

        var bucket = result.Buckets.Single(b => b.BucketStart.Date == bucketDay.Date);
        Assert.Equal(100, bucket.InputTokens);
        Assert.Equal(50, bucket.OutputTokens);
        Assert.Equal(150, bucket.TotalTokens);
        Assert.Equal(0.02, bucket.CostAmount);
        Assert.Equal("USD", bucket.CostCurrency);
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_CumulativeCostPerShipAggregatesPreWindowAndBuckets()
    {
        var project = await CreateProjectAsync();
        // Pre-window spend: $1.00 across 1 sample (created 10 days ago).
        await InsertSessionAsync(project.Id, Today.AddDays(-10).AddHours(8),
            costAmount: 1.00, costCurrency: "USD");
        // In-window spend: $0.50 + $0.25 across 2 samples.
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(8),
            costAmount: 0.50, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-3).AddHours(8),
            costAmount: 0.25, costCurrency: "USD");
        // Two issues completed today (shipped in last bucket).
        await InsertDoneIssueAsync(project.Id, number: 1, title: "ship-1",
            completedAt: Today.AddHours(9));
        await InsertDoneIssueAsync(project.Id, number: 2, title: "ship-2",
            completedAt: Today.AddHours(10));

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id);

        Assert.NotNull(result.CumulativeCostPerShip);
        var points = result.CumulativeCostPerShip!;

        // Last bucket should carry both in-window sessions ($0.75) plus pre-window ($1.00) = $1.75.
        var lastPoint = points[^1];
        Assert.Equal(1.75, lastPoint.CumulativeCost);
        Assert.Equal("USD", lastPoint.Currency);
        // Two completed issues today → cost-per-ship 1.75 / 2 = 0.875.
        Assert.Equal(0.875, lastPoint.CostPerShip);
        Assert.Equal(2, lastPoint.CumulativeShippedCount);
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_CumulativeSeriesFollowsBucketGrid()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-3).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.05, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id, windowDays: 30);

        Assert.NotNull(result.CumulativeCostPerShip);
        Assert.Equal(result.Buckets.Count, result.CumulativeCostPerShip!.Count);
        Assert.Equal(30, result.CumulativeCostPerShip.Count);
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_BucketsSumSessionsWithUsage_SkipSessionsWithoutUsage()
    {
        var project = await CreateProjectAsync();
        var bucketDay = Today.AddDays(-2);
        await InsertSessionAsync(project.Id, bucketDay.AddHours(8),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");
        await InsertSessionAsync(project.Id, bucketDay.AddHours(12),
            inputTokens: 200, outputTokens: 80, totalTokens: 280, costAmount: 0.05, costCurrency: "USD",
            agentSessionId: "runtime-session-1");
        await InsertSessionWithoutUsageAsync(project.Id, Today.AddDays(-3).AddHours(10));

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id);

        var bucket = result.Buckets.Single(b => b.BucketStart.Date == bucketDay.Date);
        Assert.Equal(300, bucket.InputTokens);
        Assert.Equal(130, bucket.OutputTokens);
        Assert.Equal(430, bucket.TotalTokens);
        Assert.Equal(0.07, bucket.CostAmount);
        Assert.Equal("USD", bucket.CostCurrency);

        var usagelessDay = result.Buckets.Single(b => b.BucketStart.Date == Today.AddDays(-3));
        Assert.Equal(0, usagelessDay.InputTokens);
        Assert.Equal(0.0, usagelessDay.CostAmount);
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_SessionsOutsideRangeAreExcludedFromBuckets()
    {
        var project = await CreateProjectAsync();
        // Before the 7-day window.
        await InsertSessionAsync(project.Id, Today.AddDays(-10).AddHours(8),
            inputTokens: 999, outputTokens: 999, totalTokens: 1998, costAmount: 9.99, costCurrency: "USD");
        // At rangeTo (exclusive upper bound).
        await InsertSessionAsync(project.Id, Today.AddDays(1),
            inputTokens: 999, outputTokens: 999, totalTokens: 1998, costAmount: 9.99, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id);

        Assert.Equal(0, result.Buckets.Sum(b => b.InputTokens));
        Assert.Equal(0.0, result.Buckets.Sum(b => b.CostAmount));
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_CumulativeCostPrefixSums_CostPerShipNullWithoutShipped()
    {
        var project = await CreateProjectAsync();
        // Sessions land at bucket index 2 and 6 of the 7-day window.
        await InsertSessionAsync(project.Id, Today.AddDays(-4).AddHours(10),
            costAmount: 0.02, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddHours(10),
            costAmount: 0.05, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id);

        var points = result.CumulativeCostPerShip!;
        Assert.Equal(7, points.Count);

        // Before the first sample the cumulative cost is empty, not zero.
        for (var i = 0; i < 2; i++)
        {
            Assert.Null(points[i].CumulativeCost);
            Assert.Null(points[i].Currency);
            Assert.Equal(0, points[i].CumulativeShippedCount);
            Assert.Null(points[i].CostPerShip);
        }

        Assert.Equal(0.02, points[2].CumulativeCost);
        Assert.Equal("USD", points[2].Currency);
        for (var i = 3; i <= 5; i++)
        {
            Assert.Equal(0.02, points[i].CumulativeCost);
        }
        Assert.Equal(0.07, points[6].CumulativeCost);

        // Nothing shipped anywhere: cost-per-ship stays empty.
        Assert.All(points, point => Assert.Equal(0, point.CumulativeShippedCount));
        Assert.All(points, point => Assert.Null(point.CostPerShip));
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_CumulativeShippedPrefixSums_IncludePreWindowIssues()
    {
        var project = await CreateProjectAsync();
        // Shipped before the window: carried into every point.
        await InsertDoneIssueAsync(project.Id, number: 1, title: "pre",
            completedAt: Today.AddDays(-8).AddHours(12));
        // In-window spend at bucket index 0 so cost-per-ship is defined.
        await InsertSessionAsync(project.Id, Today.AddDays(-6).AddHours(10),
            costAmount: 0.60, costCurrency: "USD");
        // Shipped at bucket index 2 and 5.
        await InsertDoneIssueAsync(project.Id, number: 2, title: "mid",
            completedAt: Today.AddDays(-4).AddHours(12));
        await InsertDoneIssueAsync(project.Id, number: 3, title: "late",
            completedAt: Today.AddDays(-1).AddHours(8));

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id);

        var points = result.CumulativeCostPerShip!;
        Assert.Equal(1, points[0].CumulativeShippedCount);
        Assert.Equal(0.60, points[0].CumulativeCost);
        Assert.Equal(0.60, points[0].CostPerShip);
        Assert.Equal(1, points[1].CumulativeShippedCount);
        Assert.Equal(2, points[2].CumulativeShippedCount);
        Assert.Equal(0.30, points[2].CostPerShip!.Value, precision: 5);
        Assert.Equal(2, points[3].CumulativeShippedCount);
        Assert.Equal(2, points[4].CumulativeShippedCount);
        Assert.Equal(3, points[5].CumulativeShippedCount);
        Assert.Equal(0.20, points[5].CostPerShip!.Value, precision: 5);
        Assert.Equal(3, points[6].CumulativeShippedCount);
    }

    [Fact]
    public async Task GetUsageTimeseriesAsync_CumulativeZeroCost_DefinedWhenShippedOrSampled()
    {
        var project = await CreateProjectAsync();
        // Shipped (bucket index 2) before any usage sample exists.
        await InsertDoneIssueAsync(project.Id, number: 1, title: "early-ship",
            completedAt: Today.AddDays(-4).AddHours(12));
        // Genuine zero-cost session at bucket index 5.
        await InsertSessionAsync(project.Id, Today.AddDays(-1).AddHours(10),
            inputTokens: 0, outputTokens: 0, totalTokens: 0, costAmount: 0.0, costCurrency: "USD");

        var service = ResolveReporter();
        var result = await service.GetUsageTimeseriesAsync(project.Id);

        var points = result.CumulativeCostPerShip!;
        for (var i = 0; i < 2; i++)
        {
            Assert.Null(points[i].CumulativeCost);
            Assert.Null(points[i].CostPerShip);
        }

        // Shipped > 0 with no samples yet: cumulative cost is the defined
        // genuine zero without currency, cost-per-ship a genuine 0.0.
        for (var i = 2; i <= 4; i++)
        {
            Assert.Equal(0.0, points[i].CumulativeCost);
            Assert.Null(points[i].Currency);
            Assert.Equal(1, points[i].CumulativeShippedCount);
            Assert.Equal(0.0, points[i].CostPerShip);
        }

        // After the zero-cost sample the currency resolves; values stay 0.0.
        for (var i = 5; i <= 6; i++)
        {
            Assert.Equal(0.0, points[i].CumulativeCost);
            Assert.Equal("USD", points[i].Currency);
            Assert.Equal(0.0, points[i].CostPerShip);
        }
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var name = $"cost-querier-{Guid.NewGuid():N}";
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<ProjectDto>("/api/projects", name);
        await _fixture.Client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
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

    private async Task InsertDoneIssueAsync(
        string projectId,
        int number,
        string title,
        DateTime completedAt)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = IssueStatus.Done,
            CompletedAt = completedAt,
        };
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    private sealed record ProjectDto(string Id, string Name);
}
