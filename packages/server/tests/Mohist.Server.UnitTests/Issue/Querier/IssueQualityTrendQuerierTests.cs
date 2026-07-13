using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Services;
using Xunit;
using static Mohist.Server.UnitTests.Issue.Querier.IssueMetricsQuerierTestData;

namespace Mohist.Server.UnitTests.Issue.Querier;

[Collection("MohistDb")]
public class IssueQualityTrendQuerierTests
{
    private readonly MohistDbFixture _fixture;

    public IssueQualityTrendQuerierTests(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetQualityAsync_Trend_ReturnsPreSizedThirtyDayDailySeries()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-shaped-{Guid.NewGuid():N}", Name = "Quality Trend Shape" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        SeedIssue(db, project, "issue_quality_trend_shape_1", workflowRunId: "wr_quality_trend_shape_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal("day", result.Trend.Bucket);
        Assert.Equal(30, result.Trend.Points.Count);
        Assert.Equal("2026-05-21", result.Trend.Points[0].Boundary);
        Assert.Equal("2026-06-19", result.Trend.Points[^1].Boundary);
        // Window matches the scalar 30d window.
        Assert.Equal(result.Window.From, result.Trend.WindowFrom);
        Assert.Equal(result.Window.To, result.Trend.WindowTo);
        // No issues shipped: every bucket is the empty result (null rates).
        Assert.All(result.Trend.Points, p =>
        {
            Assert.Equal(0, p.SampleCount);
            Assert.Null(p.FirstTimeRightRate);
            Assert.Null(p.ReworkRate);
        });
    }

    [Fact]
    public async Task GetQualityAsync_Trend_IncludesLeadingCalendarBoundarySample()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-leading-{Guid.NewGuid():N}", Name = "Quality Trend Leading" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var shipTime = new DateTimeOffset(2026, 5, 21, 9, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_trend_leading_1", workflowRunId: "wr_quality_trend_leading_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, shipTime, "wr_quality_trend_leading_1");
        await SeedWorkflowRunAsync(db, "wr_quality_trend_leading_1", QualityRunState("wr_quality_trend_leading_1", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        var leadingPoint = Assert.Single(result.Trend.Points, p => p.Boundary == "2026-05-21");
        Assert.Equal(1, leadingPoint.SampleCount);
        Assert.Equal(1.0, leadingPoint.FirstTimeRightRate);
        Assert.Equal(0.0, leadingPoint.ReworkRate);
    }

    [Fact]
    public async Task GetQualityAsync_Trend_PerBucketFtrRateEqualsFtrShippedOverAllShipped()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-ftr-{Guid.NewGuid():N}", Name = "Quality Trend Ftr" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        // Day 17 (3 days ago): 1 FTR + 1 not-FTR → 1/2 = 0.5
        var ftrDay17 = SeedIssue(db, project, "issue_quality_trend_ftr_a", workflowRunId: "wr_quality_trend_ftr_a", status: IssueStatus.Done);
        var notFtrDay17 = SeedIssue(db, project, "issue_quality_trend_ftr_b", workflowRunId: "wr_quality_trend_ftr_b", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, ftrDay17.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2).AddHours(1), "wr_quality_trend_ftr_a");
        SeedEvent(db, notFtrDay17.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2).AddHours(1), "wr_quality_trend_ftr_b");
        await SeedWorkflowRunAsync(db, "wr_quality_trend_ftr_a", QualityRunState("wr_quality_trend_ftr_a", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await SeedWorkflowRunAsync(db, "wr_quality_trend_ftr_b", QualityRunState("wr_quality_trend_ftr_b", [("plan", [("plan-repair", "Plan repair", 1)])]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        var day17 = Assert.Single(result.Trend.Points, p => p.Boundary == "2026-06-17");
        Assert.Equal(2, day17.SampleCount);
        Assert.Equal(0.5, day17.FirstTimeRightRate);
    }

    [Fact]
    public async Task GetQualityAsync_Trend_PerBucketReworkRateUsesAnyStageClassification()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-rework-{Guid.NewGuid():N}", Name = "Quality Trend Rework" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        // Day 18 (1 day ago): 1 issue, plan stage repaired → reworked-at-any-stage = true
        var reworked = SeedIssue(db, project, "issue_quality_trend_rework_1", workflowRunId: "wr_quality_trend_rework_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, reworked.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_trend_rework_1");
        await SeedWorkflowRunAsync(db, "wr_quality_trend_rework_1", QualityRunState("wr_quality_trend_rework_1", [
            ("plan", [("plan-repair", "Plan repair", 1)]),
            ("build", [("build-ok", "Build ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        var day18 = Assert.Single(result.Trend.Points, p => p.Boundary == "2026-06-18");
        Assert.Equal(1, day18.SampleCount);
        Assert.Equal(0.0, day18.FirstTimeRightRate);
        Assert.Equal(1.0, day18.ReworkRate);
    }

    [Fact]
    public async Task GetQualityAsync_Trend_IssueReworkedAtMultipleStagesCountsOnce()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-multistage-{Guid.NewGuid():N}", Name = "Quality Trend MultiStage" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_trend_multistage_1", workflowRunId: "wr_quality_trend_multistage_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-3), "wr_quality_trend_multistage_1");
        await SeedWorkflowRunAsync(db, "wr_quality_trend_multistage_1", QualityRunState("wr_quality_trend_multistage_1", [
            ("plan", [("plan-repair", "Plan repair", 1)]),
            ("build", [("build-repair", "Build repair", 1)]),
            ("check", [("check-ok", "Check ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        var day = Assert.Single(result.Trend.Points, p => p.SampleCount > 0);
        Assert.Equal(1, day.SampleCount);
        // Two stages reworked, but the issue contributes ONE to the
        // any-stage numerator — the rate stays 1.0, not 2.0.
        Assert.Equal(1.0, day.ReworkRate);
        // The scalar 30d stage rates stay per-stage (sum > 1) so the
        // test is unambiguous about which surface is being read.
        var plan = Assert.Single(result.Window.Stages, s => s.Stage == "plan");
        var build = Assert.Single(result.Window.Stages, s => s.Stage == "build");
        Assert.Equal(1.0, plan.ReworkRate);
        Assert.Equal(1.0, build.ReworkRate);
    }

    [Fact]
    public async Task GetQualityAsync_Trend_EmptyBucketYieldsNullRatesIndependentOfSiblings()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-empty-{Guid.NewGuid():N}", Name = "Quality Trend Empty" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_trend_empty_1", workflowRunId: "wr_quality_trend_empty_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_trend_empty_1");
        await SeedWorkflowRunAsync(db, "wr_quality_trend_empty_1", QualityRunState("wr_quality_trend_empty_1", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        // Day 18 has a sample.
        var day18 = Assert.Single(result.Trend.Points, p => p.Boundary == "2026-06-18");
        Assert.Equal(1, day18.SampleCount);
        Assert.Equal(1.0, day18.FirstTimeRightRate);
        Assert.Equal(0.0, day18.ReworkRate);
        // Day 17 has no ships: independent null rates (not 0 or 1).
        var day17 = Assert.Single(result.Trend.Points, p => p.Boundary == "2026-06-17");
        Assert.Equal(0, day17.SampleCount);
        Assert.Null(day17.FirstTimeRightRate);
        Assert.Null(day17.ReworkRate);
        // Sanity: every other bucket is also null — no fabricated zero.
        Assert.All(
            result.Trend.Points.Where(p => p.Boundary != "2026-06-18"),
            p =>
            {
                Assert.Equal(0, p.SampleCount);
                Assert.Null(p.FirstTimeRightRate);
                Assert.Null(p.ReworkRate);
            });
    }

    [Fact]
    public async Task GetQualityAsync_Trend_NonShippedIssuesDoNotContributeToAnyBucket()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-nonshipped-{Guid.NewGuid():N}", Name = "Quality Trend NonShipped" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var inProgress = SeedIssue(db, project, "issue_quality_trend_ns_inprog", workflowRunId: "wr_quality_trend_ns_inprog", status: IssueStatus.InProgress);
        var backlog = SeedIssue(db, project, "issue_quality_trend_ns_backlog", status: IssueStatus.Backlog);
        var cancelled = SeedIssue(db, project, "issue_quality_trend_ns_cancelled", workflowRunId: "wr_quality_trend_ns_cancelled", status: IssueStatus.Cancelled);
        await db.SaveChangesAsync();

        // Even if these non-Done issues had terminal events, they
        // must not appear in any trend bucket.
        SeedEvent(db, inProgress.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_trend_ns_inprog");
        SeedEvent(db, backlog.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2));
        SeedEvent(db, cancelled.Id, EventCatalog.ReverseDns.IssueCancelled, now.AddDays(-3));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.All(result.Trend.Points, p =>
        {
            Assert.Equal(0, p.SampleCount);
            Assert.Null(p.FirstTimeRightRate);
            Assert.Null(p.ReworkRate);
        });
    }

    [Fact]
    public async Task GetQualityAsync_Trend_BucketMembershipIsAnchoredOnShipTime()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-anchor-{Guid.NewGuid():N}", Name = "Quality Trend Anchor" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_trend_anchor_1", workflowRunId: "wr_quality_trend_anchor_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        // Anchor the ship event on day 5 of the trailing window
        // (now.AddDays(-5) → 2026-06-14, a Sunday).
        var shipTime = now.AddDays(-5);
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, shipTime, "wr_quality_trend_anchor_1");
        await SeedWorkflowRunAsync(db, "wr_quality_trend_anchor_1", QualityRunState("wr_quality_trend_anchor_1", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        var day = Assert.Single(result.Trend.Points, p => p.SampleCount > 0);
        Assert.Equal(shipTime.UtcDateTime.Date.ToString("yyyy-MM-dd"), day.Boundary);
        Assert.Equal(1, day.SampleCount);
        Assert.Equal(1.0, day.FirstTimeRightRate);
    }

    [Fact]
    public async Task GetQualityAsync_Trend_CurrentDayMorningShipUsesCurrentCalendarDayBucket()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-today-{Guid.NewGuid():N}", Name = "Quality Trend Today" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var shipTime = new DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_trend_today_1", workflowRunId: "wr_quality_trend_today_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, shipTime, "wr_quality_trend_today_1");
        await SeedWorkflowRunAsync(db, "wr_quality_trend_today_1", QualityRunState("wr_quality_trend_today_1", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        var today = Assert.Single(result.Trend.Points, p => p.Boundary == "2026-06-19");
        Assert.Equal(1, today.SampleCount);
        Assert.Equal(1.0, today.FirstTimeRightRate);
        Assert.Equal(0.0, today.ReworkRate);

        var yesterday = Assert.Single(result.Trend.Points, p => p.Boundary == "2026-06-18");
        Assert.Equal(0, yesterday.SampleCount);
        Assert.Null(yesterday.FirstTimeRightRate);
        Assert.Null(yesterday.ReworkRate);
    }

    [Fact]
    public async Task GetQualityAsync_Trend_MidWindowMorningShipUsesItsCalendarDayBucket()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-midday-{Guid.NewGuid():N}", Name = "Quality Trend Midday" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var shipTime = new DateTimeOffset(2026, 6, 9, 9, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_trend_midday_1", workflowRunId: "wr_quality_trend_midday_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, shipTime, "wr_quality_trend_midday_1");
        await SeedWorkflowRunAsync(db, "wr_quality_trend_midday_1", QualityRunState("wr_quality_trend_midday_1", [
            ("plan", [("plan-repair", "Plan repair", 1)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        var shipDay = Assert.Single(result.Trend.Points, p => p.Boundary == "2026-06-09");
        Assert.Equal(1, shipDay.SampleCount);
        Assert.Equal(0.0, shipDay.FirstTimeRightRate);
        Assert.Equal(1.0, shipDay.ReworkRate);

        var previousDay = Assert.Single(result.Trend.Points, p => p.Boundary == "2026-06-08");
        Assert.Equal(0, previousDay.SampleCount);
        Assert.Null(previousDay.FirstTimeRightRate);
        Assert.Null(previousDay.ReworkRate);
    }

    [Fact]
    public async Task GetQualityAsync_Trend_AdditiveAndLeavesWindowScalarsUnchanged()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-additive-{Guid.NewGuid():N}", Name = "Quality Trend Additive" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_trend_additive_1", workflowRunId: "wr_quality_trend_additive_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_trend_additive_1");
        await SeedWorkflowRunAsync(db, "wr_quality_trend_additive_1", QualityRunState("wr_quality_trend_additive_1", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-ok", "Build ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        // The primary window is untouched by the trend addition —
        // same SampleCount, same FTR rate, same stages.
        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(1.0, result.Window.FirstTimeRightRate);
        Assert.Contains(result.Window.Stages, s => s.Stage == "plan" && s.EnteredCount == 1 && s.ReworkRate == 0.0);
        Assert.Contains(result.Window.Stages, s => s.Stage == "build" && s.EnteredCount == 1 && s.ReworkRate == 0.0);
        // The trend lives on the same read, dense 30-day.
        Assert.Equal(30, result.Trend.Points.Count);
        var day = Assert.Single(result.Trend.Points, p => p.SampleCount > 0);
        Assert.Equal(1.0, day.FirstTimeRightRate);
        Assert.Equal(0.0, day.ReworkRate);
    }


}
