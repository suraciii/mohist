using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Tests.Support;
using Mohist.Server.Sessions.Services;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Tests.Specs.Sessions;

/// <summary>
/// Querier-level specs for <see cref="AgentSessionQuerier.GetCostWindowedAsync"/>
/// (issue-322 T-004 / design D1, D2). Mirrors the
/// <see cref="IssueQuerierSpecs"/> adjacency/length/empty-result pattern,
/// driven by the integration fixture's pinned
/// <see cref="FakeTimeProvider"/> (2026-06-30 00:00 UTC). API-level coverage
/// lives in <c>AgentCostRollupApiSpecs</c>; these specs assert the
/// querier-level contract directly so windowing regressions are caught
/// without spinning a full route.
/// </summary>
[Collection("MohistIntegration")]
public class AgentSessionQuerierSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public AgentSessionQuerierSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private DateTime Today => _fixture.TimeProvider.GetUtcNow().UtcDateTime.Date;

    private AgentSessionQuerier ResolveQuerier()
    {
        var scope = _fixture.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentSessionQuerier>();
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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

        var service = ResolveQuerier();
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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

        var service = ResolveQuerier();
        var result = await service.GetCostWindowedAsync(project.Id);

        Assert.Equal(0.50, result.CurrentWindow.PerIssueCost.Amount);
        Assert.Equal(1, result.CurrentWindow.PerIssueCost.SampleCount);
        Assert.Equal("USD", result.CurrentWindow.PerIssueCost.Currency);

        Assert.Equal(0.10, result.PreviousWindow.PerIssueCost.Amount);
        Assert.Equal(1, result.PreviousWindow.PerIssueCost.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GetCostWindowedAsync_NoSessionsInWindow_YieldsEmptySpend()
    {
        // Only insert a session without usage (no spend recorded). Both
        // windows evaluate to the empty result (amount null, sampleCount 0),
        // structurally distinguishable from a genuine 0.0.
        var project = await CreateProjectAsync();
        await InsertSessionWithoutUsageAsync(project.Id, Today.AddDays(-1).AddHours(8));
        await InsertSessionWithoutUsageAsync(project.Id, Today.AddDays(-40).AddHours(8));

        var service = ResolveQuerier();
        var result = await service.GetCostWindowedAsync(project.Id);

        Assert.Null(result.CurrentWindow.Spend.Amount);
        Assert.Equal(0, result.CurrentWindow.Spend.SampleCount);
        Assert.Null(result.PreviousWindow.Spend.Amount);
        Assert.Equal(0, result.PreviousWindow.Spend.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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

        var service = ResolveQuerier();
        var result = await service.GetCostWindowedAsync(project.Id);

        Assert.Equal(0.0, result.CurrentWindow.Spend.Amount);
        Assert.Equal(1, result.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.0, result.CurrentWindow.PerIssueCost.Amount);
        Assert.Equal(1, result.CurrentWindow.PerIssueCost.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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

        var service = ResolveQuerier();
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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

        var service = ResolveQuerier();
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

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GetCostWindowedAsync_WindowDays7_CurrentAndPreviousEachCover7Days()
    {
        // windowDays=7 ⇒ current window is the last 7 days inclusive of
        // today; previous window is the immediately-preceding 7 days.
        // Sessions placed exactly on the boundary verify both windows
        // independently. This is the string-independent querier-level
        // assertion of D1 (route owns the wire parsing).
        var project = await CreateProjectAsync();

        // Current window [today-6d, today+1d).
        await InsertSessionAsync(project.Id, Today.AddDays(-1).AddHours(8),
            costAmount: 0.30, costCurrency: "USD");
        // Previous window [today-13d, today-6d).
        await InsertSessionAsync(project.Id, Today.AddDays(-8).AddHours(8),
            costAmount: 0.10, costCurrency: "USD");
        // Outside both windows.
        await InsertSessionAsync(project.Id, Today.AddDays(-20).AddHours(8),
            costAmount: 9.99, costCurrency: "USD");

        var service = ResolveQuerier();
        var result = await service.GetCostWindowedAsync(project.Id, windowDays: 7);

        Assert.Equal(0.30, result.CurrentWindow.Spend.Amount);
        Assert.Equal(1, result.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.10, result.PreviousWindow.Spend.Amount);
        Assert.Equal(1, result.PreviousWindow.Spend.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GetCostWindowedAsync_WindowDays90_CurrentAndPreviousEachCover90Days()
    {
        // windowDays=90 ⇒ current window is the last 90 days; previous
        // window is the immediately-preceding 90 days. Sessions placed
        // 60d and 120d ago verify the boundary: 60d is in current, 120d
        // is in previous (just inside).
        var project = await CreateProjectAsync();

        // Current window [today-89d, today+1d).
        await InsertSessionAsync(project.Id, Today.AddDays(-60).AddHours(8),
            costAmount: 1.00, costCurrency: "USD");
        // Previous window [today-179d, today-89d).
        await InsertSessionAsync(project.Id, Today.AddDays(-120).AddHours(8),
            costAmount: 0.50, costCurrency: "USD");
        // Outside both windows (>180d ago) — ignored.
        await InsertSessionAsync(project.Id, Today.AddDays(-200).AddHours(8),
            costAmount: 9.99, costCurrency: "USD");

        var service = ResolveQuerier();
        var result = await service.GetCostWindowedAsync(project.Id, windowDays: 90);

        Assert.Equal(1.00, result.CurrentWindow.Spend.Amount);
        Assert.Equal(1, result.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.50, result.PreviousWindow.Spend.Amount);
        Assert.Equal(1, result.PreviousWindow.Spend.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GetCostWindowedAsync_OmittedWindowDays_Reproduces30DayWindow()
    {
        // Omit-equality witness: omitted windowDays falls back to the
        // prior fixed 30-day window — the Dashboard back-compat default
        // (FactoryStatusHeadline consumes this endpoint without a range).
        var project = await CreateProjectAsync();
        // Inside the 30-day current window.
        await InsertSessionAsync(project.Id, Today.AddDays(-10).AddHours(8),
            costAmount: 0.40, costCurrency: "USD");
        // Outside the 30-day window but inside the 30-day previous window
        // [today-59d, today-29d).
        await InsertSessionAsync(project.Id, Today.AddDays(-40).AddHours(8),
            costAmount: 0.20, costCurrency: "USD");

        var service = ResolveQuerier();
        var omit = await service.GetCostWindowedAsync(project.Id);

        Assert.Equal(0.40, omit.CurrentWindow.Spend.Amount);
        Assert.Equal(1, omit.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.20, omit.PreviousWindow.Spend.Amount);
        Assert.Equal(1, omit.PreviousWindow.Spend.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GetUsageTimeseriesAsync_OmittedWindowDays_Reproduces7Day7BucketDailyWindow()
    {
        // Omit-equality witness: omitted windowDays falls back to the
        // prior fixed 7-day / 7-bucket daily window — the Dashboard
        // back-compat default.
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-2).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.05, costCurrency: "USD");

        var service = ResolveQuerier();
        var result = await service.GetUsageTimeseriesAsync(project.Id);

        Assert.Equal("day", result.BucketGranularity);
        Assert.Equal(7, result.Buckets.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GetUsageTimeseriesAsync_WindowDays7_Daily7Buckets()
    {
        // windowDays=7 ⇒ day(7). Recorded in design.md D5.
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-1).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var service = ResolveQuerier();
        var result = await service.GetUsageTimeseriesAsync(project.Id, windowDays: 7);

        Assert.Equal("day", result.BucketGranularity);
        Assert.Equal(7, result.Buckets.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GetUsageTimeseriesAsync_WindowDays30_Daily30Buckets()
    {
        // windowDays=30 ⇒ day(30). Recorded in design.md D5.
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-5).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02, costCurrency: "USD");

        var service = ResolveQuerier();
        var result = await service.GetUsageTimeseriesAsync(project.Id, windowDays: 30);

        Assert.Equal("day", result.BucketGranularity);
        Assert.Equal(30, result.Buckets.Count);
        // Window span is exactly 30 calendar days inclusive of today.
        Assert.Equal(TimeSpan.FromDays(30), result.RangeTo - result.RangeFrom);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GetUsageTimeseriesAsync_WindowDays90_Weekly13Buckets()
    {
        // windowDays=90 ⇒ week(ceil(90/7)=13). Recorded in design.md D5.
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-20).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.05, costCurrency: "USD");

        var service = ResolveQuerier();
        var result = await service.GetUsageTimeseriesAsync(project.Id, windowDays: 90);

        Assert.Equal("week", result.BucketGranularity);
        Assert.Equal(13, result.Buckets.Count);
        // Window span is exactly 90 calendar days inclusive of today.
        Assert.Equal(TimeSpan.FromDays(90), result.RangeTo - result.RangeFrom);
        // Each weekly bucket is 7 days long, except the trailing one
        // which clamps to rangeTo so the series covers the full span.
        for (var i = 0; i < result.Buckets.Count - 1; i++)
        {
            Assert.Equal(TimeSpan.FromDays(7), result.Buckets[i].BucketEnd - result.Buckets[i].BucketStart);
        }
        var last = result.Buckets[^1];
        Assert.Equal(result.RangeTo, last.BucketEnd);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task GetUsageTimeseriesAsync_CumulativeSeriesFollowsBucketGrid()
    {
        // Cumulative cost-per-ship sub-series follows the same bucket
        // grid (D5); it must have one entry per bucket regardless of
        // granularity. For 30d daily it has 30 entries.
        var project = await CreateProjectAsync();
        await InsertSessionAsync(project.Id, Today.AddDays(-3).AddHours(10),
            inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.05, costCurrency: "USD");

        var service = ResolveQuerier();
        var result = await service.GetUsageTimeseriesAsync(project.Id, windowDays: 30);

        Assert.NotNull(result.CumulativeCostPerShip);
        Assert.Equal(result.Buckets.Count, result.CumulativeCostPerShip!.Count);
        Assert.Equal(30, result.CumulativeCostPerShip.Count);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var name = $"cost-querier-{Guid.NewGuid():N}";
        var project = await _fixture.Client.PostDataAsync<ProjectDto>("/api/projects", new { name });
        await _fixture.Client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
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
}