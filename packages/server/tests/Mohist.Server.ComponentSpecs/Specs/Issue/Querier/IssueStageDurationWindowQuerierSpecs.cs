using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.ComponentSpecs.Support;
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
using static Mohist.Server.ComponentSpecs.Specs.Issue.Querier.IssueMetricsQuerierTestData;

namespace Mohist.Server.ComponentSpecs.Specs.Issue.Querier;

[Collection("MohistDb")]
public class IssueStageDurationWindowQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueStageDurationWindowQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetStageDurationsAsync_StageSpanGreaterThanCycle_ExcludesIssueFromCycleAggregates()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-invalid-stage-{Guid.NewGuid():N}", Name = "Stage Duration Invalid Stage" };
        var completedAt = new DateTime(2026, 6, 10, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_sd_invalid_stage",
            createdAt: completedAt.AddDays(-2),
            completedAt: completedAt,
            workflowRunId: "wr_sd_invalid_stage");
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, completedAt.AddHours(-1), workflowRunId: "wr_sd_invalid_stage");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_sd_invalid_stage", ApprovalRunState("wr_sd_invalid_stage", completedAt.AddHours(-2), TimeSpan.Zero));
        SeedWorkflowRunEvent(db, "wr_sd_invalid_stage", 1, EventCatalog.ReverseDns.StageStarted, completedAt.AddHours(-2), new { stage = "build" });
        SeedWorkflowRunEvent(db, "wr_sd_invalid_stage", 2, EventCatalog.ReverseDns.StageCompleted, completedAt, new { stage = "build" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetStageDurationsAsync(project.Id, new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));

        Assert.Single(result.Stages, s => s.Stage == "build");
        Assert.Null(result.FlowEfficiencyRatio);
        Assert.NotNull(result.WaitBreakout);
        Assert.Null(result.WaitBreakout!.AverageApprovalGateWaitSeconds);
        Assert.Null(result.WaitBreakout.AverageInactiveGapSeconds);
    }

    [Fact]
    public async Task GetStageDurationsAsync_NoDeliveredIssuesInWindow_ReturnsEmptyResult()
    {
        // No delivered issues in the window yields a defined empty
        // result: empty stages array, null ratio, null wait fields,
        // zero sample counts. NOT an error.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-empty-{Guid.NewGuid():N}", Name = "Stage Duration Empty" };
        SeedIssue(db, project, "issue_sd_empty_1");
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetStageDurationsAsync(project.Id, now);

        Assert.Empty(result.Stages);
        Assert.Null(result.FlowEfficiencyRatio);
        Assert.NotNull(result.WaitBreakout);
        Assert.Null(result.WaitBreakout!.AverageApprovalGateWaitSeconds);
        Assert.Null(result.WaitBreakout.AverageInactiveGapSeconds);
    }

    [Fact]
    public async Task GetStageDurationsAsync_GenuineZeroDurationStage_DistinctFromEmpty()
    {
        // A genuine zero-duration stage (same StageStarted and
        // StageCompleted moment) is reported as a real value with a
        // non-zero sample count, distinguishable from the empty result.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-zero-{Guid.NewGuid():N}", Name = "Stage Duration Zero" };
        var zeroMoment = new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_sd_zero",
            createdAt: new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: zeroMoment,
            workflowRunId: "wr_sd_zero");
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(zeroMoment, TimeSpan.Zero), workflowRunId: "wr_sd_zero");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_sd_zero", ApprovalRunState("wr_sd_zero", requestedAt: new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero), wait: TimeSpan.Zero));
        // Genuine zero-duration stage at the same moment.
        SeedWorkflowRunEvent(db, "wr_sd_zero", 1, EventCatalog.ReverseDns.StageStarted, new DateTimeOffset(zeroMoment, TimeSpan.Zero), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_zero", 2, EventCatalog.ReverseDns.StageCompleted, new DateTimeOffset(zeroMoment, TimeSpan.Zero), new { stage = "plan" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetStageDurationsAsync(project.Id, now);

        var planStage = Assert.Single(result.Stages, s => s.Stage == "plan");
        Assert.Equal(1, planStage.SampleCount);
        Assert.NotNull(planStage.AverageSeconds);
        Assert.Equal(0.0, planStage.AverageSeconds!.Value, precision: 3);
        Assert.NotNull(planStage.MedianSeconds);
        Assert.Equal(0.0, planStage.MedianSeconds!.Value, precision: 3);
    }

    [Fact]
    public async Task GetStageDurationsAsync_CompletedBeyond30Days_ExcludedFromWindow()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-window-{Guid.NewGuid():N}", Name = "Stage Duration Window" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        // Inside the 30-day window.
        var inside = SeedDeliveredIssue(
            db, project, "issue_sd_window_inside",
            createdAt: new DateTime(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc),
            workflowRunId: "wr_sd_window_inside");
        SeedEvent(db, inside.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_window_inside");
        await db.SaveChangesAsync();
        await SeedWorkflowRunAsync(db, "wr_sd_window_inside", ApprovalRunState("wr_sd_window_inside", requestedAt: new DateTimeOffset(2026, 6, 18, 8, 0, 0, TimeSpan.Zero), wait: TimeSpan.Zero));
        SeedWorkflowRunEvent(db, "wr_sd_window_inside", 1, EventCatalog.ReverseDns.StageStarted, new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_window_inside", 2, EventCatalog.ReverseDns.StageCompleted, new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero), new { stage = "plan" });

        // Outside: 31 days before `now`.
        var outside = SeedDeliveredIssue(
            db, project, "issue_sd_window_outside",
            createdAt: new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc),
            workflowRunId: "wr_sd_window_outside");
        SeedEvent(db, outside.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 5, 19, 9, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_window_outside");
        await db.SaveChangesAsync();
        await SeedWorkflowRunAsync(db, "wr_sd_window_outside", ApprovalRunState("wr_sd_window_outside", requestedAt: new DateTimeOffset(2026, 5, 19, 8, 0, 0, TimeSpan.Zero), wait: TimeSpan.Zero));
        SeedWorkflowRunEvent(db, "wr_sd_window_outside", 1, EventCatalog.ReverseDns.StageStarted, new DateTimeOffset(2026, 5, 19, 9, 0, 0, TimeSpan.Zero), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_window_outside", 2, EventCatalog.ReverseDns.StageCompleted, new DateTimeOffset(2026, 5, 19, 10, 0, 0, TimeSpan.Zero), new { stage = "plan" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetStageDurationsAsync(project.Id, now);

        var planStage = Assert.Single(result.Stages, s => s.Stage == "plan");
        Assert.Equal(1, planStage.SampleCount);
        Assert.Equal(3600, planStage.AverageSeconds!.Value, precision: 3);

        var ninetyDayResult = await service.GetStageDurationsAsync(project.Id, now, windowDays: 90);
        var ninetyDayPlanStage = Assert.Single(ninetyDayResult.Stages, s => s.Stage == "plan");
        Assert.Equal(2, ninetyDayPlanStage.SampleCount);
        Assert.Equal(3600, ninetyDayPlanStage.AverageSeconds!.Value, precision: 3);
    }

    [Fact]
    public async Task GetStageDurationsAsync_DeliveredIssuesInOtherProject_NotInStages()
    {
        // Project scoping: only the target project's delivered issues
        // contribute.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var projectA = new ProjectInfo { Id = $"proj-sd-scope-a-{Guid.NewGuid():N}", Name = "Scope A" };
        var projectB = new ProjectInfo { Id = $"proj-sd-scope-b-{Guid.NewGuid():N}", Name = "Scope B" };
        var completedAt = new DateTime(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc);
        var insideA = SeedDeliveredIssue(
            db, projectA, "issue_sd_scope_a",
            createdAt: new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc),
            completedAt: completedAt,
            workflowRunId: "wr_sd_scope_a");
        var insideB = SeedDeliveredIssue(
            db, projectB, "issue_sd_scope_b",
            createdAt: new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc),
            completedAt: completedAt,
            workflowRunId: "wr_sd_scope_b");
        SeedEvent(db, insideA.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(completedAt.AddHours(-2), TimeSpan.Zero), workflowRunId: "wr_sd_scope_a");
        SeedEvent(db, insideB.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(completedAt.AddHours(-4), TimeSpan.Zero), workflowRunId: "wr_sd_scope_b");
        await db.SaveChangesAsync();
        await SeedWorkflowRunAsync(db, "wr_sd_scope_a", ApprovalRunState("wr_sd_scope_a", requestedAt: new DateTimeOffset(2026, 6, 18, 7, 0, 0, TimeSpan.Zero), wait: TimeSpan.Zero));
        await SeedWorkflowRunAsync(db, "wr_sd_scope_b", ApprovalRunState("wr_sd_scope_b", requestedAt: new DateTimeOffset(2026, 6, 18, 7, 0, 0, TimeSpan.Zero), wait: TimeSpan.Zero));
        SeedWorkflowRunEvent(db, "wr_sd_scope_a", 1, EventCatalog.ReverseDns.StageStarted, completedAt.AddHours(-2), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_scope_a", 2, EventCatalog.ReverseDns.StageCompleted, completedAt, new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_scope_b", 1, EventCatalog.ReverseDns.StageStarted, completedAt.AddHours(-4), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_scope_b", 2, EventCatalog.ReverseDns.StageCompleted, completedAt, new { stage = "plan" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var resultA = await service.GetStageDurationsAsync(projectA.Id, now);
        var resultB = await service.GetStageDurationsAsync(projectB.Id, now);

        var planA = Assert.Single(resultA.Stages, s => s.Stage == "plan");
        Assert.Equal(2 * 3600, planA.AverageSeconds!.Value, precision: 3);

        var planB = Assert.Single(resultB.Stages, s => s.Stage == "plan");
        Assert.Equal(4 * 3600, planB.AverageSeconds!.Value, precision: 3);
    }

    [Fact]
    public async Task GetStageDurationsAsync_StagesOrderedByWorkflowStageOrder()
    {
        // Spec: stages are returned in the workflow's stage order (plan
        // → build → check → integrate) regardless of insertion order.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-order-{Guid.NewGuid():N}", Name = "Stage Duration Order" };
        var completedAt = new DateTime(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_sd_order",
            createdAt: new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc),
            completedAt: completedAt,
            workflowRunId: "wr_sd_order");
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, completedAt.AddHours(-10), workflowRunId: "wr_sd_order");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_sd_order", ApprovalRunState("wr_sd_order", requestedAt: new DateTimeOffset(2026, 6, 10, 8, 0, 0, TimeSpan.Zero), wait: TimeSpan.Zero));
        // Insert in reverse order to verify the response reorders.
        SeedWorkflowRunEvent(db, "wr_sd_order", 1, EventCatalog.ReverseDns.StageStarted, completedAt.AddHours(-2), new { stage = "integrate" });
        SeedWorkflowRunEvent(db, "wr_sd_order", 2, EventCatalog.ReverseDns.StageCompleted, completedAt.AddHours(-1), new { stage = "integrate" });
        SeedWorkflowRunEvent(db, "wr_sd_order", 3, EventCatalog.ReverseDns.StageStarted, completedAt.AddHours(-10), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_order", 4, EventCatalog.ReverseDns.StageCompleted, completedAt.AddHours(-7), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_order", 5, EventCatalog.ReverseDns.StageStarted, completedAt.AddHours(-7), new { stage = "build" });
        SeedWorkflowRunEvent(db, "wr_sd_order", 6, EventCatalog.ReverseDns.StageCompleted, completedAt.AddHours(-5), new { stage = "build" });
        SeedWorkflowRunEvent(db, "wr_sd_order", 7, EventCatalog.ReverseDns.StageStarted, completedAt.AddHours(-5), new { stage = "check" });
        SeedWorkflowRunEvent(db, "wr_sd_order", 8, EventCatalog.ReverseDns.StageCompleted, completedAt.AddHours(-2), new { stage = "check" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetStageDurationsAsync(project.Id, now);

        var stageNames = result.Stages.Select(s => s.Stage).ToArray();
        Assert.Equal(new[] { "plan", "build", "check", "integrate" }, stageNames);
    }

}
