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
public class IssueMetricsStageDurationsSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueMetricsStageDurationsSpecs(MohistDbFixture fixture)
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
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_multirun");
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
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_first");
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 6, 10, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_second");
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
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, workStartedAt, workflowRunId: priorRunId);
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, completedAt, workflowRunId: currentRunId);
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
                    Tasks = new[] { new { Id = "build-task", DefinitionId = "build-task", Attempt = 1, Title = "Build task", Status = "Completed", Uses = "mohist/opencode" } },
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
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, completedAt.AddHours(-4));
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, completedAt, workflowRunId: "wr_sd_completed_run");
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
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, completedAt.AddHours(-6), workflowRunId: "wr_sd_duplicate_complete");
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
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_undef");
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

    [Fact]
    public async Task GetStageDurationsAsync_SumToCycleDecomposition_HoldsForDeliveredIssue()
    {
        // Spec D6: activeWork + approvalGateWait + inactiveGap == cycleTime
        // per delivered issue; pending approvals contribute nothing;
        // issues with no approval gates have zero approval-gate wait.
        // Layout: cycle = 10h, stage spans = 7h, approval wait = 1h.
        // Expected: activeWork = 6h, inactiveGap = 3h, wait = 1h.
        // Sum = 6 + 1 + 3 = 10.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-decompose-{Guid.NewGuid():N}", Name = "Stage Duration Decompose" };
        var requestedAt = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var respondedAt = requestedAt + TimeSpan.FromHours(1);
        var issueId = $"issue_sd_decompose_{Guid.NewGuid():N}";
        var workflowRunId = $"wr_sd_decompose_{Guid.NewGuid():N}";
        var createdAt = requestedAt.UtcDateTime.AddHours(-1);
        var completedAt = new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc);
        var firstWorkStart = completedAt.AddHours(-10);

        // Seed delivered issue with cycle 10h.
        var issue = SeedDeliveredIssue(db, project, issueId,
            createdAt: createdAt,
            completedAt: completedAt,
            workflowRunId: workflowRunId);
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, firstWorkStart, workflowRunId: workflowRunId);
        await db.SaveChangesAsync();

        // Stage spans = 7h (plan 3h + build 4h).
        await SeedWorkflowRunAsync(db, workflowRunId, new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = firstWorkStart.AddMinutes(-5), Name = "test" },
            Status = "Completed",
            CurrentStageId = "build",
            Stages = new object[]
            {
                new
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "Completed",
                    Tasks = new[] { new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/opencode" } },
                    Checks = new[] { new { Name = "plan-ok", Title = "Plan ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" } },
                    ApprovalStatus = new { Result = "approved", RequestedAt = requestedAt.ToString("O"), RespondedAt = respondedAt.ToString("O") },
                },
                new
                {
                    Id = "build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = "Completed",
                    Tasks = new[] { new { Id = "build-task", DefinitionId = "build-task", Attempt = 1, Title = "Build task", Status = "Completed", Uses = "mohist/opencode" } },
                    Checks = new object[0],
                },
            },
        });
        SeedWorkflowRunEvent(db, workflowRunId, 1, EventCatalog.ReverseDns.StageStarted, firstWorkStart, new { stage = "plan" });
        SeedWorkflowRunEvent(db, workflowRunId, 2, EventCatalog.ReverseDns.StageCompleted, firstWorkStart.AddHours(3), new { stage = "plan" });
        SeedWorkflowRunEvent(db, workflowRunId, 3, EventCatalog.ReverseDns.StageStarted, firstWorkStart.AddHours(3), new { stage = "build" });
        SeedWorkflowRunEvent(db, workflowRunId, 4, EventCatalog.ReverseDns.StageCompleted, firstWorkStart.AddHours(7), new { stage = "build" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetStageDurationsAsync(project.Id, now);

        // 1 delivered issue with cycle = 10h.
        Assert.NotNull(result.FlowEfficiencyRatio);
        // Σ activeWork / Σ cycle = 6/10 = 0.6
        Assert.Equal(0.6, result.FlowEfficiencyRatio!.Value, precision: 3);

        Assert.NotNull(result.WaitBreakout);
        Assert.NotNull(result.WaitBreakout!.AverageApprovalGateWaitSeconds);
        Assert.Equal(3600, result.WaitBreakout!.AverageApprovalGateWaitSeconds!.Value, precision: 3);
        // inactiveGap = 3h (cycle 10 - stages 7)
        Assert.NotNull(result.WaitBreakout.AverageInactiveGapSeconds);
        Assert.Equal(3 * 3600, result.WaitBreakout.AverageInactiveGapSeconds!.Value, precision: 3);
    }

    [Fact]
    public async Task GetStageDurationsAsync_PopulationWeightedRatio_NotArithmeticMean()
    {
        // Spec: ratio is Σ activeWork / Σ cycle (population weighted),
        // not the arithmetic mean of per-issue ratios.
        // Issue A: cycle 10h, activeWork = 7h (no approval wait).
        // Issue B: cycle 20h, activeWork = 5h.
        // Σ activeWork / Σ cycle = (7 + 5) / (10 + 20) = 12/30 = 0.4.
        // Arithmetic mean of per-issue ratios = (0.7 + 0.25) / 2 = 0.475.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-weighted-{Guid.NewGuid():N}", Name = "Stage Duration Weighted" };
        var completedAtBase = new DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc);
        var issueA = SeedDeliveredIssue(db, project, "issue_sd_weighted_a",
            createdAt: completedAtBase.AddDays(-15),
            completedAt: completedAtBase.AddDays(-2),
            workflowRunId: "wr_sd_weighted_a");
        var issueB = SeedDeliveredIssue(db, project, "issue_sd_weighted_b",
            createdAt: completedAtBase.AddDays(-25),
            completedAt: completedAtBase.AddDays(-1),
            workflowRunId: "wr_sd_weighted_b");
        SeedEvent(db, issueA, EventCatalog.ReverseDns.IssueWorkStarted, completedAtBase.AddDays(-2).AddHours(-10), workflowRunId: "wr_sd_weighted_a");
        SeedEvent(db, issueB, EventCatalog.ReverseDns.IssueWorkStarted, completedAtBase.AddDays(-1).AddHours(-20), workflowRunId: "wr_sd_weighted_b");
        await db.SaveChangesAsync();

        var wrA = "wr_sd_weighted_a";
        await SeedWorkflowRunAsync(db, wrA, ApprovalRunState(wrA, requestedAt: completedAtBase.AddDays(-15), wait: TimeSpan.Zero));
        // Issue A: stage spans 7h, cycle 10h → activeWork 7h (no wait).
        SeedWorkflowRunEvent(db, wrA, 1, EventCatalog.ReverseDns.StageStarted, completedAtBase.AddDays(-2).AddHours(-10), new { stage = "plan" });
        SeedWorkflowRunEvent(db, wrA, 2, EventCatalog.ReverseDns.StageCompleted, completedAtBase.AddDays(-2).AddHours(-3), new { stage = "plan" });

        var wrB = "wr_sd_weighted_b";
        await SeedWorkflowRunAsync(db, wrB, ApprovalRunState(wrB, requestedAt: completedAtBase.AddDays(-25), wait: TimeSpan.Zero));
        // Issue B: stage spans 5h, cycle 20h → activeWork 5h (no wait).
        SeedWorkflowRunEvent(db, wrB, 1, EventCatalog.ReverseDns.StageStarted, completedAtBase.AddDays(-1).AddHours(-20), new { stage = "plan" });
        SeedWorkflowRunEvent(db, wrB, 2, EventCatalog.ReverseDns.StageCompleted, completedAtBase.AddDays(-1).AddHours(-15), new { stage = "plan" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetStageDurationsAsync(project.Id, completedAtBase);

        Assert.NotNull(result.FlowEfficiencyRatio);
        // Population-weighted ratio: (7 + 5) / (10 + 20) = 12 / 30 = 0.4.
        Assert.Equal(12.0 / 30.0, result.FlowEfficiencyRatio!.Value, precision: 3);
    }

    [Fact]
    public async Task GetStageDurationsAsync_WaitBreakoutAverages_ZeroWaitContributesZero()
    {
        // An issue with no wait contributes zero to the averages (not
        // exclusion). Wait breakout averages are over the same
        // population as the ratio.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-wait-{Guid.NewGuid():N}", Name = "Stage Duration Wait Breakout" };
        var completedAtBase = new DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc);
        // Two issues, identical 10h cycles. Issue A: 1h approval wait.
        // Issue B: no approval (zero wait). Average approval wait = (1 + 0) / 2 = 0.5h.
        var issueA = SeedDeliveredIssue(db, project, "issue_sd_wait_a",
            createdAt: completedAtBase.AddDays(-12),
            completedAt: completedAtBase.AddDays(-2),
            workflowRunId: "wr_sd_wait_a");
        var issueB = SeedDeliveredIssue(db, project, "issue_sd_wait_b",
            createdAt: completedAtBase.AddDays(-12),
            completedAt: completedAtBase.AddDays(-1),
            workflowRunId: "wr_sd_wait_b");
        SeedEvent(db, issueA, EventCatalog.ReverseDns.IssueWorkStarted, completedAtBase.AddDays(-2).AddHours(-10), workflowRunId: "wr_sd_wait_a");
        SeedEvent(db, issueB, EventCatalog.ReverseDns.IssueWorkStarted, completedAtBase.AddDays(-1).AddHours(-10), workflowRunId: "wr_sd_wait_b");
        await db.SaveChangesAsync();

        var wrA = "wr_sd_wait_a";
        await SeedWorkflowRunAsync(db, wrA, new
        {
            Id = wrA,
            Metadata = new { CreatedAt = completedAtBase.AddDays(-12), Name = "test" },
            Status = "Completed",
            CurrentStageId = "plan",
            Stages = new[]
            {
                new
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "Completed",
                    Tasks = new[] { new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/opencode" } },
                    Checks = new[] { new { Name = "plan-ok", Title = "Plan ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" } },
                    ApprovalStatus = new
                    {
                        Result = "approved",
                        RequestedAt = completedAtBase.AddDays(-2).AddHours(-10).ToString("O"),
                        RespondedAt = completedAtBase.AddDays(-2).AddHours(-9).ToString("O"),
                    },
                },
            },
        });
        SeedWorkflowRunEvent(db, wrA, 1, EventCatalog.ReverseDns.StageStarted, completedAtBase.AddDays(-2).AddHours(-10), new { stage = "plan" });
        SeedWorkflowRunEvent(db, wrA, 2, EventCatalog.ReverseDns.StageCompleted, completedAtBase.AddDays(-2), new { stage = "plan" });

        var wrB = "wr_sd_wait_b";
        // Issue B has no approval gate.
        await SeedWorkflowRunAsync(db, wrB, new
        {
            Id = wrB,
            Metadata = new { CreatedAt = completedAtBase.AddDays(-12), Name = "test" },
            Status = "Completed",
            CurrentStageId = "plan",
            Stages = new object[]
            {
                new
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = "Completed",
                    Tasks = new[] { new { Id = "build", DefinitionId = "build", Attempt = 1, Title = "Plan task", Status = "Completed", Uses = "mohist/opencode" } },
                    Checks = new object[0],
                },
            },
        });
        SeedWorkflowRunEvent(db, wrB, 1, EventCatalog.ReverseDns.StageStarted, completedAtBase.AddDays(-1).AddHours(-10), new { stage = "plan" });
        SeedWorkflowRunEvent(db, wrB, 2, EventCatalog.ReverseDns.StageCompleted, completedAtBase.AddDays(-1), new { stage = "plan" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetStageDurationsAsync(project.Id, completedAtBase);

        Assert.NotNull(result.WaitBreakout);
        Assert.NotNull(result.WaitBreakout!.AverageApprovalGateWaitSeconds);
        // Average over 2 issues: (1h + 0h) / 2 = 0.5h.
        Assert.Equal(0.5 * 3600, result.WaitBreakout!.AverageApprovalGateWaitSeconds!.Value, precision: 3);
        Assert.NotNull(result.WaitBreakout.AverageInactiveGapSeconds);
    }

    [Fact]
    public async Task GetStageDurationsAsync_ApprovalWaitGreaterThanStageSpan_ExcludesIssueFromCycleAggregates()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-sd-invalid-wait-{Guid.NewGuid():N}", Name = "Stage Duration Invalid Wait" };
        var completedAt = new DateTime(2026, 6, 10, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_sd_invalid_wait",
            createdAt: completedAt.AddDays(-2),
            completedAt: completedAt,
            workflowRunId: "wr_sd_invalid_wait");
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, completedAt.AddHours(-3), workflowRunId: "wr_sd_invalid_wait");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_sd_invalid_wait", ApprovalRunState("wr_sd_invalid_wait", completedAt.AddHours(-3), TimeSpan.FromHours(2)));
        SeedWorkflowRunEvent(db, "wr_sd_invalid_wait", 1, EventCatalog.ReverseDns.StageStarted, completedAt.AddHours(-2), new { stage = "plan" });
        SeedWorkflowRunEvent(db, "wr_sd_invalid_wait", 2, EventCatalog.ReverseDns.StageCompleted, completedAt.AddHours(-1), new { stage = "plan" });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetStageDurationsAsync(project.Id, new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));

        Assert.Single(result.Stages, s => s.Stage == "plan");
        Assert.Null(result.FlowEfficiencyRatio);
        Assert.NotNull(result.WaitBreakout);
        Assert.Null(result.WaitBreakout!.AverageApprovalGateWaitSeconds);
        Assert.Null(result.WaitBreakout.AverageInactiveGapSeconds);
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
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, completedAt.AddHours(-1), workflowRunId: "wr_sd_invalid_stage");
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
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(zeroMoment, TimeSpan.Zero), workflowRunId: "wr_sd_zero");
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
        // Membership is keyed on completion time within the fixed (not
        // caller-configurable) trailing window shared with delivery-time.
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
        SeedEvent(db, inside, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_window_inside");
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
        SeedEvent(db, outside, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 5, 19, 9, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_window_outside");
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
        SeedEvent(db, insideA, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(completedAt.AddHours(-2), TimeSpan.Zero), workflowRunId: "wr_sd_scope_a");
        SeedEvent(db, insideB, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(completedAt.AddHours(-4), TimeSpan.Zero), workflowRunId: "wr_sd_scope_b");
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
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, completedAt.AddHours(-10), workflowRunId: "wr_sd_order");
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
