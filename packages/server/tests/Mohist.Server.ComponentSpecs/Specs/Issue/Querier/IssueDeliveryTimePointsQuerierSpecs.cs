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
public class IssueDeliveryTimePointsQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueDeliveryTimePointsQuerierSpecs(MohistDbFixture fixture)
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, workStartedAt);
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, firstStart);
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, retryStart);
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, firstStart);
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, firstCompletionRecorded);
        SeedEvent(db, issue.Id, "com.mohist.issue.reopened", reopenedAt);
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, latestCompletionRecorded);
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(zeroMoment, TimeSpan.Zero));
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
        UpdateCompletedAtAndCreatedAt(db, issue.Id, createdAt, closedAt);
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(closedAt, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetDeliveryTimesAsync(project.Id, now);

        Assert.Empty(result.Points);
    }

}
