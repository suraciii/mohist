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

namespace Mohist.Server.Tests.Specs.Api;

[Collection("MohistIntegration")]
public class AgentCostRollupApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;
    private readonly HttpClient _client;

    public AgentCostRollupApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

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
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddDays(-2).AddHours(8),
            costAmount: 0.02, costCurrency: "USD");
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddDays(-5).AddHours(8),
            costAmount: 0.05, costCurrency: "USD");
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddDays(-9).AddHours(8),
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
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddDays(-1).AddHours(8),
            costAmount: 0.05, costCurrency: "USD");
        await InsertSessionWithoutUsageAsync(project.Id, DateTime.UtcNow.Date.AddDays(-2).AddHours(8));
        await InsertSessionWithoutUsageAsync(project.Id, DateTime.UtcNow.Date.AddDays(-3).AddHours(8));

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
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddHours(10),
            costAmount: 0.04, costCurrency: "USD");
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddDays(-2).AddHours(10),
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
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddHours(1),
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
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddDays(-1).AddHours(8),
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
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddDays(-1).AddHours(8),
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
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddDays(-1).AddHours(8),
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
        await InsertSessionWithoutUsageAsync(project.Id, DateTime.UtcNow.Date.AddDays(-1).AddHours(8));

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
        var twoDaysAgo = DateTime.UtcNow.Date.AddDays(-2).AddHours(8);
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
        await InsertSessionAsync(project.Id, DateTime.UtcNow.Date.AddDays(-2).AddHours(8),
            costAmount: 0.02, costCurrency: "USD");

        var usageResponse = await _client.GetDataAsync<UsageTimeseriesResponseDto>(
            $"/api/projects/{project.Id}/agent/usage");

        Assert.Equal("day", usageResponse.BucketGranularity);
        Assert.Equal(7, usageResponse.Buckets.Count);
    }

    private async Task<ProjectDto> CreateProjectAsync()
    {
        var name = $"cost-{Guid.NewGuid():N}";
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

    private sealed record ProjectDto(string Id, string Name, string Path, string BaseBranch);

    private sealed record AgentCostMetricResponseDto(double? Amount, string? Currency, int SampleCount);

    private sealed record AgentCostRollupResponseDto(
        AgentCostMetricResponseDto TotalCost,
        AgentCostMetricResponseDto TodayCost,
        int DoneIssuesCount,
        AgentCostMetricResponseDto CostPerShip);

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