using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Services.Sessions;
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