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
public class IssueStageDurationAggregationQuerierTests
{
    private readonly MohistDbFixture _fixture;

    public IssueStageDurationAggregationQuerierTests(MohistDbFixture fixture)
    {
        _fixture = fixture;
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, firstWorkStart, workflowRunId: workflowRunId);
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
                    Tasks = new[] { new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/acp-agent" } },
                    Checks = new[] { new { Name = "plan-ok", Title = "Plan ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" } },
                    ApprovalStatus = new { Result = "approved", RequestedAt = requestedAt.ToString("O"), RespondedAt = respondedAt.ToString("O") },
                },
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
        SeedEvent(db, issueA.Id, EventCatalog.ReverseDns.IssueWorkStarted, completedAtBase.AddDays(-2).AddHours(-10), workflowRunId: "wr_sd_weighted_a");
        SeedEvent(db, issueB.Id, EventCatalog.ReverseDns.IssueWorkStarted, completedAtBase.AddDays(-1).AddHours(-20), workflowRunId: "wr_sd_weighted_b");
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
        SeedEvent(db, issueA.Id, EventCatalog.ReverseDns.IssueWorkStarted, completedAtBase.AddDays(-2).AddHours(-10), workflowRunId: "wr_sd_wait_a");
        SeedEvent(db, issueB.Id, EventCatalog.ReverseDns.IssueWorkStarted, completedAtBase.AddDays(-1).AddHours(-10), workflowRunId: "wr_sd_wait_b");
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
                    Tasks = new[] { new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/acp-agent" } },
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
                    Tasks = new[] { new { Id = "build", DefinitionId = "build", Attempt = 1, Title = "Plan task", Status = "Completed", Uses = "mohist/acp-agent" } },
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, completedAt.AddHours(-3), workflowRunId: "wr_sd_invalid_wait");
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

}
