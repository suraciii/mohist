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
using Mohist.Server.TestSupport;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Services;
using Mohist.Server.SpecTests.Specs.Sessions;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Querier;

using static Mohist.Server.SpecTests.Specs.Issue.Querier.IssueMetricsTestSupport;

[Collection("MohistDb")]
public class IssueMetricsDeliveryTimesSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueMetricsDeliveryTimesSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_DeliveredIssue_ReturnsLeadAndCycleDurations()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dt-basic-{Guid.NewGuid():N}", Name = "Delivery Time Basic" };
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var workStartedAt = new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero);
        var completedAt = new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_dt_basic",
            createdAt: createdAt,
            completedAt: completedAt);
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, workStartedAt);
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetDeliveryTimesAsync(project.Id, now);

        var point = Assert.Single(result.Points);
        Assert.Equal(issue.Number, point.IssueNumber);
        Assert.Equal(
            new DateTimeOffset(completedAt, TimeSpan.Zero),
            point.CompletedAt);
        // Lead = (6/5 14:00) - (6/1 08:00) = 4.25 days.
        Assert.Equal(4.25, point.LeadDays, precision: 5);
        // Cycle = (6/5 14:00) - (6/3 10:00) = 2.166... days.
        Assert.NotNull(point.CycleDays);
        Assert.Equal(2.1667, point.CycleDays!.Value, precision: 3);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_RetryKeepsEarliestWorkStart()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dt-retry-{Guid.NewGuid():N}", Name = "Delivery Time Retry" };
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var firstStart = new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero);
        var retryStart = new DateTimeOffset(2026, 6, 5, 11, 0, 0, TimeSpan.Zero);
        var completedAt = new DateTime(2026, 6, 9, 16, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_dt_retry",
            createdAt: createdAt,
            completedAt: completedAt);
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, firstStart);
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, retryStart);
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetDeliveryTimesAsync(project.Id, now);

        var point = Assert.Single(result.Points);
        Assert.NotNull(point.CycleDays);
        // Cycle anchors on earliest start (6/2 09:00), not the retry (6/5 11:00).
        Assert.Equal(7.2917, point.CycleDays!.Value, precision: 3);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_ReopenedAndReCompleted_AnchorsOnLatestCompletion()
    {
        // The aggregate's CompletedAt is the latest terminal `done` moment
        // (issue-completion-timestamp spec). A reopen-and-re-complete
        // therefore re-anchors the point at the latest completion and the
        // prior completion is not retained as a separate sample.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dt-reopen-{Guid.NewGuid():N}", Name = "Delivery Time Reopen" };
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var firstStart = new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.Zero);
        var firstCompletionRecorded = new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
        var reopenedAt = new DateTimeOffset(2026, 6, 7, 9, 0, 0, TimeSpan.Zero);
        var latestCompletionRecorded = new DateTimeOffset(2026, 6, 12, 14, 0, 0, TimeSpan.Zero);
        var latestCompletedAt = new DateTime(2026, 6, 12, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_dt_reopen",
            createdAt: createdAt,
            completedAt: latestCompletedAt);
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, firstStart);
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, firstCompletionRecorded);
        SeedEvent(db, issue, "com.mohist.issue.reopened", reopenedAt);
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCompleted, latestCompletionRecorded);
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetDeliveryTimesAsync(project.Id, now);

        var point = Assert.Single(result.Points);
        // Only one sample from this issue — the prior completion is not
        // a separate point.
        Assert.Equal(issue.Number, point.IssueNumber);
        Assert.Equal(
            new DateTimeOffset(latestCompletedAt, TimeSpan.Zero),
            point.CompletedAt);
        // Lead anchored at creation, spanning to the latest completion.
        Assert.Equal(11.25, point.LeadDays, precision: 5);
        // Cycle preserves the earliest work-start (6/2 10:00) even though
        // completion moved from 6/6 to 6/12.
        Assert.NotNull(point.CycleDays);
        Assert.Equal(10.1667, point.CycleDays!.Value, precision: 3);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_DeliveredIssueWithoutWorkStart_CycleIsUndefined()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dt-noStart-{Guid.NewGuid():N}", Name = "Delivery Time NoStart" };
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var completedAt = new DateTime(2026, 6, 4, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_dt_no_start",
            createdAt: createdAt,
            completedAt: completedAt);
        await db.SaveChangesAsync();
        // No work-started event for this issue.

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetDeliveryTimesAsync(project.Id, now);

        var point = Assert.Single(result.Points);
        // Lead is still defined even when cycle is undefined.
        Assert.Equal(3.25, point.LeadDays, precision: 5);
        // `null` distinguishes "undefined" from a genuine zero-duration cycle.
        Assert.Null(point.CycleDays);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_GenuineZeroDurationCycle_ReportsZero()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dt-zero-{Guid.NewGuid():N}", Name = "Delivery Time Zero" };
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var zeroMoment = new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_dt_zero",
            createdAt: createdAt,
            completedAt: zeroMoment);
        // Work-started happens at exactly the same instant as completion.
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(zeroMoment, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetDeliveryTimesAsync(project.Id, now);

        var point = Assert.Single(result.Points);
        Assert.NotNull(point.CycleDays);
        // Genuine zero-duration cycle, not the undefined null.
        Assert.Equal(0.0, point.CycleDays!.Value, precision: 5);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_CancelledIssue_ExcludedFromSeries()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dt-cancel-{Guid.NewGuid():N}", Name = "Delivery Time Cancelled" };
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var closedAt = new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc);
        var issue = SeedIssue(
            db, project, "issue_dt_cancelled",
            status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        await db.SaveChangesAsync();
        // Override `CreatedAt` and `CompletedAt` post-seed via a fresh
        // serialized state — the canonical helper does not capture these
        // for non-Done states.
        UpdateCompletedAtAndCreatedAt(db, issue, createdAt, closedAt);
        SeedEvent(db, issue, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(closedAt, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetDeliveryTimesAsync(project.Id, now);

        Assert.Empty(result.Points);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_IssueEditedAfterCompletion_AnchorsOnCompletedAt()
    {
        // A post-completion edit that bumps `UpdatedAt` must NOT move the
        // point — the surface reads `CompletedAt`, not `UpdatedAt`.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dt-edit-{Guid.NewGuid():N}", Name = "Delivery Time Edit" };
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var completedAt = new DateTime(2026, 6, 8, 10, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(
            db, project, "issue_dt_edit",
            createdAt: createdAt,
            completedAt: completedAt);
        await db.SaveChangesAsync();
        UpdateIssueUpdatedAt(
            db,
            issue,
            new DateTime(2026, 6, 25, 14, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 26, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetDeliveryTimesAsync(project.Id, now);

        var point = Assert.Single(result.Points);
        Assert.Equal(
            new DateTimeOffset(completedAt, TimeSpan.Zero),
            point.CompletedAt);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_CompletedBeyond30Days_ExcludedFromWindow()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dt-window-{Guid.NewGuid():N}", Name = "Delivery Time Window" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        // Inside the 30-day window.
        var inside = SeedDeliveredIssue(
            db, project, "issue_dt_inside",
            createdAt: new DateTime(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc));
        // Outside: 31 days before `now`.
        SeedDeliveredIssue(
            db, project, "issue_dt_outside",
            createdAt: new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 5, 19, 10, 0, 0, DateTimeKind.Utc));
        // Boundary-equal: exactly 30 days before `now` (inclusive lower bound).
        SeedDeliveredIssue(
            db, project, "issue_dt_boundary",
            createdAt: new DateTime(2026, 5, 19, 8, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc));

        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetDeliveryTimesAsync(project.Id, now);

        // Two issues remain; the 31-day-old one drops out of the window.
        Assert.Equal(2, result.Points.Count);
        Assert.Contains(result.Points, p => p.IssueNumber == inside.Number);
        Assert.DoesNotContain(result.Points, p => string.Equals(p.IssueNumber.ToString(), "issue_dt_outside"));
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_NoDeliveredIssuesInWindow_ReturnsEmptyPoints()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dt-empty-{Guid.NewGuid():N}", Name = "Delivery Time Empty" };
        SeedIssue(db, project, "issue_dt_empty_1");
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetDeliveryTimesAsync(project.Id, now);

        Assert.Empty(result.Points);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_DeliveredIssuesInOtherProject_NotInSeries()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var projectA = new ProjectInfo { Id = $"proj-dt-scope-a-{Guid.NewGuid():N}", Name = "Scope A" };
        var projectB = new ProjectInfo { Id = $"proj-dt-scope-b-{Guid.NewGuid():N}", Name = "Scope B" };
        var a = SeedDeliveredIssue(
            db, projectA, "issue_dt_scope_a",
            createdAt: new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 6, 10, 14, 0, 0, DateTimeKind.Utc));
        SeedDeliveredIssue(
            db, projectB, "issue_dt_scope_b",
            createdAt: new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 6, 12, 14, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var resultA = await service.GetDeliveryTimesAsync(projectA.Id, now);
        var resultB = await service.GetDeliveryTimesAsync(projectB.Id, now);

        var pointA = Assert.Single(resultA.Points);
        Assert.Equal(a.Number, pointA.IssueNumber);
        Assert.Single(resultB.Points);
        Assert.DoesNotContain(resultA.Points, p => p.IssueNumber == a.Number + 1);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_MultipleDeliveredIssues_OrdersByCompletionAscending()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dt-order-{Guid.NewGuid():N}", Name = "Delivery Time Order" };
        var early = SeedDeliveredIssue(
            db, project, "issue_dt_early",
            createdAt: new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc));
        var late = SeedDeliveredIssue(
            db, project, "issue_dt_late",
            createdAt: new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 6, 15, 14, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetDeliveryTimesAsync(project.Id, now);

        Assert.Equal(2, result.Points.Count);
        Assert.True(result.Points[0].CompletedAt < result.Points[1].CompletedAt);
        Assert.Equal(early.Number, result.Points[0].IssueNumber);
        Assert.Equal(late.Number, result.Points[1].IssueNumber);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_BothWindowsReturned_PreviousAverageDerivableFromSeededIssues()
    {
        // now = 2026-06-30 00:00 UTC: current window [2026-05-31, 2026-06-30],
        // previous window [2026-05-01, 2026-05-31). One delivered issue (with
        // work-start) in each window; only the current-window issue contributes
        // a point, the previous-window issue contributes only the average.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-dt-both-{Guid.NewGuid():N}", Name = "Delivery Time Both Windows" };

        var current = SeedDeliveredIssue(
            db, project, "issue_dt_both_current",
            createdAt: new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 6, 14, 14, 0, 0, DateTimeKind.Utc));
        SeedEvent(db, current, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero));

        var previous = SeedDeliveredIssue(
            db, project, "issue_dt_both_previous",
            createdAt: new DateTime(2026, 5, 10, 8, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 5, 20, 14, 0, 0, DateTimeKind.Utc));
        SeedEvent(db, previous, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 5, 14, 14, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        var result = await service.GetDeliveryTimesAsync(project.Id, now);

        var point = Assert.Single(result.Points);
        Assert.NotNull(point.CycleDays);
        // Previous window's only delivered cycle is exactly 6 days
        // (work-start 6 days before its completion).
        Assert.Equal(6.0, result.PreviousAverageCycleDays!.Value, precision: 5);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_WindowEmptiness_CurrentAndPreviousEvaluatedIndependently()
    {
        // Project A delivers only in the current window → previous average is
        // the defined null (empty), not a fabricated zero. Project B delivers
        // only in the previous window → Points is empty but the previous
        // average is still returned.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var projectA = new ProjectInfo { Id = $"proj-dt-prev-empty-{Guid.NewGuid():N}", Name = "Delivery Previous Empty" };
        var issueA = SeedDeliveredIssue(
            db, projectA, "issue_dt_prev_empty",
            createdAt: new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 6, 14, 14, 0, 0, DateTimeKind.Utc));
        SeedEvent(db, issueA, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero));

        var projectB = new ProjectInfo { Id = $"proj-dt-curr-empty-{Guid.NewGuid():N}", Name = "Delivery Current Empty" };
        var issueB = SeedDeliveredIssue(
            db, projectB, "issue_dt_curr_empty",
            createdAt: new DateTime(2026, 5, 6, 0, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 5, 16, 0, 0, 0, DateTimeKind.Utc));
        SeedEvent(db, issueB, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 5, 13, 0, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

        var currentOnly = await service.GetDeliveryTimesAsync(projectA.Id, now);
        Assert.Single(currentOnly.Points);
        Assert.Null(currentOnly.PreviousAverageCycleDays);

        var previousOnly = await service.GetDeliveryTimesAsync(projectB.Id, now);
        Assert.Empty(previousOnly.Points);
        Assert.Equal(3.0, previousOnly.PreviousAverageCycleDays!.Value, precision: 5);
    }
}
