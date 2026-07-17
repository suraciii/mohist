using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Issue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Services;
using Mohist.Server.SpecTests.Specs.Sessions;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

using static Mohist.Server.SpecTests.Specs.Issue.Querier.IssueMetricsTestSupport;

[Collection("MohistDb")]
public class IssueMetricsQualitySpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueMetricsQualitySpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetQualityAsync_AllChecksZeroRepair_IsFirstTimeRight()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-ftr-{Guid.NewGuid():N}", Name = "Quality FTR Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_ftr_1", workflowRunId: "wr_quality_ftr_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_ftr_1");
        await SeedWorkflowRunAsync(db, "wr_quality_ftr_1", QualityRunState("wr_quality_ftr_1", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-ok", "Build ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(1.0, result.Window.FirstTimeRightRate);
        Assert.Contains(result.Window.Stages, s => s.Stage == "plan" && s.EnteredCount == 1 && s.ReworkRate == 0.0);
        Assert.Contains(result.Window.Stages, s => s.Stage == "build" && s.EnteredCount == 1 && s.ReworkRate == 0.0);
    }

    [Fact]
    public async Task GetQualityAsync_AnyRepairedCheck_IsNotFirstTimeRight()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-rework-{Guid.NewGuid():N}", Name = "Quality Rework Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_rework_1", workflowRunId: "wr_quality_rework_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_rework_1");
        await SeedWorkflowRunAsync(db, "wr_quality_rework_1", QualityRunState("wr_quality_rework_1", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-ok", "Build ok", 1)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(0.0, result.Window.FirstTimeRightRate);
        Assert.Contains(result.Window.Stages, s => s.Stage == "plan" && s.EnteredCount == 1 && s.ReworkRate == 0.0);
        Assert.Contains(result.Window.Stages, s => s.Stage == "build" && s.EnteredCount == 1 && s.ReworkRate == 1.0);
    }

    [Fact]
    public async Task GetQualityAsync_NonDoneIssues_AreExcludedFromNumeratorAndDenominator()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-status-{Guid.NewGuid():N}", Name = "Quality Status Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var shipped = SeedIssue(db, project, "issue_quality_status_shipped", workflowRunId: "wr_quality_status_shipped", status: IssueStatus.Done);
        var inProgress = SeedIssue(db, project, "issue_quality_status_inprogress", workflowRunId: "wr_quality_status_inprogress", status: IssueStatus.InProgress);
        SeedIssue(db, project, "issue_quality_status_backlog", workflowRunId: null, status: IssueStatus.Backlog);
        await db.SaveChangesAsync();

        SeedEvent(db, shipped, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_status_shipped");
        await SeedWorkflowRunAsync(db, "wr_quality_status_shipped", QualityRunState("wr_quality_status_shipped", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await SeedWorkflowRunAsync(db, "wr_quality_status_inprogress", QualityRunState("wr_quality_status_inprogress", [("plan", [("plan-ok", "Plan ok", 1)])]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(1.0, result.Window.FirstTimeRightRate);
        var plan = Assert.Single(result.Window.Stages, s => s.Stage == "plan");
        Assert.Equal(1, plan.EnteredCount);
        Assert.Equal(0.0, plan.ReworkRate);
    }

    [Fact]
    public async Task GetQualityAsync_NeverEnteredStage_IsReturnedWithNullStageRate()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-stage-{Guid.NewGuid():N}", Name = "Quality Stage Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_stage_1", workflowRunId: "wr_quality_stage_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_stage_1");
        await SeedWorkflowRunAsync(db, "wr_quality_stage_1", QualityRunState("wr_quality_stage_1", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-ok", "Build ok", 0)]),
            ("integrate", null),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Contains(result.Window.Stages, s => s.Stage == "plan" && s.EnteredCount == 1);
        Assert.Contains(result.Window.Stages, s => s.Stage == "build" && s.EnteredCount == 1);
        var check = Assert.Single(result.Window.Stages, s => s.Stage == "check");
        Assert.Equal(0, check.EnteredCount);
        Assert.Null(check.ReworkRate);
        var integrate = Assert.Single(result.Window.Stages, s => s.Stage == "integrate");
        Assert.Equal(0, integrate.EnteredCount);
        Assert.Null(integrate.ReworkRate);
    }

    [Fact]
    public async Task GetQualityAsync_WindowBucketing_BucketsByShipEventTime()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-window-{Guid.NewGuid():N}", Name = "Quality Window Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var recent = SeedIssue(db, project, "issue_quality_win_recent", workflowRunId: "wr_quality_win_recent", status: IssueStatus.Done);
        var mid = SeedIssue(db, project, "issue_quality_win_mid", workflowRunId: "wr_quality_win_mid", status: IssueStatus.Done);
        var old = SeedIssue(db, project, "issue_quality_win_old", workflowRunId: "wr_quality_win_old", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, recent, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-3), "wr_quality_win_recent");
        SeedEvent(db, mid, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-20), "wr_quality_win_mid");
        SeedEvent(db, old, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-40), "wr_quality_win_old");

        await SeedWorkflowRunAsync(db, "wr_quality_win_recent", QualityRunState("wr_quality_win_recent", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await SeedWorkflowRunAsync(db, "wr_quality_win_mid", QualityRunState("wr_quality_win_mid", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await SeedWorkflowRunAsync(db, "wr_quality_win_old", QualityRunState("wr_quality_win_old", [("plan", [("plan-ok", "Plan ok", 1)])]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(2, result.Window.SampleCount);
        Assert.Equal(1.0, result.Window.FirstTimeRightRate);
    }

    [Fact]
    public async Task GetQualityAsync_EmptyWindow_ReturnsNullRatesWithZeroSampleCount()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-empty-{Guid.NewGuid():N}", Name = "Quality Empty Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        SeedIssue(db, project, "issue_quality_empty_1", workflowRunId: "wr_quality_empty_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(0, result.Window.SampleCount);
        Assert.Null(result.Window.FirstTimeRightRate);
        Assert.Contains(result.Window.Stages, s => s.Stage == "plan" && s.EnteredCount == 0 && s.ReworkRate == null);
        Assert.Contains(result.Window.Stages, s => s.Stage == "build" && s.EnteredCount == 0 && s.ReworkRate == null);
        Assert.Contains(result.Window.Stages, s => s.Stage == "check" && s.EnteredCount == 0 && s.ReworkRate == null);
        Assert.Contains(result.Window.Stages, s => s.Stage == "integrate" && s.EnteredCount == 0 && s.ReworkRate == null);
    }

    [Fact]
    public async Task GetQualityAsync_PerStageDenominators_AreIndependent()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-denom-{Guid.NewGuid():N}", Name = "Quality Denom Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var reachedIntegrate = SeedIssue(db, project, "issue_quality_denom_integrate", workflowRunId: "wr_quality_denom_integrate", status: IssueStatus.Done);
        var onlyPlan = SeedIssue(db, project, "issue_quality_denom_plan", workflowRunId: "wr_quality_denom_plan", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, reachedIntegrate, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_denom_integrate");
        SeedEvent(db, onlyPlan, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_denom_plan");

        await SeedWorkflowRunAsync(db, "wr_quality_denom_integrate", QualityRunState("wr_quality_denom_integrate", [
            ("plan", [("plan-ok", "Plan ok", 1)]),
            ("integrate", [("integrate-ok", "Integrate ok", 0)]),
        ]));
        await SeedWorkflowRunAsync(db, "wr_quality_denom_plan", QualityRunState("wr_quality_denom_plan", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("integrate", null),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        var plan = Assert.Single(result.Window.Stages, s => s.Stage == "plan");
        Assert.Equal(2, plan.EnteredCount);
        Assert.Equal(0.5, plan.ReworkRate);

        var integrate = Assert.Single(result.Window.Stages, s => s.Stage == "integrate");
        Assert.Equal(1, integrate.EnteredCount);
        Assert.Equal(0.0, integrate.ReworkRate);
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

        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, now.AddDays(-5), "wr_quality_lifecycle_first");
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, now.AddDays(-2), "wr_quality_lifecycle_final");
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_lifecycle_final");

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

        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_rerun_repair_1");
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

        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_check_retry_1");
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
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_missing_run_1");
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
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_corrupt_run_1");
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
    public async Task GetQualityAsync_NullWorkflowRun_LogsReadModelAndMetricsIntegrityErrors()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-null-run-{Guid.NewGuid():N}", Name = "Quality Null Run Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        const string workflowRunId = "wr_quality_null_run_1";

        var issue = SeedIssue(db, project, "issue_quality_null_run_1", workflowRunId: workflowRunId, status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), workflowRunId);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            workflowRunId,
            "null");
        await db.SaveChangesAsync();

        var readModelLogger = new TestLogger<IssueReadModelLoader>();
        var loader = new IssueReadModelLoader(
            scope.ServiceProvider.GetRequiredService<IssueWorkflowProfileRegistry>(),
            scope.ServiceProvider.GetRequiredService<EffectiveWorkflowProfileResolver>(),
            scope.ServiceProvider.GetRequiredService<ProjectWorkflowProfileManager>(),
            readModelLogger);
        var metricsLogger = new TestLogger<IssueMetricsQuerier>();
        var service = new IssueMetricsQuerier(
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
            scope.ServiceProvider.GetRequiredService<IssueWorkflowProfileRegistry>(),
            scope.ServiceProvider.GetRequiredService<EffectiveWorkflowProfileResolver>(),
            scope.ServiceProvider.GetRequiredService<ProjectWorkflowProfileManager>(),
            loader,
            metricsLogger);

        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(0.0, result.Window.FirstTimeRightRate);
        Assert.Contains(readModelLogger.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && entry.Message.Contains(workflowRunId, StringComparison.Ordinal));
        Assert.Contains(metricsLogger.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && entry.Message.Contains(workflowRunId, StringComparison.Ordinal));
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

        SeedEvent(db, a1, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero), "wr_quality_scope_a_1");
        SeedEvent(db, b1, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero), "wr_quality_scope_b_1");

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

        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, shipTime, "wr_quality_trend_leading_1");
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

        SeedEvent(db, ftrDay17, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2).AddHours(1), "wr_quality_trend_ftr_a");
        SeedEvent(db, notFtrDay17, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2).AddHours(1), "wr_quality_trend_ftr_b");
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

        SeedEvent(db, reworked, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_trend_rework_1");
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

        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-3), "wr_quality_trend_multistage_1");
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

        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_trend_empty_1");
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
        SeedEvent(db, inProgress, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_trend_ns_inprog");
        SeedEvent(db, backlog, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2));
        SeedEvent(db, cancelled, EventCatalog.ReverseDns.IssueCancelled, now.AddDays(-3));
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
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, shipTime, "wr_quality_trend_anchor_1");
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

        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, shipTime, "wr_quality_trend_today_1");
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

        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, shipTime, "wr_quality_trend_midday_1");
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

        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_trend_additive_1");
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

    [Fact]
    public async Task GetQualityAsync_BothWindowsReturned_PreviousRateDerivableFromSeededRuns()
    {
        // now = 2026-06-30 00:00 UTC: current 30d window [2026-05-31, 2026-06-30],
        // previous 30d window [2026-05-01, 2026-05-31). One shipped issue in
        // each window with different FTR outcomes; both rates must be returned
        // so a consumer can derive the percentage-point delta in a single read.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-both-{Guid.NewGuid():N}", Name = "Quality Both Windows" };

        var current = SeedIssue(db, project, "issue_quality_both_current", workflowRunId: "wr_quality_both_current", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, current, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 14, 14, 0, 0, TimeSpan.Zero), "wr_quality_both_current");
        await SeedWorkflowRunAsync(db, "wr_quality_both_current", QualityRunState("wr_quality_both_current", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-repair", "Build repair", 1)]),
        ]));

        var previous = SeedIssue(db, project, "issue_quality_both_previous", workflowRunId: "wr_quality_both_previous", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, previous, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 5, 20, 14, 0, 0, TimeSpan.Zero), "wr_quality_both_previous");
        await SeedWorkflowRunAsync(db, "wr_quality_both_previous", QualityRunState("wr_quality_both_previous", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-ok", "Build ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(0.0, result.Window.FirstTimeRightRate);
        Assert.Equal(1, result.PreviousWindow.SampleCount);
        Assert.Equal(1.0, result.PreviousWindow.FirstTimeRightRate);
        Assert.Equal(
            1.0,
            result.PreviousWindow.FirstTimeRightRate!.Value - result.Window.FirstTimeRightRate!.Value,
            precision: 5);
    }

    [Fact]
    public async Task GetQualityAsync_PreviousWindowEmptyAndGenuineRates_AreDistinct()
    {
        // Project A ships only in the current window → the previous window is
        // empty (SampleCount 0, null rate). Project B ships two issues in the
        // previous window (one repaired, one clean) → a genuine 0.5 rate with
        // SampleCount 2. The genuine rate must be distinguishable from empty.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

        var projectA = new ProjectInfo { Id = $"proj-quality-prev-empty-{Guid.NewGuid():N}", Name = "Quality Empty Previous" };
        var issueA = SeedIssue(db, projectA, "issue_quality_prev_empty", workflowRunId: "wr_quality_prev_empty", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issueA, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_prev_empty");
        await SeedWorkflowRunAsync(db, "wr_quality_prev_empty", QualityRunState("wr_quality_prev_empty", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-ok", "Build ok", 0)]),
        ]));

        var projectB = new ProjectInfo { Id = $"proj-quality-prev-genuine-{Guid.NewGuid():N}", Name = "Quality Genuine Previous" };
        var issueB1 = SeedIssue(db, projectB, "issue_quality_prev_zero", workflowRunId: "wr_quality_prev_zero", status: IssueStatus.Done);
        var issueB2 = SeedIssue(db, projectB, "issue_quality_prev_one", workflowRunId: "wr_quality_prev_one", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issueB1, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 5, 5, 14, 0, 0, TimeSpan.Zero), "wr_quality_prev_zero");
        await SeedWorkflowRunAsync(db, "wr_quality_prev_zero", QualityRunState("wr_quality_prev_zero", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-repair", "Build repair", 1)]),
        ]));
        SeedEvent(db, issueB2, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 5, 20, 14, 0, 0, TimeSpan.Zero), "wr_quality_prev_one");
        await SeedWorkflowRunAsync(db, "wr_quality_prev_one", QualityRunState("wr_quality_prev_one", [
            ("plan", [("plan-ok", "Plan ok", 0)]),
            ("build", [("build-ok", "Build ok", 0)]),
        ]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();

        var empty = await service.GetQualityAsync(projectA.Id, now);
        Assert.Equal(1, empty.Window.SampleCount);
        Assert.Equal(1.0, empty.Window.FirstTimeRightRate);
        Assert.Equal(0, empty.PreviousWindow.SampleCount);
        Assert.Null(empty.PreviousWindow.FirstTimeRightRate);

        var genuine = await service.GetQualityAsync(projectB.Id, now);
        Assert.Equal(0, genuine.Window.SampleCount);
        Assert.Equal(2, genuine.PreviousWindow.SampleCount);
        Assert.Equal(0.5, genuine.PreviousWindow.FirstTimeRightRate!.Value, precision: 5);
    }
}
