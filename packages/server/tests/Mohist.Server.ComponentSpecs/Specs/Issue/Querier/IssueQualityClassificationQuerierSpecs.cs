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
public class IssueQualityClassificationQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueQualityClassificationQuerierSpecs(MohistDbFixture fixture)
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_ftr_1");
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_rework_1");
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

        SeedEvent(db, shipped.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_status_shipped");
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_stage_1");
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

        SeedEvent(db, recent.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-3), "wr_quality_win_recent");
        SeedEvent(db, mid.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-20), "wr_quality_win_mid");
        SeedEvent(db, old.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-40), "wr_quality_win_old");

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

        SeedEvent(db, reachedIntegrate.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_denom_integrate");
        SeedEvent(db, onlyPlan.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_denom_plan");

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

}
