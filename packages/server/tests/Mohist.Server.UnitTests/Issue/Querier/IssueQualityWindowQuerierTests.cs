using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Xunit;
using static Mohist.Server.UnitTests.Issue.Querier.IssueMetricsQuerierTestData;

namespace Mohist.Server.UnitTests.Issue.Querier;

[Collection("MohistDb")]
public class IssueQualityWindowQuerierTests
{
    private readonly MohistDbFixture _fixture;

    public IssueQualityWindowQuerierTests(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetQualityAsync_ReturnsCurrentAndPreviousWindowRates()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-previous-{Guid.NewGuid():N}", Name = "Quality Previous Window" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var current = SeedIssue(db, project, $"{project.Id}-current", workflowRunId: "wr-quality-current", status: IssueStatus.Done);
        var previous = SeedIssue(db, project, $"{project.Id}-previous", workflowRunId: "wr-quality-previous", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, current.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr-quality-current");
        SeedEvent(db, previous.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-40), "wr-quality-previous");
        await SeedWorkflowRunAsync(db, "wr-quality-current", QualityRunState("wr-quality-current", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
        ]));
        await SeedWorkflowRunAsync(db, "wr-quality-previous", QualityRunState("wr-quality-previous", [
            ("plan", [("plan-repair", "Plan repair", 1)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(1.0, result.Window.FirstTimeRightRate);
        Assert.Equal(1, result.PreviousWindow.SampleCount);
        Assert.Equal(0.0, result.PreviousWindow.FirstTimeRightRate);
    }

    [Fact]
    public async Task GetQualityAsync_EmptyPreviousWindow_ReturnsNullRate()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-previous-empty-{Guid.NewGuid():N}", Name = "Quality Empty Previous Window" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var issue = SeedIssue(db, project, $"{project.Id}-current", workflowRunId: "wr-quality-current-only", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr-quality-current-only");
        await SeedWorkflowRunAsync(db, "wr-quality-current-only", QualityRunState("wr-quality-current-only", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(0, result.PreviousWindow.SampleCount);
        Assert.Null(result.PreviousWindow.FirstTimeRightRate);
    }

    [Theory]
    [InlineData(7, 0)]
    [InlineData(30, 1)]
    [InlineData(90, 1)]
    public async Task GetQualityAsync_UsesRequestedWindowLength(int windowDays, int expectedSampleCount)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-range-{windowDays}-{Guid.NewGuid():N}", Name = "Quality Range" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var issue = SeedIssue(db, project, $"{project.Id}-issue", workflowRunId: "wr-quality-range", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-10), "wr-quality-range");
        await SeedWorkflowRunAsync(db, "wr-quality-range", QualityRunState("wr-quality-range", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now, windowDays);

        Assert.Equal(expectedSampleCount, result.Window.SampleCount);
        Assert.Equal(TimeSpan.FromDays(windowDays), result.Window.To - result.Window.From);
        Assert.Equal(windowDays, result.Trend.Points.Count);
    }
}
