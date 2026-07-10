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
public class IssueQualityLifecycleQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueQualityLifecycleQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetQualityAsync_PriorLifecycleRunRepair_PreventsFirstTimeRight()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-lifecycle-{Guid.NewGuid():N}", Name = "Quality Lifecycle Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_lifecycle_1", workflowRunId: "wr_quality_lifecycle_final", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, now.AddDays(-5), "wr_quality_lifecycle_first");
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, now.AddDays(-2), "wr_quality_lifecycle_final");
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_lifecycle_final");

        await SeedWorkflowRunAsync(db, "wr_quality_lifecycle_first", QualityRunState("wr_quality_lifecycle_first", [
            ("plan", [("plan-repair", "Plan repair", 1)]),
        ]));
        await SeedWorkflowRunAsync(db, "wr_quality_lifecycle_final", QualityRunState("wr_quality_lifecycle_final", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-ok", "Build ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(0.0, result.Window.FirstTimeRightRate);
        var plan = Assert.Single(result.Window.Stages, s => s.Stage == "plan");
        Assert.Equal(1, plan.EnteredCount);
        Assert.Equal(1.0, plan.ReworkRate);
        var build = Assert.Single(result.Window.Stages, s => s.Stage == "build");
        Assert.Equal(1, build.EnteredCount);
        Assert.Equal(0.0, build.ReworkRate);
    }

    [Fact]
    public async Task GetQualityAsync_HistoricalRepairEventBeforeStageRerun_UsesDurableReworkHistory()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-rerun-repair-{Guid.NewGuid():N}", Name = "Quality Rerun Repair Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_rerun_repair_1", workflowRunId: "wr_quality_rerun_repair_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_rerun_repair_1");
        await SeedWorkflowRunAsync(db, "wr_quality_rerun_repair_1", QualityRunState("wr_quality_rerun_repair_1", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("check", [("review", "Review", 0)]),
        ]));
        SeedWorkflowRunEvent(db, "wr_quality_rerun_repair_1", 1, EventCatalog.ReverseDns.StageStarted, now.AddDays(-2), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_quality_rerun_repair_1", 2, EventCatalog.ReverseDns.StageStarted, now.AddDays(-2), new { stage = "check" });
        SeedWorkflowRunEvent(db, "wr_quality_rerun_repair_1", 3, EventCatalog.ReverseDns.RepairScheduled, now.AddDays(-2), new { stage = "check", checkName = "review", taskIds = new[] { "repair-1" } });
        SeedWorkflowRunEvent(db, "wr_quality_rerun_repair_1", 4, EventCatalog.ReverseDns.StageStarted, now.AddDays(-2), new { stage = "check" });
        SeedWorkflowRunEvent(db, "wr_quality_rerun_repair_1", 5, EventCatalog.ReverseDns.CheckPassed, now.AddDays(-1), new { stage = "check", checkName = "review", message = "ok" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(0.0, result.Window.FirstTimeRightRate);

        var check = Assert.Single(result.Window.Stages, s => s.Stage == "check");
        Assert.Equal(1, check.EnteredCount);
        Assert.Equal(1.0, check.ReworkRate);
    }

    [Fact]
    public async Task GetQualityAsync_CheckFailsThenManualRetry_CountsRepeatedCheckRunAsRework()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-check-retry-{Guid.NewGuid():N}", Name = "Quality Check Retry Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_check_retry_1", workflowRunId: "wr_quality_check_retry_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_check_retry_1");
        await SeedWorkflowRunAsync(db, "wr_quality_check_retry_1", QualityRunState("wr_quality_check_retry_1", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("check", [("review", "Review", 0)]),
        ]));
        SeedWorkflowRunEvent(db, "wr_quality_check_retry_1", 1, EventCatalog.ReverseDns.StageStarted, now.AddDays(-2), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_quality_check_retry_1", 2, EventCatalog.ReverseDns.CheckPassed, now.AddDays(-2), new { stage = "plan", checkName = "plan-ok", message = "ok" });
        SeedWorkflowRunEvent(db, "wr_quality_check_retry_1", 3, EventCatalog.ReverseDns.StageStarted, now.AddDays(-2), new { stage = "check" });
        SeedWorkflowRunEvent(db, "wr_quality_check_retry_1", 4, EventCatalog.ReverseDns.CheckFailed, now.AddDays(-2), new { stage = "check", checkName = "review", message = "broken" });
        SeedWorkflowRunEvent(db, "wr_quality_check_retry_1", 5, EventCatalog.ReverseDns.CheckPassed, now.AddDays(-1), new { stage = "check", checkName = "review", message = "ok" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(0.0, result.Window.FirstTimeRightRate);

        var check = Assert.Single(result.Window.Stages, s => s.Stage == "check");
        Assert.Equal(1, check.EnteredCount);
        Assert.Equal(1.0, check.ReworkRate);
    }

    [Fact]
    public async Task GetQualityAsync_MissingWorkflowRun_CountsAsNotFirstTimeRight()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-missing-run-{Guid.NewGuid():N}", Name = "Quality Missing Run Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_missing_run_1", workflowRunId: "wr_quality_missing_run_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_missing_run_1");
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(0.0, result.Window.FirstTimeRightRate);
        Assert.All(result.Window.Stages, stage => Assert.Equal(0, stage.EnteredCount));
    }

    [Fact]
    public async Task GetQualityAsync_UnreadableWorkflowRun_CountsAsNotFirstTimeRight()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-corrupt-run-{Guid.NewGuid():N}", Name = "Quality Corrupt Run Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_corrupt_run_1", workflowRunId: "wr_quality_corrupt_run_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_corrupt_run_1");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            "wr_quality_corrupt_run_1",
            "{\"workflowRunId\":\"wr_quality_corrupt_run_1\",\"status\":\"not-a-status\",\"stages\":[]}");
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(0.0, result.Window.FirstTimeRightRate);
        Assert.All(result.Window.Stages, stage => Assert.Equal(0, stage.EnteredCount));
    }

    [Fact]
    public async Task GetQualityAsync_ProjectScoping_OnlyCountsTargetProjectsIssues()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var projectA = new ProjectInfo { Id = $"proj-quality-scope-a-{Guid.NewGuid():N}", Name = "Quality Scope A" };
        var projectB = new ProjectInfo { Id = $"proj-quality-scope-b-{Guid.NewGuid():N}", Name = "Quality Scope B" };
        var a1 = SeedIssue(db, projectA, "issue_quality_scope_a_1", workflowRunId: "wr_quality_scope_a_1", status: IssueStatus.Done);
        var b1 = SeedIssue(db, projectB, "issue_quality_scope_b_1", workflowRunId: "wr_quality_scope_b_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, a1.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero), "wr_quality_scope_a_1");
        SeedEvent(db, b1.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero), "wr_quality_scope_b_1");

        await SeedWorkflowRunAsync(db, "wr_quality_scope_a_1", QualityRunState("wr_quality_scope_a_1", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await SeedWorkflowRunAsync(db, "wr_quality_scope_b_1", QualityRunState("wr_quality_scope_b_1", [("plan", [("plan-ok", "Plan ok", 1)])]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var resultA = await service.GetQualityAsync(projectA.Id, now);
        var resultB = await service.GetQualityAsync(projectB.Id, now);

        Assert.Equal(1, resultA.Window.SampleCount);
        Assert.Equal(1.0, resultA.Window.FirstTimeRightRate);
        Assert.Equal(1, resultB.Window.SampleCount);
        Assert.Equal(0.0, resultB.Window.FirstTimeRightRate);
    }

}
