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
/// Calculation specs for <see cref="AgentCostRollupQuerier"/>, the service
/// behind <c>GET /api/projects/{projectRef}/agent/cost</c>. Asserts the
/// all-time cumulative (total + today) cost, the project-scoped done-issue
/// count, the derived cost-per-ship, the windowed current/previous
/// spend + per-issue-cost, and the all-time invariance under range
/// changes without an HTTP round-trip. The route contract (404 unknown,
/// 400 unknown range, accepted ranges 200) stays in
/// <c>AgentCostRollupApiSpecs</c>.
/// </summary>
[Collection("MohistDb")]
public class AgentCostRollupQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public AgentCostRollupQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    private DateTime Today => _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.Date;

    private AgentCostRollupQuerier ResolveQuerier() =>
        _fixture.Services.GetRequiredService<AgentCostRollupQuerier>();

    [Fact]
    public async Task GetCostRollupAsync_ReturnsEnvelopeWithAllFourFields()
    {
        var projectId = await CreateProjectAsync();

        var result = await ResolveQuerier().GetCostRollupAsync(projectId);

        Assert.NotNull(result.TotalCost);
        Assert.NotNull(result.TodayCost);
        Assert.Equal(0, result.DoneIssuesCount);
        Assert.NotNull(result.CostPerShip);
    }

    [Fact]
    public async Task GetCostRollupAsync_DoneIssuesCountCountsOnlyDone()
    {
        var projectId = await CreateProjectAsync();
        for (var i = 1; i <= 7; i++)
            await InsertIssueWithStatusAsync(projectId, i, $"d{i}", IssueStatus.Done);
        for (var i = 8; i <= 10; i++)
            await InsertIssueWithStatusAsync(projectId, i, $"p{i}", IssueStatus.InProgress);
        await InsertIssueWithStatusAsync(projectId, 11, "open", IssueStatus.Backlog);

        var result = await ResolveQuerier().GetCostRollupAsync(projectId);

        Assert.Equal(7, result.DoneIssuesCount);
    }

    [Fact]
    public async Task GetCostRollupAsync_CostPerShipEqualsTotalCostOverDoneIssues()
    {
        var projectId = await CreateProjectAsync();
        await InsertSessionAsync(projectId, Today.AddDays(-1).AddHours(8),
            costAmount: 1.50, costCurrency: "USD");
        for (var i = 1; i <= 6; i++)
            await InsertIssueWithStatusAsync(projectId, i, $"d{i}", IssueStatus.Done);

        var result = await ResolveQuerier().GetCostRollupAsync(projectId);

        Assert.Equal(1.50, result.TotalCost.Amount);
        Assert.Equal(6, result.DoneIssuesCount);
        Assert.Equal(0.25, result.CostPerShip.Amount);
        Assert.Equal(1, result.CostPerShip.SampleCount);
        Assert.Equal("USD", result.CostPerShip.Currency);
    }

    [Fact]
    public async Task GetCostRollupAsync_FreeShippingIsRealZeroNotEmpty()
    {
        var projectId = await CreateProjectAsync();
        await InsertSessionAsync(projectId, Today.AddDays(-1).AddHours(8),
            costAmount: 0.0, costCurrency: "USD");
        for (var i = 1; i <= 5; i++)
            await InsertIssueWithStatusAsync(projectId, i, $"d{i}", IssueStatus.Done);

        var result = await ResolveQuerier().GetCostRollupAsync(projectId);

        Assert.Equal(0.0, result.TotalCost.Amount);
        Assert.Equal(1, result.TotalCost.SampleCount);
        Assert.Equal(5, result.DoneIssuesCount);
        Assert.NotNull(result.CostPerShip.Amount);
        Assert.Equal(0.0, result.CostPerShip.Amount);
        Assert.Equal(1, result.CostPerShip.SampleCount);
    }

    [Fact]
    public async Task GetCostRollupAsync_ZeroShippedIssuesYieldsUndefinedCostPerShip()
    {
        var projectId = await CreateProjectAsync();
        await InsertSessionAsync(projectId, Today.AddDays(-1).AddHours(8),
            costAmount: 1.20, costCurrency: "USD");

        var result = await ResolveQuerier().GetCostRollupAsync(projectId);

        Assert.Equal(1.20, result.TotalCost.Amount);
        Assert.Equal(0, result.DoneIssuesCount);
        Assert.Null(result.CostPerShip.Amount);
        Assert.Equal(0, result.CostPerShip.SampleCount);
    }

    [Fact]
    public async Task GetCostRollupAsync_Range90d_ScalesWindowedCurrentAndPreviousWindow()
    {
        var projectId = await CreateProjectAsync();
        await InsertSessionAsync(projectId, Today.AddDays(-60).AddHours(8),
            costAmount: 1.00, costCurrency: "USD");
        await InsertSessionAsync(projectId, Today.AddDays(-120).AddHours(8),
            costAmount: 0.50, costCurrency: "USD");
        await InsertSessionAsync(projectId, Today.AddDays(-200).AddHours(8),
            costAmount: 9.99, costCurrency: "USD");

        var result = await ResolveQuerier().GetCostRollupAsync(projectId, windowDays: 90);

        Assert.Equal(1.00, result.CurrentWindow.Spend.Amount);
        Assert.Equal(1, result.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.50, result.PreviousWindow.Spend.Amount);
        Assert.Equal(1, result.PreviousWindow.Spend.SampleCount);
    }

    [Fact]
    public async Task GetCostRollupAsync_Range_DoesNotAffectAllTimeFigures()
    {
        var projectId = await CreateProjectAsync();
        await InsertSessionAsync(projectId, Today.AddHours(8),
            costAmount: 0.10, costCurrency: "USD");
        await InsertSessionAsync(projectId, Today.AddDays(-2).AddHours(8),
            costAmount: 0.20, costCurrency: "USD");
        await InsertSessionAsync(projectId, Today.AddDays(-100).AddHours(8),
            costAmount: 0.99, costCurrency: "USD");
        for (var i = 1; i <= 4; i++)
            await InsertIssueWithStatusAsync(projectId, i, $"d{i}", IssueStatus.Done);

        var querier = ResolveQuerier();
        var omit = await querier.GetCostRollupAsync(projectId);
        var r90 = await querier.GetCostRollupAsync(projectId, windowDays: 90);
        var r7 = await querier.GetCostRollupAsync(projectId, windowDays: 7);

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

    [Fact]
    public async Task GetCostRollupAsync_OmittedRange_Reproduces30DayWindow()
    {
        var projectId = await CreateProjectAsync();
        await InsertSessionAsync(projectId, Today.AddDays(-10).AddHours(8),
            costAmount: 0.40, costCurrency: "USD");
        await InsertSessionAsync(projectId, Today.AddDays(-40).AddHours(8),
            costAmount: 0.20, costCurrency: "USD");
        await InsertSessionAsync(projectId, Today.AddDays(-80).AddHours(8),
            costAmount: 9.99, costCurrency: "USD");

        var result = await ResolveQuerier().GetCostRollupAsync(projectId);

        Assert.Equal(0.40, result.CurrentWindow.Spend.Amount);
        Assert.Equal(1, result.CurrentWindow.Spend.SampleCount);
        Assert.Equal(0.20, result.PreviousWindow.Spend.Amount);
        Assert.Equal(1, result.PreviousWindow.Spend.SampleCount);
    }

    private async Task<string> CreateProjectAsync()
    {
        var projectId = $"project-{Guid.NewGuid():N}";
        await using var db = await _fixture.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>().CreateDbContextAsync();
        db.Projects.Add(new Mohist.Server.Infrastructure.Data.Project.ProjectRow
        {
            Id = projectId,
            Name = $"cost-{Guid.NewGuid():N}",
            CreatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
            UpdatedAt = _fixture.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
        });
        await db.SaveChangesAsync();
        return projectId;
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

    private async Task InsertIssueWithStatusAsync(
        string projectId,
        int number,
        string title,
        IssueStatus status)
    {
        var issue = new DomainIssue
        {
            ProjectId = projectId,
            Number = number,
            Title = title,
            Status = status,
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
}
