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
public class IssueStageDurationAttemptsQuerierTests
{
    private readonly MohistDbFixture _fixture;

    public IssueStageDurationAttemptsQuerierTests(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetStageDurationsAsync_MultiRunLatestAttempt_UsesLastAttemptPerStage()
    {
        // A re-attempted stage uses the latest attempt, not the earlier
        // one. The `build` stage is attempted twice on the same run;
        // only the later (started hour 10, completed hour 12) attempt
        // contributes.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-multirun-{Guid.NewGuid():N}", Name = "Stage Duration MultiRun" };
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var completedAt = new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_sd_multirun",
            createdAt: createdAt,
            completedAt: completedAt,
            workflowRunId: "wr_sd_multirun");
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_multirun");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_sd_multirun", ApprovalRunState("wr_sd_multirun", requestedAt: new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero), wait: TimeSpan.Zero));
        SeedWorkflowRunEvent(db, "wr_sd_multirun", 1, EventCatalog.ReverseDns.StageStarted, new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero), new { stage = "build" });
        SeedWorkflowRunEvent(db, "wr_sd_multirun", 2, EventCatalog.ReverseDns.StageCompleted, new DateTimeOffset(2026, 6, 3, 13, 0, 0, TimeSpan.Zero), new { stage = "build" });
        // Earlier 3h attempt (started hour 1, completed hour 4) is
        // superseded by the later 2h attempt (started hour 10, completed
        // hour 12) — the surface takes the LATEST StageStarted, not the
        // average of the two.
        SeedWorkflowRunEvent(db, "wr_sd_multirun", 3, EventCatalog.ReverseDns.StageStarted, new DateTimeOffset(2026, 6, 3, 15, 0, 0, TimeSpan.Zero), new { stage = "build" });
        SeedWorkflowRunEvent(db, "wr_sd_multirun", 4, EventCatalog.ReverseDns.StageCompleted, new DateTimeOffset(2026, 6, 3, 17, 0, 0, TimeSpan.Zero), new { stage = "build" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetStageDurationsAsync(project.Id, now);

        var buildStage = Assert.Single(result.Stages, s => s.Stage == "build");
        Assert.Equal(1, buildStage.SampleCount);
        Assert.NotNull(buildStage.AverageSeconds);
        // 2h latest attempt (15:00 → 17:00), not the average (3h) nor
        // the sum (5h) of the two attempts.
        Assert.Equal(2 * 3600, buildStage.AverageSeconds!.Value, precision: 3);
        Assert.NotNull(buildStage.MedianSeconds);
        Assert.Equal(2 * 3600, buildStage.MedianSeconds!.Value, precision: 3);
    }

    [Fact]
    public async Task GetStageDurationsAsync_CrossRunLatestPair_TakesLatestFromMostRecentRun()
    {
        // An issue may have multiple workflow runs (a `rerun` /
        // `rerun-from-stage` produces additional runs). The latest attempt
        // is taken across the issue's full run history.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-crossrun-{Guid.NewGuid():N}", Name = "Stage Duration CrossRun" };
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var completedAt = new DateTime(2026, 6, 10, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_sd_crossrun",
            createdAt: createdAt,
            completedAt: completedAt,
            workflowRunId: "wr_sd_second");
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_first");
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 6, 10, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_second");
        await db.SaveChangesAsync();

        // First run: plan takes 1h (started 10:00, completed 11:00).
        await SeedWorkflowRunAsync(db, "wr_sd_first", ApprovalRunState("wr_sd_first", requestedAt: new DateTimeOffset(2026, 6, 3, 9, 0, 0, TimeSpan.Zero), wait: TimeSpan.Zero));
        SeedWorkflowRunEvent(db, "wr_sd_first", 1, EventCatalog.ReverseDns.StageStarted, new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_first", 2, EventCatalog.ReverseDns.StageCompleted, new DateTimeOffset(2026, 6, 3, 11, 0, 0, TimeSpan.Zero), new { stage = "plan" });

        // Second run: plan takes 0.5h (started 14:00, completed 14:30).
        // The latest plan attempt comes from this run.
        await SeedWorkflowRunAsync(db, "wr_sd_second", ApprovalRunState("wr_sd_second", requestedAt: new DateTimeOffset(2026, 6, 6, 9, 0, 0, TimeSpan.Zero), wait: TimeSpan.Zero));
        SeedWorkflowRunEvent(db, "wr_sd_second", 1, EventCatalog.ReverseDns.StageStarted, new DateTimeOffset(2026, 6, 6, 14, 0, 0, TimeSpan.Zero), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_second", 2, EventCatalog.ReverseDns.StageCompleted, new DateTimeOffset(2026, 6, 6, 14, 30, 0, TimeSpan.Zero), new { stage = "plan" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetStageDurationsAsync(project.Id, now);

        var planStage = Assert.Single(result.Stages, s => s.Stage == "plan");
        Assert.Equal(1, planStage.SampleCount);
        Assert.NotNull(planStage.AverageSeconds);
        Assert.Equal(0.5 * 3600, planStage.AverageSeconds!.Value, precision: 3);
    }

    [Fact]
    public async Task GetStageDurationsAsync_CrossRunApprovalWait_CountsEarlierRunGate()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-crossrun-wait-{Guid.NewGuid():N}", Name = "Stage Duration CrossRun Wait" };
        var completedAt = new DateTime(2026, 6, 10, 14, 0, 0, DateTimeKind.Utc);
        var workStartedAt = completedAt.AddHours(-10);
        var priorRunId = "wr_sd_crossrun_wait_prior";
        var currentRunId = "wr_sd_crossrun_wait_current";
        var issue = SeedDeliveredIssue(
            db, project, "issue_sd_crossrun_wait",
            createdAt: completedAt.AddDays(-5),
            completedAt: completedAt,
            workflowRunId: currentRunId);
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, workStartedAt, workflowRunId: priorRunId);
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, completedAt, workflowRunId: currentRunId);
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, priorRunId, ApprovalRunState(priorRunId, workStartedAt, TimeSpan.FromHours(1)));
        SeedWorkflowRunEvent(db, priorRunId, 1, EventCatalog.ReverseDns.StageStarted, workStartedAt, new { stage = "plan" });
        SeedWorkflowRunEvent(db, priorRunId, 2, EventCatalog.ReverseDns.StageCompleted, workStartedAt.AddHours(3), new { stage = "plan" });

        await SeedWorkflowRunAsync(db, currentRunId, new
        {
            Id = currentRunId,
            Metadata = new { CreatedAt = workStartedAt.AddHours(3).AddMinutes(-5), Name = "test" },
            Status = "Completed",
            CurrentStageId = "build",
            Stages = new object[]
            {
                new
                {
                    Id = "build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = "Completed",
                    Tasks = new[] { new { Id = "build-task", DefinitionId = "build-task", Attempt = 1, Title = "Build task", Status = "Completed", Uses = "mohist/acp-agent" } },
                    Checks = new object[0],
                },
            },
        });
        SeedWorkflowRunEvent(db, currentRunId, 1, EventCatalog.ReverseDns.StageStarted, workStartedAt.AddHours(3), new { stage = "build" });
        SeedWorkflowRunEvent(db, currentRunId, 2, EventCatalog.ReverseDns.StageCompleted, workStartedAt.AddHours(7), new { stage = "build" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetStageDurationsAsync(project.Id, new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));

        Assert.NotNull(result.FlowEfficiencyRatio);
        Assert.Equal(0.2, result.FlowEfficiencyRatio!.Value, precision: 3);
        Assert.NotNull(result.WaitBreakout);
        Assert.NotNull(result.WaitBreakout!.AverageApprovalGateWaitSeconds);
        Assert.Equal(3600, result.WaitBreakout.AverageApprovalGateWaitSeconds!.Value, precision: 3);
        Assert.NotNull(result.WaitBreakout.AverageInactiveGapSeconds);
        Assert.Equal(7 * 3600, result.WaitBreakout.AverageInactiveGapSeconds!.Value, precision: 3);
    }

    [Fact]
    public async Task GetStageDurationsAsync_RunIdOnlyOnWorkCompleted_DiscoversStageEvents()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-completed-run-{Guid.NewGuid():N}", Name = "Stage Duration Completed Run" };
        var completedAt = new DateTime(2026, 6, 10, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_sd_completed_run",
            createdAt: new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: completedAt);
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, completedAt.AddHours(-4));
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, completedAt, workflowRunId: "wr_sd_completed_run");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_sd_completed_run", ApprovalRunState("wr_sd_completed_run", completedAt.AddHours(-4), TimeSpan.Zero));
        SeedWorkflowRunEvent(db, "wr_sd_completed_run", 1, EventCatalog.ReverseDns.StageStarted, completedAt.AddHours(-3), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_completed_run", 2, EventCatalog.ReverseDns.StageCompleted, completedAt.AddHours(-1), new { stage = "plan" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetStageDurationsAsync(project.Id, new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));

        var planStage = Assert.Single(result.Stages, s => s.Stage == "plan");
        Assert.Equal(1, planStage.SampleCount);
        Assert.Equal(2 * 3600, planStage.AverageSeconds!.Value, precision: 3);
    }

    [Fact]
    public async Task GetStageDurationsAsync_DuplicateCompletion_UsesFirstCompletionAfterLatestStart()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-duplicate-complete-{Guid.NewGuid():N}", Name = "Stage Duration Duplicate Complete" };
        var completedAt = new DateTime(2026, 6, 10, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_sd_duplicate_complete",
            createdAt: new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: completedAt,
            workflowRunId: "wr_sd_duplicate_complete");
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, completedAt.AddHours(-6), workflowRunId: "wr_sd_duplicate_complete");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_sd_duplicate_complete", ApprovalRunState("wr_sd_duplicate_complete", completedAt.AddHours(-6), TimeSpan.Zero));
        SeedWorkflowRunEvent(db, "wr_sd_duplicate_complete", 1, EventCatalog.ReverseDns.StageStarted, completedAt.AddHours(-5), new { stage = "build" });
        SeedWorkflowRunEvent(db, "wr_sd_duplicate_complete", 2, EventCatalog.ReverseDns.StageCompleted, completedAt.AddHours(-3), new { stage = "build" });
        SeedWorkflowRunEvent(db, "wr_sd_duplicate_complete", 3, EventCatalog.ReverseDns.StageCompleted, completedAt.AddHours(-1), new { stage = "build" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetStageDurationsAsync(project.Id, new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));

        var buildStage = Assert.Single(result.Stages, s => s.Stage == "build");
        Assert.Equal(2 * 3600, buildStage.AverageSeconds!.Value, precision: 3);
    }

    [Fact]
    public async Task GetStageDurationsAsync_StartedButNeverCompleted_ExcludedFromAverage()
    {
        // A started-but-never-completed latest attempt yields an
        // undefined stage duration: that stage contributes no defined
        // sample for that issue and is excluded from avg / median /
        // count. The other stage with a defined duration still
        // aggregates normally.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-undef-{Guid.NewGuid():N}", Name = "Stage Duration Undefined" };
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var completedAt = new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_sd_undef",
            createdAt: createdAt,
            completedAt: completedAt,
            workflowRunId: "wr_sd_undef");
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_undef");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_sd_undef", ApprovalRunState("wr_sd_undef", requestedAt: new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero), wait: TimeSpan.Zero));
        // `plan` has a defined duration (2h).
        SeedWorkflowRunEvent(db, "wr_sd_undef", 1, EventCatalog.ReverseDns.StageStarted, new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_undef", 2, EventCatalog.ReverseDns.StageCompleted, new DateTimeOffset(2026, 6, 3, 12, 0, 0, TimeSpan.Zero), new { stage = "plan" });
        // `build` started but never completed — undefined duration.
        SeedWorkflowRunEvent(db, "wr_sd_undef", 3, EventCatalog.ReverseDns.StageStarted, new DateTimeOffset(2026, 6, 3, 13, 0, 0, TimeSpan.Zero), new { stage = "build" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetStageDurationsAsync(project.Id, now);

        var planStage = Assert.Single(result.Stages, s => s.Stage == "plan");
        Assert.Equal(1, planStage.SampleCount);
        Assert.NotNull(planStage.AverageSeconds);
        Assert.Equal(2 * 3600, planStage.AverageSeconds!.Value, precision: 3);

        Assert.DoesNotContain(result.Stages, s => s.Stage == "build");
    }

}
