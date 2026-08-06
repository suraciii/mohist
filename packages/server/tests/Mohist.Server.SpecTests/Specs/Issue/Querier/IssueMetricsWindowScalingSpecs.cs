using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

using static Mohist.Server.SpecTests.Specs.Issue.Querier.IssueMetricsTestSupport;

[Collection("MohistDb")]
public class IssueMetricsWindowScalingSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueMetricsWindowScalingSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(7)]
    [InlineData(90)]
    public async Task GetCompletionBucketsAsync_DayBucketing_WindowDaysScalesWindowAndPreviousTotals(int windowDays)
    {
        // `windowDays` drives the trailing day-window length and the
        // immediately-preceding window of the same length. now = 2026-06-30
        // 00:00 UTC: current window [(today-(W-1)) 00:00, tomorrow 00:00),
        // previous window [(today-(2W-1)) 00:00, current window from).
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-range-day-{Guid.NewGuid():N}", Name = "Range Day Project" };
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

        var current = SeedIssue(db, project, "issue_range_day_current");
        var previous = SeedIssue(db, project, "issue_range_day_previous");
        var outside = SeedIssue(db, project, "issue_range_day_outside");
        await db.SaveChangesAsync();

        SeedEvent(db, current, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-windowDays / 2.0));
        SeedEvent(db, previous, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1.5 * windowDays));
        SeedEvent(db, outside, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2.5 * windowDays));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueMetricsQuerier.CompletionBucket.Day, now, windowDays);

        Assert.Equal(windowDays, result.Buckets.Count);
        Assert.Equal(TimeSpan.FromDays(windowDays), result.WindowTo - result.WindowFrom);
        // Exactly one terminal event per window; the third event (before the
        // previous window) must not leak into either total.
        Assert.Equal(1, result.CurrentTotal.SampleCount);
        Assert.Equal(1, result.CurrentTotal.Completed);
        Assert.Equal(1, result.PreviousTotal.SampleCount);
        Assert.Equal(1, result.PreviousTotal.Completed);
    }

    [Theory]
    [InlineData(7, 1)]
    [InlineData(90, 13)]
    public async Task GetCompletionBucketsAsync_WeekBucketing_WindowDaysDerivesWeekCountRoundedUp(int windowDays, int expectedWeeks)
    {
        // Week bucket: ceil(windowDays / 7) trailing ISO weeks, Mon-anchored.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-range-week-{Guid.NewGuid():N}", Name = "Range Week Project" };
        SeedIssue(db, project, "issue_range_week_1");
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueMetricsQuerier.CompletionBucket.Week, now, windowDays);

        Assert.Equal(expectedWeeks, result.Buckets.Count);
        Assert.Equal(TimeSpan.FromDays(7 * expectedWeeks), result.WindowTo - result.WindowFrom);
    }

    [Fact]
    public async Task GetApprovalWaitAsync_WindowDays_ScalesTrailingWindow()
    {
        // `windowDays` replaces the fixed 7-day default: a response at
        // now-10d is outside the default window but inside the 30-day one.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-range-{Guid.NewGuid():N}", Name = "Approval Range Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        SeedIssue(db, project, "issue_aw_range_1", workflowRunId: "wr_aw_range_1");
        await db.SaveChangesAsync();
        await SeedWorkflowRunAsync(db, "wr_aw_range_1", ApprovalRunState("wr_aw_range_1", now.AddDays(-10), TimeSpan.FromHours(2)));

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now, windowDays: 30);

        Assert.Equal(1, result.SampleCount);
        Assert.Equal(now.AddDays(-30), result.Window.From);
        Assert.Equal(now, result.Window.To);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_WindowDays_ScalesCurrentAndPreviousWindow()
    {
        // `windowDays` drives both the current trailing window and the
        // same-length preceding window: a completion at now-60d is inside
        // the 90-day window (out of the default 30-day one) and one at
        // now-120d contributes only the previous-window average.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dt-range-{Guid.NewGuid():N}", Name = "Delivery Time Range" };
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

        var current = SeedDeliveredIssue(
            db, project, "issue_dt_range_current",
            createdAt: new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: now.AddDays(-60).UtcDateTime);
        SeedEvent(db, current, EventCatalog.ReverseDns.IssueWorkStarted, now.AddDays(-62));

        var previous = SeedDeliveredIssue(
            db, project, "issue_dt_range_previous",
            createdAt: new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: now.AddDays(-120).UtcDateTime);
        SeedEvent(db, previous, EventCatalog.ReverseDns.IssueWorkStarted, now.AddDays(-123));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetDeliveryTimesAsync(project.Id, now, windowDays: 90);

        var point = Assert.Single(result.Points);
        Assert.Equal(current.Number, point.IssueNumber);
        // Previous window's only delivered cycle is exactly 3 days
        // (work-start 3 days before its completion).
        Assert.Equal(3.0, result.PreviousAverageCycleDays!.Value, precision: 5);

        // Omitting the range reproduces the 30-day default: the now-60d
        // completion falls outside it.
        var omit = await service.GetDeliveryTimesAsync(project.Id, now);
        Assert.Empty(omit.Points);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(90)]
    public async Task GetQualityAsync_WindowDays_ScalesPrimaryPreviousAndTrend(int windowDays)
    {
        // `windowDays` drives the primary window, the same-length previous
        // window, and the per-day trend: a shipment at now-W/2 lands in the
        // primary window and one at now-3W/2 in the previous window; the
        // trend carries exactly `windowDays` daily buckets over the primary
        // window.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-range-{Guid.NewGuid():N}", Name = "Quality Range Project" };
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

        var current = SeedIssue(db, project, "issue_quality_range_current", workflowRunId: "wr_quality_range_current", status: IssueStatus.Done);
        var previous = SeedIssue(db, project, "issue_quality_range_previous", workflowRunId: "wr_quality_range_previous", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, current, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-windowDays / 2.0), "wr_quality_range_current");
        await SeedWorkflowRunAsync(db, "wr_quality_range_current", QualityRunState("wr_quality_range_current", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
        ]));
        SeedEvent(db, previous, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1.5 * windowDays), "wr_quality_range_previous");
        await SeedWorkflowRunAsync(db, "wr_quality_range_previous", QualityRunState("wr_quality_range_previous", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now, windowDays);

        Assert.Equal(TimeSpan.FromDays(windowDays), result.Window.To - result.Window.From);
        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(1.0, result.Window.FirstTimeRightRate);
        Assert.Equal(1, result.PreviousWindow.SampleCount);
        Assert.Equal(1.0, result.PreviousWindow.FirstTimeRightRate);
        Assert.Equal(windowDays, result.Trend.Points.Count);
        Assert.Equal(result.Window.From, result.Trend.WindowFrom);
        Assert.Equal(result.Window.To, result.Trend.WindowTo);
    }

    [Fact]
    public async Task GetStageDurationsAsync_WindowDays_ScalesTrailingWindow()
    {
        // `windowDays` extends the fixed 30-day default: a completion at
        // now-60d is inside the 90-day window (out of the default one) and
        // one at now-100d stays outside.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-range-{Guid.NewGuid():N}", Name = "Stage Duration Range" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var inside = SeedDeliveredIssue(
            db, project, "issue_sd_range_inside",
            createdAt: new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: now.AddDays(-60).UtcDateTime,
            workflowRunId: "wr_sd_range_inside");
        SeedEvent(db, inside, EventCatalog.ReverseDns.IssueWorkStarted, now.AddDays(-60).AddHours(-1), workflowRunId: "wr_sd_range_inside");
        await db.SaveChangesAsync();
        await SeedWorkflowRunAsync(db, "wr_sd_range_inside", ApprovalRunState("wr_sd_range_inside", now.AddDays(-60).AddHours(-2), TimeSpan.Zero));
        SeedWorkflowRunEvent(db, "wr_sd_range_inside", 1, EventCatalog.ReverseDns.StageStarted, now.AddDays(-60).AddHours(-1), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_range_inside", 2, EventCatalog.ReverseDns.StageCompleted, now.AddDays(-60), new { stage = "plan" });

        var outside = SeedDeliveredIssue(
            db, project, "issue_sd_range_outside",
            createdAt: new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: now.AddDays(-100).UtcDateTime,
            workflowRunId: "wr_sd_range_outside");
        SeedEvent(db, outside, EventCatalog.ReverseDns.IssueWorkStarted, now.AddDays(-100).AddHours(-1), workflowRunId: "wr_sd_range_outside");
        await db.SaveChangesAsync();
        await SeedWorkflowRunAsync(db, "wr_sd_range_outside", ApprovalRunState("wr_sd_range_outside", now.AddDays(-100).AddHours(-2), TimeSpan.Zero));
        SeedWorkflowRunEvent(db, "wr_sd_range_outside", 1, EventCatalog.ReverseDns.StageStarted, now.AddDays(-100).AddHours(-1), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_range_outside", 2, EventCatalog.ReverseDns.StageCompleted, now.AddDays(-100), new { stage = "plan" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetStageDurationsAsync(project.Id, now, windowDays: 90);

        Assert.Equal(now.AddDays(-90), result.Window.From);
        Assert.Equal(now, result.Window.To);
        var planStage = Assert.Single(result.Stages, s => s.Stage == "plan");
        Assert.Equal(1, planStage.SampleCount);
        Assert.Equal(3600, planStage.AverageSeconds!.Value, precision: 3);
    }
}
