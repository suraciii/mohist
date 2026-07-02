using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SystemTimeProvider = System.TimeProvider;
using Mohist.Server.Events.Hosting;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Project;
using Mohist.Server.Infrastructure.Data.StagePopulation;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Tests.Specs.Events;

/// <summary>
/// Covers the daily stage-population snapshot job:
/// <list type="bullet">
/// <item><description>per-project-per-day persistence,</description></item>
/// <item><description>idempotent writes on retry,</description></item>
/// <item><description>no backfill before the day the snapshot is run,</description></item>
/// <item><description>attribution across backlog / in-flight / done /
/// cancelled.</description></item>
/// </list>
/// Spec: <c>openspec/changes/issue-297/specs/stage-population-snapshot/spec.md</c>.
/// <para>
/// Each test stands up an isolated in-memory SQLite and a fresh snapshot
/// service — the sweep walks every project in the database, so
/// whole-DB assertions (no projects / exactly two rows) require per-test
/// isolation rather than the shared collection fixture.
/// </para>
/// </summary>
public class StagePopulationSnapshotServiceSpecs
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 1, 6, 0, 0, TimeSpan.Zero);
    private static readonly string FixedDay = "2026-07-01";

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_NoProjects_WritesNoRows()
    {
        await using var scope = SnapshotTestScope.Create();
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        Assert.Empty(rows);
        var stored = await scope.DbContext.StagePopulationSnapshots.CountAsync();
        Assert.Equal(0, stored);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_ProjectWithNoIssues_StillWritesRow()
    {
        // A project with zero issues still gets a row — the snapshot
        // is one-per-project-per-day, the counts are all zero.
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_empty");
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal("project_empty", row.ProjectId);
        Assert.Equal(FixedDay, row.Day);
        Assert.Equal(0, row.Backlog);
        Assert.Equal(0, row.Done);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_BacklogIssue_IncrementsBacklogCount()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_backlog");
        await SeedIssueAsync(scope.DbContext, "project_backlog", "issue_backlog", number: 1,
            status: IssueStatus.Backlog);
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.Backlog);
        Assert.Equal(0, row.Done);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_DoneIssue_IncrementsDoneCount()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_done");
        await SeedDoneIssueAsync(scope.DbContext, "project_done", "issue_done", number: 1,
            completedAt: FixedNow.AddHours(-1));
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.Backlog);
        Assert.Equal(1, row.Done);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_CancelledIssue_IsExcludedFromAllBuckets()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_cancelled");
        await SeedCancelledIssueAsync(scope.DbContext, "project_cancelled", "issue_cancelled", number: 1,
            closedAt: FixedNow.AddHours(-2));
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.Backlog);
        Assert.Equal(0, row.Done);
        Assert.Equal(0, row.Plan);
        Assert.Equal(0, row.Build);
        Assert.Equal(0, row.Check);
        Assert.Equal(0, row.Integrate);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_InFlightIssue_AttributesToLatestStageStarted()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_inflight");
        await SeedInFlightIssueAsync(scope.DbContext, "project_inflight", "issue_inflight", number: 1,
            workStartedAt: FixedNow.AddHours(-4),
            currentStage: "build",
            currentStageStartedAt: FixedNow.AddHours(-3));
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.Backlog);
        Assert.Equal(0, row.Done);
        Assert.Equal(1, row.Build);
        Assert.Equal(0, row.Plan);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_MultiStageInFlight_AttributesToLatestStage()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_multi");
        var issue = await SeedIssueAsync(scope.DbContext, "project_multi", "issue_multi", number: 1,
            status: IssueStatus.InProgress);
        var wrId = "wr_multi";
        await SeedWorkflowRunAsync(scope.DbContext, wrId, ApprovalRunState(wrId,
            requestedAt: FixedNow.AddHours(-10), wait: TimeSpan.Zero));
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-10), workflowRunId: wrId);
        SeedWorkflowRunEvent(scope.DbContext, wrId, 1, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-10), new { stage = "plan" });
        SeedWorkflowRunEvent(scope.DbContext, wrId, 2, EventCatalog.ReverseDns.StageCompleted,
            FixedNow.AddHours(-9), new { stage = "plan" });
        SeedWorkflowRunEvent(scope.DbContext, wrId, 3, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-8), new { stage = "build" });
        SeedWorkflowRunEvent(scope.DbContext, wrId, 4, EventCatalog.ReverseDns.StageCompleted,
            FixedNow.AddHours(-7), new { stage = "build" });
        SeedWorkflowRunEvent(scope.DbContext, wrId, 5, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-6), new { stage = "check" });
        await scope.DbContext.SaveChangesAsync();
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.Plan);
        Assert.Equal(0, row.Build);
        Assert.Equal(1, row.Check);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_MixedProject_PopulatesAllSixBuckets()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_mixed");
        await SeedIssueAsync(scope.DbContext, "project_mixed", "issue_bl_1", number: 1, status: IssueStatus.Backlog);
        await SeedIssueAsync(scope.DbContext, "project_mixed", "issue_bl_2", number: 2, status: IssueStatus.Backlog);
        await SeedIssueAsync(scope.DbContext, "project_mixed", "issue_bl_3", number: 3, status: IssueStatus.Backlog);
        await SeedInFlightIssueAsync(scope.DbContext, "project_mixed", "issue_plan", number: 4,
            workStartedAt: FixedNow.AddHours(-4),
            currentStage: "plan",
            currentStageStartedAt: FixedNow.AddHours(-3));
        await SeedInFlightIssueAsync(scope.DbContext, "project_mixed", "issue_build", number: 5,
            workStartedAt: FixedNow.AddHours(-4),
            currentStage: "build",
            currentStageStartedAt: FixedNow.AddHours(-2));
        await SeedInFlightIssueAsync(scope.DbContext, "project_mixed", "issue_check", number: 6,
            workStartedAt: FixedNow.AddHours(-4),
            currentStage: "check",
            currentStageStartedAt: FixedNow.AddHours(-1));
        await SeedInFlightIssueAsync(scope.DbContext, "project_mixed", "issue_integrate", number: 7,
            workStartedAt: FixedNow.AddHours(-4),
            currentStage: "integrate",
            currentStageStartedAt: FixedNow.AddMinutes(-30));
        await SeedDoneIssueAsync(scope.DbContext, "project_mixed", "issue_done_1", number: 8,
            completedAt: FixedNow.AddHours(-6));
        await SeedDoneIssueAsync(scope.DbContext, "project_mixed", "issue_done_2", number: 9,
            completedAt: FixedNow.AddHours(-12));
        await SeedCancelledIssueAsync(scope.DbContext, "project_mixed", "issue_cancelled", number: 10,
            closedAt: FixedNow.AddHours(-7));
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(3, row.Backlog);
        Assert.Equal(1, row.Plan);
        Assert.Equal(1, row.Build);
        Assert.Equal(1, row.Check);
        Assert.Equal(1, row.Integrate);
        Assert.Equal(2, row.Done);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_ReRun_IsIdempotent()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_idem");
        await SeedIssueAsync(scope.DbContext, "project_idem", "issue_bl", number: 1, status: IssueStatus.Backlog);
        await SeedInFlightIssueAsync(scope.DbContext, "project_idem", "issue_build", number: 2,
            workStartedAt: FixedNow.AddHours(-1),
            currentStage: "build",
            currentStageStartedAt: FixedNow.AddMinutes(-15));
        var service = scope.NewService();

        var first = await service.SnapshotForUtcDayAsync(FixedNow);
        Assert.Single(first);

        var second = await service.SnapshotForUtcDayAsync(FixedNow);
        var secondRow = Assert.Single(second);
        Assert.Equal(first[0].Backlog, secondRow.Backlog);
        Assert.Equal(first[0].Build, secondRow.Build);

        var stored = await scope.DbContext.StagePopulationSnapshots
            .Where(r => r.ProjectId == "project_idem" && r.Day == FixedDay)
            .CountAsync();
        Assert.Equal(1, stored);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_DifferentDay_WritesSecondRow()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_twodays");
        await SeedIssueAsync(scope.DbContext, "project_twodays", "issue_bl", number: 1, status: IssueStatus.Backlog);
        var service = scope.NewService();

        var day1 = await service.SnapshotForUtcDayAsync(FixedNow);
        Assert.Single(day1);
        var day2 = await service.SnapshotForUtcDayAsync(FixedNow.AddDays(1));
        Assert.Single(day2);
        Assert.NotEqual(day1[0].Day, day2[0].Day);

        var stored = await scope.DbContext.StagePopulationSnapshots
            .Where(r => r.ProjectId == "project_twodays")
            .CountAsync();
        Assert.Equal(2, stored);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_TwoProjects_WritesOneRowPerProject()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_alpha");
        await SeedProjectAsync(scope.DbContext, "project_beta");
        await SeedInFlightIssueAsync(scope.DbContext, "project_beta", "issue_b", number: 1,
            workStartedAt: FixedNow.AddHours(-1),
            currentStage: "plan",
            currentStageStartedAt: FixedNow.AddMinutes(-30));
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        Assert.Equal(2, rows.Count);
        var alpha = Assert.Single(rows, r => r.ProjectId == "project_alpha");
        var beta = Assert.Single(rows, r => r.ProjectId == "project_beta");
        Assert.Equal(0, alpha.Backlog);
        Assert.Equal(1, beta.Plan);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_NoBackfill_OnlyWritesDayItRunsFor()
    {
        // Spec: "Snapshots accumulate forward from go-live with no
        // historical backfill" and "no snapshot row is persisted for
        // any day before go-live; history accrues one day at a time".
        // The job only persists the day derived from its `nowUtc`
        // input; a sweep on day D writes a row for D and nothing
        // earlier. Run for an early day, then for the current day;
        // only the days we asked for exist.
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_no_backfill");
        await SeedIssueAsync(scope.DbContext, "project_no_backfill", "issue_bl", number: 1,
            status: IssueStatus.Backlog);
        var service = scope.NewService();

        await service.SnapshotForUtcDayAsync(FixedNow.AddDays(-90));
        await service.SnapshotForUtcDayAsync(FixedNow);

        var storedDays = await scope.DbContext.StagePopulationSnapshots
            .Where(r => r.ProjectId == "project_no_backfill")
            .Select(r => r.Day)
            .ToListAsync();
        storedDays.Sort(StringComparer.Ordinal);
        Assert.Equal(new[] { "2026-04-02", "2026-07-01" }, storedDays);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_DayBoundApplied_ExcludesEventsAfterDay()
    {
        // The day bound is applied in LINQ-to-objects after
        // materialization. WorkStarted events past the day end are
        // excluded, so the issue has no WorkStarted as of the day and
        // is attributed to backlog (the day-bound trumps the issue's
        // current InProgress status, which is a JSON snapshot, not
        // an event).
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_bound");
        var issue = await SeedIssueAsync(scope.DbContext, "project_bound", "issue_bound", number: 1,
            status: IssueStatus.InProgress);
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddDays(2), workflowRunId: "wr_late");
        await scope.DbContext.SaveChangesAsync();
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.Backlog);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_DayBoundExcludesNextMidnight()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_midnight");
        var issue = await SeedIssueAsync(scope.DbContext, "project_midnight", "issue_midnight", number: 1,
            status: IssueStatus.InProgress);
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero), workflowRunId: "wr_midnight");
        await scope.DbContext.SaveChangesAsync();
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.Backlog);
        Assert.Equal(0, row.Plan);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_MultiRunInFlight_AttributesToLatestRunLatestStage()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_multirun");
        var issue = await SeedIssueAsync(scope.DbContext, "project_multirun", "issue_mr", number: 1,
            status: IssueStatus.InProgress);
        var wr1 = "wr_first";
        var wr2 = "wr_second";
        await SeedWorkflowRunAsync(scope.DbContext, wr1, ApprovalRunState(wr1,
            requestedAt: FixedNow.AddHours(-10), wait: TimeSpan.Zero));
        await SeedWorkflowRunAsync(scope.DbContext, wr2, ApprovalRunState(wr2,
            requestedAt: FixedNow.AddHours(-5), wait: TimeSpan.Zero));
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-10), workflowRunId: wr1);
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-5), workflowRunId: wr2);
        SeedWorkflowRunEvent(scope.DbContext, wr1, 1, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-10), new { stage = "plan" });
        SeedWorkflowRunEvent(scope.DbContext, wr1, 2, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-9), new { stage = "build" });
        SeedWorkflowRunEvent(scope.DbContext, wr2, 1, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-5), new { stage = "plan" });
        SeedWorkflowRunEvent(scope.DbContext, wr2, 2, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-4), new { stage = "build" });
        SeedWorkflowRunEvent(scope.DbContext, wr2, 3, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-3), new { stage = "check" });
        await scope.DbContext.SaveChangesAsync();
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.Plan);
        Assert.Equal(0, row.Build);
        Assert.Equal(1, row.Check);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_NewRunBeforeFirstStage_DoesNotCountOldRunStage()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_restart");
        var issue = await SeedIssueAsync(scope.DbContext, "project_restart", "issue_restart", number: 1,
            status: IssueStatus.InProgress);
        var wr1 = "wr_restart_first";
        var wr2 = "wr_restart_second";
        await SeedWorkflowRunAsync(scope.DbContext, wr1, ApprovalRunState(wr1,
            requestedAt: FixedNow.AddHours(-10), wait: TimeSpan.Zero));
        await SeedWorkflowRunAsync(scope.DbContext, wr2, ApprovalRunState(wr2,
            requestedAt: FixedNow.AddHours(-1), wait: TimeSpan.Zero));
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-10), workflowRunId: wr1);
        SeedWorkflowRunEvent(scope.DbContext, wr1, 1, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-9), new { stage = "build" });
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-1), workflowRunId: wr2);
        await scope.DbContext.SaveChangesAsync();
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.Build);
        Assert.Equal(0, row.Plan);
        Assert.Equal(0, row.Check);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_LateOldRunStage_DoesNotOverrideActiveRun()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_late_old");
        var issue = await SeedIssueAsync(scope.DbContext, "project_late_old", "issue_late_old", number: 1,
            status: IssueStatus.InProgress);
        var wr1 = "wr_late_old_first";
        var wr2 = "wr_late_old_second";
        await SeedWorkflowRunAsync(scope.DbContext, wr1, ApprovalRunState(wr1,
            requestedAt: FixedNow.AddHours(-10), wait: TimeSpan.Zero));
        await SeedWorkflowRunAsync(scope.DbContext, wr2, ApprovalRunState(wr2,
            requestedAt: FixedNow.AddHours(-5), wait: TimeSpan.Zero));
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-10), workflowRunId: wr1);
        SeedWorkflowRunEvent(scope.DbContext, wr1, 1, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-9), new { stage = "build" });
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-5), workflowRunId: wr2);
        SeedWorkflowRunEvent(scope.DbContext, wr2, 1, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-4), new { stage = "plan" });
        SeedWorkflowRunEvent(scope.DbContext, wr1, 2, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-3), new { stage = "check" });
        await scope.DbContext.SaveChangesAsync();
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.Plan);
        Assert.Equal(0, row.Check);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_NewRunBeforeReplacementStage_DoesNotCountOldRunStage()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_restart");
        var issue = await SeedIssueAsync(scope.DbContext, "project_restart", "issue_restart", number: 1,
            status: IssueStatus.InProgress);
        var wr1 = "wr_restart_first";
        var wr2 = "wr_restart_second";
        await SeedWorkflowRunAsync(scope.DbContext, wr1, ApprovalRunState(wr1,
            requestedAt: FixedNow.AddHours(-10), wait: TimeSpan.Zero));
        await SeedWorkflowRunAsync(scope.DbContext, wr2, ApprovalRunState(wr2,
            requestedAt: FixedNow.AddHours(-2), wait: TimeSpan.Zero));
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-10), workflowRunId: wr1);
        SeedWorkflowRunEvent(scope.DbContext, wr1, 1, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-9), new { stage = "build" });
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-2), workflowRunId: wr2);
        await scope.DbContext.SaveChangesAsync();
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.Build);
        Assert.Equal(0, row.Plan);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_LateOldRunStageDoesNotOverrideActiveRun()
    {
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_late_old");
        var issue = await SeedIssueAsync(scope.DbContext, "project_late_old", "issue_late_old", number: 1,
            status: IssueStatus.InProgress);
        var wr1 = "wr_late_old_first";
        var wr2 = "wr_late_old_second";
        await SeedWorkflowRunAsync(scope.DbContext, wr1, ApprovalRunState(wr1,
            requestedAt: FixedNow.AddHours(-10), wait: TimeSpan.Zero));
        await SeedWorkflowRunAsync(scope.DbContext, wr2, ApprovalRunState(wr2,
            requestedAt: FixedNow.AddHours(-3), wait: TimeSpan.Zero));
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-10), workflowRunId: wr1);
        SeedWorkflowRunEvent(scope.DbContext, wr1, 1, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-9), new { stage = "build" });
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-3), workflowRunId: wr2);
        SeedWorkflowRunEvent(scope.DbContext, wr2, 1, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-2), new { stage = "plan" });
        SeedWorkflowRunEvent(scope.DbContext, wr1, 2, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-1), new { stage = "check" });
        await scope.DbContext.SaveChangesAsync();
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.Plan);
        Assert.Equal(0, row.Check);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task SnapshotOnceAsync_IssueWithRerunFromStage_AttributesToRerunLatestStage()
    {
        // Rerun-from-stage: the issue progressed through plan / build /
        // check, then a rerun-from-plan restarts from plan on or before
        // the snapshot day, invalidating the later progress. The latest
        // StageStarted as of the day is the rerun-from-plan "build"
        // stage, so the issue is attributed to "build" (the rerun's
        // furthest entered stage), not to the original "check".
        await using var scope = SnapshotTestScope.Create();
        await SeedProjectAsync(scope.DbContext, "project_rerun");
        var issue = await SeedIssueAsync(scope.DbContext, "project_rerun", "issue_rerun", number: 1,
            status: IssueStatus.InProgress);
        var wr1 = "wr_rerun_first";
        var wr2 = "wr_rerun_second";
        await SeedWorkflowRunAsync(scope.DbContext, wr1, ApprovalRunState(wr1,
            requestedAt: FixedNow.AddHours(-20), wait: TimeSpan.Zero));
        await SeedWorkflowRunAsync(scope.DbContext, wr2, ApprovalRunState(wr2,
            requestedAt: FixedNow.AddHours(-10), wait: TimeSpan.Zero));
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-20), workflowRunId: wr1);
        SeedIssueEvent(scope.DbContext, issue.Id, "com.mohist.issue.work-started",
            FixedNow.AddHours(-10), workflowRunId: wr2);
        SeedWorkflowRunEvent(scope.DbContext, wr1, 1, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-20), new { stage = "plan" });
        SeedWorkflowRunEvent(scope.DbContext, wr1, 2, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-19), new { stage = "build" });
        SeedWorkflowRunEvent(scope.DbContext, wr1, 3, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-18), new { stage = "check" });
        SeedWorkflowRunEvent(scope.DbContext, wr2, 1, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-10), new { stage = "plan" });
        SeedWorkflowRunEvent(scope.DbContext, wr2, 2, EventCatalog.ReverseDns.StageStarted,
            FixedNow.AddHours(-9), new { stage = "build" });
        await scope.DbContext.SaveChangesAsync();
        var service = scope.NewService();

        var rows = await service.SnapshotForUtcDayAsync(FixedNow);

        var row = Assert.Single(rows);
        Assert.Equal(0, row.Plan);
        Assert.Equal(0, row.Check);
        Assert.Equal(1, row.Build);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void DefaultSnapshotPeriod_IsOneDay()
    {
        Assert.Equal(TimeSpan.FromDays(1), StagePopulationSnapshotOptions.DefaultSnapshotPeriod);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void SnapshotService_UsesConfiguredPeriod_WhenOptionsSupplied()
    {
        var customPeriod = TimeSpan.FromHours(6);
        var options = Options.Create(new StagePopulationSnapshotOptions
        {
            SnapshotPeriod = customPeriod,
        });

        var service = new StagePopulationSnapshotService(
            dbFactory: (IDbContextFactory<MohistDbContext>)null!,
            scopeFactory: (IServiceScopeFactory)null!,
            timeProvider: System.TimeProvider.System,
            log: Microsoft.Extensions.Logging.Abstractions.NullLogger<StagePopulationSnapshotService>.Instance,
            options: options);

        Assert.NotNull(service);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void SnapshotService_UsesDefaultPeriod_WhenOptionsNull()
    {
        var service = new StagePopulationSnapshotService(
            dbFactory: (IDbContextFactory<MohistDbContext>)null!,
            scopeFactory: (IServiceScopeFactory)null!,
            timeProvider: System.TimeProvider.System,
            log: Microsoft.Extensions.Logging.Abstractions.NullLogger<StagePopulationSnapshotService>.Instance);

        Assert.NotNull(service);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void StagePopulationSnapshotOptions_HasSectionName()
    {
        Assert.False(string.IsNullOrWhiteSpace(StagePopulationSnapshotOptions.SectionName));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void StagePopulationSnapshotOptions_SnapshotPeriod_MustBePositive()
    {
        var validator = new Microsoft.Extensions.Options.ValidateOptions<StagePopulationSnapshotOptions>(
            string.Empty, o => o.SnapshotPeriod > TimeSpan.Zero, "Period must be positive.");
        var result = validator.Validate(string.Empty, new StagePopulationSnapshotOptions
        {
            SnapshotPeriod = TimeSpan.Zero,
        });
        Assert.True(result.Failed);
    }

    // ============================================================================
    // Test scope — per-test isolated SQLite + minimal service deps for the
    // snapshot service. The sweep walks every project, so whole-DB assertions
    // require a fresh database per test (the shared collection fixture would
    // leak rows from parallel tests).
    // ============================================================================

    private sealed class SnapshotTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _rootProvider;

        private SnapshotTestScope(
            SqliteConnection connection,
            IDbContextFactory<MohistDbContext> dbFactory,
            MohistDbContext dbContext,
            FakeTimeProvider timeProvider,
            ServiceProvider rootProvider)
        {
            _connection = connection;
            DbFactory = dbFactory;
            DbContext = dbContext;
            TimeProvider = timeProvider;
            _rootProvider = rootProvider;
        }

        public IDbContextFactory<MohistDbContext> DbFactory { get; }
        public MohistDbContext DbContext { get; }
        public FakeTimeProvider TimeProvider { get; }

        public static SnapshotTestScope Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(connection)
                .Options;
            var factory = new TestDbContextFactory(options);
            using (var setupDb = factory.CreateDbContext())
            {
                GrainTestConfig.MigrateWithSchemaFix(setupDb);
            }

            // Hand-construct the snapshot service's DI graph — same
            // wiring MohistServiceRegistration.ConfigureMohistServices
            // provides in production, but lifted out of the full host so
            // each test is isolated. The scoped workflow-profile
            // resolvers are resolved per-sweep via IServiceScopeFactory.
            var promptLoader = new FakePromptLoader();
            var services = new ServiceCollection();
            services.AddSingleton(factory);
            services.AddSingleton<IDbContextFactory<MohistDbContext>>(factory);
            services.AddSingleton(SystemTimeProvider.System);
            services.AddLogging();
            services.AddSingleton<IPromptLoader>(promptLoader);
            services.AddScoped<IssueWorkflowProfileRegistry>(sp =>
                new IssueWorkflowProfileRegistry(promptLoader, sp.GetRequiredService<IDbContextFactory<MohistDbContext>>()));
            services.AddScoped<EffectiveWorkflowProfileResolver>(sp =>
                new EffectiveWorkflowProfileResolver(sp.GetRequiredService<IssueWorkflowProfileRegistry>()));
            services.AddScoped<ProjectWorkflowProfileManager>(sp =>
                new ProjectWorkflowProfileManager(
                    sp.GetRequiredService<IDbContextFactory<MohistDbContext>>(),
                    promptLoader,
                    new PromptTemplateEngine()));
            var rootProvider = services.BuildServiceProvider();

            var db = factory.CreateDbContext();

            return new SnapshotTestScope(
                connection,
                factory,
                db,
                new FakeTimeProvider(FixedNow),
                rootProvider);
        }

        public StagePopulationSnapshotService NewService() =>
            new(
                DbFactory,
                _rootProvider.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider,
                NullLogger<StagePopulationSnapshotService>.Instance);

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _rootProvider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        {
            Options = options;
        }

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    // ============================================================================
    // Seed helpers — write directly against the test DbContext. Event IDs are
    // derived from the max ID already persisted for the source so the per-test
    // DB stays self-consistent.
    // ============================================================================

    private static async Task SeedProjectAsync(MohistDbContext db, string projectId)
    {
        db.Projects.Add(new ProjectRow
        {
            Id = projectId,
            Name = projectId,
            RepositoriesJson = "[]",
        });
        await db.SaveChangesAsync();
    }

    private static async Task<DomainIssue> SeedIssueAsync(
        MohistDbContext db,
        string projectId,
        string issueId,
        int number,
        IssueStatus status)
    {
        var issue = new DomainIssue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = number,
            Title = $"Issue {number}",
            Status = status,
        };
        var json = IssueStore.Serialize(issue);
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            ProjectId = projectId,
            Number = number,
            State = json,
        });
        await db.SaveChangesAsync();
        return issue;
    }

    private static async Task<DomainIssue> SeedDoneIssueAsync(
        MohistDbContext db,
        string projectId,
        string issueId,
        int number,
        DateTimeOffset completedAt)
    {
        var wrId = "wr_" + issueId;
        var issue = new DomainIssue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = number,
            Title = $"Issue {number}",
            Status = IssueStatus.Done,
            WorkflowRunId = wrId,
            CompletedAt = completedAt.UtcDateTime,
        };
        var json = IssueStore.Serialize(issue);
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            ProjectId = projectId,
            Number = number,
            State = json,
        });
        SeedIssueEvent(db, issueId, "com.mohist.issue.work-started",
            completedAt.AddHours(-3), workflowRunId: wrId);
        SeedIssueEvent(db, issueId, "com.mohist.issue.work-completed",
            completedAt, workflowRunId: wrId);
        await db.SaveChangesAsync();
        return issue;
    }

    private static async Task<DomainIssue> SeedCancelledIssueAsync(
        MohistDbContext db,
        string projectId,
        string issueId,
        int number,
        DateTimeOffset closedAt)
    {
        var wrId = "wr_" + issueId;
        var issue = new DomainIssue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = number,
            Title = $"Issue {number}",
            Status = IssueStatus.Cancelled,
            WorkflowRunId = wrId,
            CompletedAt = closedAt.UtcDateTime,
        };
        var json = IssueStore.Serialize(issue);
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            ProjectId = projectId,
            Number = number,
            State = json,
        });
        SeedIssueEvent(db, issueId, "com.mohist.issue.work-started",
            closedAt.AddHours(-5), workflowRunId: wrId);
        SeedIssueEvent(db, issueId, "com.mohist.issue.closed",
            closedAt);
        await db.SaveChangesAsync();
        return issue;
    }

    private static async Task<DomainIssue> SeedInFlightIssueAsync(
        MohistDbContext db,
        string projectId,
        string issueId,
        int number,
        DateTimeOffset workStartedAt,
        string currentStage,
        DateTimeOffset currentStageStartedAt)
    {
        var wrId = "wr_" + issueId;
        var issue = new DomainIssue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = number,
            Title = $"Issue {number}",
            Status = IssueStatus.InProgress,
            WorkflowRunId = wrId,
        };
        var json = IssueStore.Serialize(issue);
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            ProjectId = projectId,
            Number = number,
            State = json,
        });
        SeedIssueEvent(db, issueId, "com.mohist.issue.work-started",
            workStartedAt, workflowRunId: wrId);
        await SeedWorkflowRunAsync(db, wrId, ApprovalRunState(wrId,
            requestedAt: workStartedAt, wait: TimeSpan.Zero));
        SeedWorkflowRunEvent(db, wrId, 1, EventCatalog.ReverseDns.StageStarted,
            currentStageStartedAt, new { stage = currentStage });
        await db.SaveChangesAsync();
        return issue;
    }

    private static void SeedIssueEvent(
        MohistDbContext db,
        string issueId,
        string type,
        DateTimeOffset time,
        string? workflowRunId = null)
    {
        var source = "/mohist/issues/" + issueId;
        var max = db.IssueEvents
            .AsNoTracking()
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .Max();
        var trackedMax = db.ChangeTracker.Entries<IssueEventRow>()
            .Where(e => e.Entity.Source == source)
            .Select(e => (long?)e.Entity.Id)
            .Max();
        var nextId = (max ?? 0) > (trackedMax ?? 0) ? (max ?? 0) : (trackedMax ?? 0);
        nextId += 1;
        db.IssueEvents.Add(new IssueEventRow
        {
            Id = nextId,
            Source = source,
            EventId = Guid.NewGuid().ToString(),
            Type = type,
            Time = time,
            SpecVersion = "1.0",
            Subject = null,
            DataContentType = "application/json",
            Data = workflowRunId is null
                ? JsonDocument.Parse("null").RootElement
                : JsonSerializer.SerializeToElement(new { workflowRunId }, Mohist.Server.Infrastructure.JSON.Options),
            ExtensionsJson = "{}",
        });
    }

    private static void SeedWorkflowRunEvent(
        MohistDbContext db,
        string workflowRunId,
        long sequence,
        string type,
        DateTimeOffset time,
        object data)
    {
        db.WorkflowRunEvents.Add(new WorkflowRunEventRow
        {
            Id = sequence,
            Source = WorkflowRunEventPersistence.WorkflowRunSource(workflowRunId),
            EventId = Guid.NewGuid().ToString(),
            Type = type,
            Time = time,
            SpecVersion = "1.0",
            Subject = null,
            DataContentType = "application/json",
            Data = JsonSerializer.SerializeToElement(data, Mohist.Server.Infrastructure.JSON.Options),
            ExtensionsJson = "{}",
        });
    }

    private static async Task SeedWorkflowRunAsync(
        MohistDbContext db,
        string workflowRunId,
        object state)
    {
        var json = JsonSerializer.Serialize(state, Mohist.Server.Infrastructure.JSON.Options);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            workflowRunId, json);
    }

    private static object ApprovalRunState(string workflowRunId, DateTimeOffset requestedAt, TimeSpan wait, string result = "approved") => new
    {
        metadata = new { annotations = new { projectId = "" } },
        approval = new
        {
            requestedAt = requestedAt.ToString("o"),
            respondedAt = requestedAt.Add(wait).ToString("o"),
            result,
        },
    };

    /// <summary>
    /// In-memory prompt loader used by the snapshot service's
    /// <see cref="IssueWorkflowProfileRegistry"/> dependency. The snapshot
    /// service only reads prompt bodies indirectly through profile
    /// resolution; a small canned dict is sufficient.
    /// </summary>
    private sealed class FakePromptLoader : IPromptLoader
    {
        public Dictionary<string, string> Prompts { get; } = new(StringComparer.Ordinal)
        {
            ["proposal"] = "# Proposal",
            ["specs"] = "# Specs",
            ["design"] = "# Design",
            ["tasks"] = "# Tasks",
            ["self-review"] = "# Self Review",
            ["review"] = "# Review",
            ["build"] = "# Build",
        };

        public Dictionary<string, string> LoadAll() => new(Prompts, StringComparer.Ordinal);
    }
}
