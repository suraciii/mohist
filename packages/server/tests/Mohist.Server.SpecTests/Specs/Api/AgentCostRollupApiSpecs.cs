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
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public class AgentCostRollupApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentCostRollupApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    private DateTime Today => _fixture.TimeProvider.GetUtcNow().UtcDateTime.Date;

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_ReturnsEnvelopeWithAllFourFields()
    {
        var project = await CreateProjectAsync();

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.NotNull(response.TotalCost);
        Assert.NotNull(response.TodayCost);
        Assert.Equal(0, response.DoneIssuesCount);
        Assert.NotNull(response.CostPerShip);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_TotalCostSumsAcrossAllSessionsWithUsage()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(8),
            costAmount: 0.02, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-5).AddHours(8),
            costAmount: 0.05, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-9).AddHours(8),
            costAmount: 0.10, costCurrency: "USD");

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Equal(0.17, response.TotalCost.Amount);
        Assert.Equal(3, response.TotalCost.SampleCount);
        Assert.Equal("USD", response.TotalCost.Currency);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_SessionsWithoutUsageAreSkipped()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-1).AddHours(8),
            costAmount: 0.05, costCurrency: "USD");
        await InsertSessionWithoutUsageAsync(project.Id, Today.AddDays(-2).AddHours(8));
        await InsertSessionWithoutUsageAsync(project.Id, Today.AddDays(-3).AddHours(8));

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Equal(0.05, response.TotalCost.Amount);
        Assert.Equal(1, response.TotalCost.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_TodayCostBucketExcludesPriorDays()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddHours(10),
            costAmount: 0.04, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(10),
            costAmount: 0.10, costCurrency: "USD");

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Equal(0.14, response.TotalCost.Amount);
        Assert.Equal(2, response.TotalCost.SampleCount);
        Assert.Equal(0.04, response.TodayCost.Amount);
        Assert.Equal(1, response.TodayCost.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_TodayBucketBoundaryMatchesTimeseriesCurrentDay()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddHours(1),
            costAmount: 0.03, costCurrency: "USD");

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Equal(0.03, response.TodayCost.Amount);
        Assert.Equal(1, response.TodayCost.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_DoneIssuesCountCountsOnlyDone()
    {
        var project = await CreateProjectAsync();
        await InsertIssueWithStatusAsync(project.Id, number: 1, title: "d1", IssueStatus.Done);
        await InsertIssueWithStatusAsync(project.Id, number: 2, title: "d2", IssueStatus.Done);
        await InsertIssueWithStatusAsync(project.Id, number: 3, title: "d3", IssueStatus.Done);
        await InsertIssueWithStatusAsync(project.Id, number: 4, title: "d4", IssueStatus.Done);
        await InsertIssueWithStatusAsync(project.Id, number: 5, title: "d5", IssueStatus.Done);
        await InsertIssueWithStatusAsync(project.Id, number: 6, title: "d6", IssueStatus.Done);
        await InsertIssueWithStatusAsync(project.Id, number: 7, title: "d7", IssueStatus.Done);
        await InsertIssueWithStatusAsync(project.Id, number: 8, title: "p1", IssueStatus.InProgress);
        await InsertIssueWithStatusAsync(project.Id, number: 9, title: "p2", IssueStatus.InProgress);
        await InsertIssueWithStatusAsync(project.Id, number: 10, title: "p3", IssueStatus.InProgress);
        await InsertIssueWithStatusAsync(project.Id, number: 11, title: "open1", IssueStatus.Backlog);

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Equal(7, response.DoneIssuesCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_CostPerShipEqualsTotalCostOverDoneIssues()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-1).AddHours(8),
            costAmount: 1.50, costCurrency: "USD");
        for (var i = 0; i < 6; i++)
            await InsertIssueWithStatusAsync(project.Id, i + 1, $"d{i + 1}", IssueStatus.Done);

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Equal(1.50, response.TotalCost.Amount);
        Assert.Equal(6, response.DoneIssuesCount);
        Assert.Equal(0.25, response.CostPerShip.Amount);
        Assert.Equal(1, response.CostPerShip.SampleCount);
        Assert.Equal("USD", response.CostPerShip.Currency);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_FreeShippingIsRealZeroNotEmpty()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-1).AddHours(8),
            costAmount: 0.0, costCurrency: "USD");
        for (var i = 0; i < 5; i++)
            await InsertIssueWithStatusAsync(project.Id, i + 1, $"d{i + 1}", IssueStatus.Done);

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Equal(0.0, response.TotalCost.Amount);
        Assert.Equal(1, response.TotalCost.SampleCount);
        Assert.Equal(5, response.DoneIssuesCount);
        Assert.NotNull(response.CostPerShip.Amount);
        Assert.Equal(0.0, response.CostPerShip.Amount);
        Assert.Equal(1, response.CostPerShip.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_ZeroShippedIssuesYieldsUndefinedCostPerShip()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-1).AddHours(8),
            costAmount: 1.20, costCurrency: "USD");

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Equal(1.20, response.TotalCost.Amount);
        Assert.Equal(0, response.DoneIssuesCount);
        Assert.Null(response.CostPerShip.Amount);
        Assert.Equal(0, response.CostPerShip.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_ProjectWithoutUsageReturnsEmptySpendNotZero()
    {
        var project = await CreateProjectAsync();
        await InsertSessionWithoutUsageAsync(project.Id, Today.AddDays(-1).AddHours(8));

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Null(response.TotalCost.Amount);
        Assert.Equal(0, response.TotalCost.SampleCount);
        Assert.Null(response.TodayCost.Amount);
        Assert.Equal(0, response.TodayCost.SampleCount);
        Assert.Null(response.CostPerShip.Amount);
        Assert.Equal(0, response.DoneIssuesCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_EmptinessIsEvaluatedIndependentlyPerMetric()
    {
        var project = await CreateProjectAsync();
        var twoDaysAgo = Today.AddDays(-2).AddHours(8);
        await InsertSessionAsync(project.Id, twoDaysAgo, costAmount: 0.50, costCurrency: "USD");

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Equal(0.50, response.TotalCost.Amount);
        Assert.Equal(1, response.TotalCost.SampleCount);
        Assert.Null(response.TodayCost.Amount);
        Assert.Equal(0, response.TodayCost.SampleCount);
        Assert.Equal(0, response.DoneIssuesCount);
        Assert.Null(response.CostPerShip.Amount);
        Assert.Equal(0, response.CostPerShip.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_UnknownProjectReturns404()
    {
        using var response = await _client.GetAsync($"/api/projects/unknown-project-{Guid.NewGuid():N}/agent/cost");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_UsageTimeseriesEndpointRemainsAvailableAndUnchanged()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(8),
            costAmount: 0.02, costCurrency: "USD");

        var usageResponse = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.Equal("day", usageResponse.BucketGranularity);
        Assert.Equal(7, usageResponse.Buckets.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_WindowedSpendSumsCurrentAndPreviousWindowSessions()
    {
        var project = await CreateProjectAsync();
        // Current window: 2 sessions, $1.25 total.
        await InsertSessionAsync(project.Id, Today.AddDays(-3).AddHours(8),
            costAmount: 0.50, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-10).AddHours(8),
            costAmount: 0.75, costCurrency: "USD");
        // Previous window: 2 sessions, $0.75 total.
        await InsertSessionAsync(project.Id, Today.AddDays(-35).AddHours(8),
            costAmount: 0.25, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-50).AddHours(8),
            costAmount: 0.50, costCurrency: "USD");
        // Outside both windows (>60d ago): ignored.
        await InsertSessionAsync(project.Id, Today.AddDays(-80).AddHours(8),
            costAmount: 9.99, costCurrency: "USD");

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Equal(1.25, response.CurrentWindow.Spend.Amount);
        Assert.Equal(2, response.CurrentWindow.Spend.SampleCount);
        Assert.Equal("USD", response.CurrentWindow.Spend.Currency);

        Assert.Equal(0.75, response.PreviousWindow.Spend.Amount);
        Assert.Equal(2, response.PreviousWindow.Spend.SampleCount);
        Assert.Equal("USD", response.PreviousWindow.Spend.Currency);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_WindowedPerIssueCostIsWindowSpendDividedByCompletedIssues()
    {
        var project = await CreateProjectAsync();
        // Current window: $1.82 spend, 2 completed issues → $0.91.
        await InsertSessionAsync(project.Id, Today.AddDays(-3).AddHours(8),
            costAmount: 1.00, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-10).AddHours(8),
            costAmount: 0.82, costCurrency: "USD");
        await InsertDoneIssueAsync(project.Id, number: 1, title: "d1",
            completedAt: Today.AddDays(-2).AddHours(9));
        await InsertDoneIssueAsync(project.Id, number: 2, title: "d2",
            completedAt: Today.AddDays(-7).AddHours(9));

        // Previous window: $0.50 spend, 5 completed issues → $0.10.
        await InsertSessionAsync(project.Id, Today.AddDays(-35).AddHours(8),
            costAmount: 0.50, costCurrency: "USD");
        for (var i = 0; i < 5; i++)
        {
            await InsertDoneIssueAsync(project.Id, number: 100 + i, $"prev{i}",
                completedAt: Today.AddDays(-31 - i).AddHours(9));
        }

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Equal(1.82 / 2.0, response.CurrentWindow.PerIssueCost.Amount!.Value, precision: 5);
        Assert.Equal(1, response.CurrentWindow.PerIssueCost.SampleCount);

        Assert.Equal(0.50 / 5.0, response.PreviousWindow.PerIssueCost.Amount!.Value, precision: 5);
        Assert.Equal(1, response.PreviousWindow.PerIssueCost.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_WindowedBothWindowsReturned_DeriveDeltaInSingleRead()
    {
        var project = await CreateProjectAsync();
        // Current window: $2.40 spend, 3 completed issues → $0.80.
        await InsertSessionAsync(project.Id, Today.AddDays(-1).AddHours(8),
            costAmount: 2.40, costCurrency: "USD");
        await InsertDoneIssueAsync(project.Id, number: 1, title: "d1",
            completedAt: Today.AddDays(-1).AddHours(9));
        await InsertDoneIssueAsync(project.Id, number: 2, title: "d2",
            completedAt: Today.AddDays(-2).AddHours(9));
        await InsertDoneIssueAsync(project.Id, number: 3, title: "d3",
            completedAt: Today.AddDays(-3).AddHours(9));

        // Previous window: $1.20 spend, 2 completed issues → $0.60.
        await InsertSessionAsync(project.Id, Today.AddDays(-40).AddHours(8),
            costAmount: 1.20, costCurrency: "USD");
        await InsertDoneIssueAsync(project.Id, number: 4, title: "d4",
            completedAt: Today.AddDays(-40).AddHours(9));
        await InsertDoneIssueAsync(project.Id, number: 5, title: "d5",
            completedAt: Today.AddDays(-45).AddHours(9));

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        // A consumer can derive the spend delta and per-issue-cost delta
        // from the two windows in a single read.
        var spendDelta = response.CurrentWindow.Spend.Amount!.Value
            - response.PreviousWindow.Spend.Amount!.Value;
        var perIssueDelta = response.CurrentWindow.PerIssueCost.Amount!.Value
            - response.PreviousWindow.PerIssueCost.Amount!.Value;
        Assert.Equal(1.20, spendDelta, precision: 5);
        Assert.Equal(0.20, perIssueDelta, precision: 5);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_WindowedEmptinessIsEvaluatedIndependentlyPerMetricPerWindow()
    {
        var project = await CreateProjectAsync();
        // Current window: spend ($1.00), but no completed issues
        // → spend is real, per-issue cost is the empty result.
        await InsertSessionAsync(project.Id, Today.AddDays(-3).AddHours(8),
            costAmount: 1.00, costCurrency: "USD");
        // Previous window: completed issues, but no sessions with usage
        // → per-issue cost is empty (no numerator), spend is empty.
        await InsertDoneIssueAsync(project.Id, number: 1, title: "d1",
            completedAt: Today.AddDays(-35).AddHours(9));

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        // Current: spend is real, per-issue cost is empty.
        Assert.Equal(1.00, response.CurrentWindow.Spend.Amount);
        Assert.Equal(1, response.CurrentWindow.Spend.SampleCount);
        Assert.Null(response.CurrentWindow.PerIssueCost.Amount);
        Assert.Equal(0, response.CurrentWindow.PerIssueCost.SampleCount);

        // Previous: spend is empty, per-issue cost is empty.
        Assert.Null(response.PreviousWindow.Spend.Amount);
        Assert.Equal(0, response.PreviousWindow.Spend.SampleCount);
        Assert.Null(response.PreviousWindow.PerIssueCost.Amount);
        Assert.Equal(0, response.PreviousWindow.PerIssueCost.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_WindowedSpendNoSessionsYieldsEmptyNotGenuineZero()
    {
        var project = await CreateProjectAsync();
        // No sessions with usage in either window.
        await InsertSessionWithoutUsageAsync(project.Id, Today.AddDays(-1).AddHours(8));
        await InsertSessionWithoutUsageAsync(project.Id, Today.AddDays(-40).AddHours(8));

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        // Both windows: empty spend (amount null, sampleCount 0),
        // distinguishable from a genuine zero.
        Assert.Null(response.CurrentWindow.Spend.Amount);
        Assert.Equal(0, response.CurrentWindow.Spend.SampleCount);
        Assert.Null(response.PreviousWindow.Spend.Amount);
        Assert.Equal(0, response.PreviousWindow.Spend.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_WindowedGenuineZeroSpendIsDistinctFromEmpty()
    {
        var project = await CreateProjectAsync();
        // One session with genuine zero cost in current window,
        // matched by one completed issue → per-issue cost is 0.0.
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(8),
            costAmount: 0.0, costCurrency: "USD");
        await InsertDoneIssueAsync(project.Id, number: 1, title: "d1",
            completedAt: Today.AddDays(-2).AddHours(9));

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        // Genuine-zero spend: amount 0.0 with sampleCount > 0,
        // structurally distinguishable from the empty result
        // (amount null, sampleCount 0).
        Assert.Equal(0.0, response.CurrentWindow.Spend.Amount);
        Assert.Equal(1, response.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.0, response.CurrentWindow.PerIssueCost.Amount);
        Assert.Equal(1, response.CurrentWindow.PerIssueCost.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_WindowedAdditivePreservation_CumulativeRollupAndTimeseriesUnchanged()
    {
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(8),
            costAmount: 1.50, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-40).AddHours(8),
            costAmount: 0.50, costCurrency: "USD");
        for (var i = 0; i < 6; i++)
            await InsertIssueWithStatusAsync(project.Id, i + 1, $"d{i + 1}", IssueStatus.Done);

        var costResponse = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        // Existing cumulative rollup preserved byte-for-byte.
        Assert.Equal(2.00, costResponse.TotalCost.Amount);
        Assert.Equal(2, costResponse.TotalCost.SampleCount);
        Assert.Equal(6, costResponse.DoneIssuesCount);
        Assert.Equal(2.00 / 6.0, costResponse.CostPerShip.Amount!.Value, precision: 5);

        // Existing 7-day usage timeseries endpoint unaffected.
        var usageResponse = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");
        Assert.Equal("day", usageResponse.BucketGranularity);
        Assert.Equal(7, usageResponse.Buckets.Count);

        // New current-window figures added without disturbing the above.
        Assert.Equal(1.50, costResponse.CurrentWindow.Spend.Amount);
        Assert.Equal(1, costResponse.CurrentWindow.Spend.SampleCount);
        // Previous-window session was 40d ago → outside 30d current window,
        // but inside the 30d previous window [now-60d, now-30d).
        Assert.Equal(0.50, costResponse.PreviousWindow.Spend.Amount);
        Assert.Equal(1, costResponse.PreviousWindow.Spend.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_Range90d_ScalesWindowedCurrentAndPreviousWindow()
    {
        // `range=90d` re-bases the windowed current/previous spend and
        // per-issue-cost to a 90-day current window and a 90-day
        // immediately-preceding previous window. Sessions placed 60d ago
        // (in current) and 120d ago (in previous) verify the boundary;
        // the 5d-ago session stays inside both windows (current under
        // both 30d and 90d).
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-60).AddHours(8),
            costAmount: 1.00, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-120).AddHours(8),
            costAmount: 0.50, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-200).AddHours(8),
            costAmount: 9.99, costCurrency: "USD");

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost?range=90d");

        Assert.Equal(1.00, response.CurrentWindow.Spend.Amount);
        Assert.Equal(1, response.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.50, response.PreviousWindow.Spend.Amount);
        Assert.Equal(1, response.PreviousWindow.Spend.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_Range7d_ScalesWindowedCurrentAndPreviousWindow()
    {
        // `range=7d` re-bases the windowed current/previous window to
        // 7 days each. Sessions placed 3d ago (current) and 10d ago
        // (previous) verify the boundary.
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-3).AddHours(8),
            costAmount: 0.30, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-10).AddHours(8),
            costAmount: 0.10, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-20).AddHours(8),
            costAmount: 9.99, costCurrency: "USD");

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost?range=7d");

        Assert.Equal(0.30, response.CurrentWindow.Spend.Amount);
        Assert.Equal(1, response.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.10, response.PreviousWindow.Spend.Amount);
        Assert.Equal(1, response.PreviousWindow.Spend.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_Range_DoesNotAffectAllTimeFigures()
    {
        // All-time `totalCost`, `todayCost`, all-time `costPerShip`, and
        // `doneIssuesCount` are byte-identical with and without a range.
        // Only the windowed current/previous figures re-base.
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddHours(8),
            costAmount: 0.10, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(8),
            costAmount: 0.20, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-100).AddHours(8),
            costAmount: 0.99, costCurrency: "USD");
        for (var i = 0; i < 4; i++)
            await InsertIssueWithStatusAsync(project.Id, i + 1, $"d{i + 1}", IssueStatus.Done);

        var omit = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");
        var r90 = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost?range=90d");
        var r7 = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost?range=7d");

        // All-time figures are byte-identical regardless of range.
        Assert.Equal(omit.TotalCost.Amount, r90.TotalCost.Amount);
        Assert.Equal(omit.TotalCost.SampleCount, r90.TotalCost.SampleCount);
        Assert.Equal(omit.TotalCost.Currency, r90.TotalCost.Currency);
        Assert.Equal(omit.TodayCost.Amount, r90.TodayCost.Amount);
        Assert.Equal(omit.TodayCost.SampleCount, r90.TodayCost.SampleCount);
        Assert.Equal(omit.DoneIssuesCount, r90.DoneIssuesCount);
        Assert.Equal(omit.CostPerShip.Amount, r90.CostPerShip.Amount);

        Assert.Equal(omit.TotalCost.Amount, r7.TotalCost.Amount);
        Assert.Equal(omit.TotalCost.SampleCount, r7.TotalCost.SampleCount);
        Assert.Equal(omit.TodayCost.Amount, r7.TodayCost.Amount);
        Assert.Equal(omit.TodayCost.SampleCount, r7.TodayCost.SampleCount);
        Assert.Equal(omit.DoneIssuesCount, r7.DoneIssuesCount);
        Assert.Equal(omit.CostPerShip.Amount, r7.CostPerShip.Amount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_OmittedRange_Reproduces30DayWindow()
    {
        // Omit-equality witness: omitting `range` reproduces today's
        // fixed 30-day windowed figures. A session at 10d ago (in
        // current 30d window) is included; a session at 40d ago is in
        // the previous 30d window; a session at 80d ago is ignored.
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-10).AddHours(8),
            costAmount: 0.40, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-40).AddHours(8),
            costAmount: 0.20, costCurrency: "USD");
        await InsertSessionAsync(project.Id, Today.AddDays(-80).AddHours(8),
            costAmount: 9.99, costCurrency: "USD");

        var response = await _client.GetDataAsync<AgentCostRollupResponseDto>(
            $"/api/projects/{project.Id}/agent/cost");

        Assert.Equal(0.40, response.CurrentWindow.Spend.Amount);
        Assert.Equal(1, response.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.20, response.PreviousWindow.Spend.Amount);
        Assert.Equal(1, response.PreviousWindow.Spend.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
    [Fact]
    public async Task GetCost_UnknownRange_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync();
        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/agent/cost?range=bad");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Api)]
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

    private async Task InsertIssueWithStatusAsync(
        string projectId,
        int number,
        string title,
        IssueStatus status)
    {
        var issue = new DomainIssue
        {
            Id = $"issue_{Guid.NewGuid():N}",
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = status,
        };
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
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
            Id = $"issue_{Guid.NewGuid():N}",
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = IssueStatus.Done,
            CompletedAt = completedAt,
        };
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();
    }

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);

    private sealed record AgentCostMetricResponseDto(double? Amount, string? Currency, int SampleCount);

    private sealed record AgentCostWindowedFigureResponseDto(
        AgentCostMetricResponseDto Spend,
        AgentCostMetricResponseDto PerIssueCost);

    private sealed record AgentCostRollupResponseDto(
        AgentCostMetricResponseDto TotalCost,
        AgentCostMetricResponseDto TodayCost,
        int DoneIssuesCount,
        AgentCostMetricResponseDto CostPerShip,
        AgentCostWindowedFigureResponseDto CurrentWindow,
        AgentCostWindowedFigureResponseDto PreviousWindow);

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
