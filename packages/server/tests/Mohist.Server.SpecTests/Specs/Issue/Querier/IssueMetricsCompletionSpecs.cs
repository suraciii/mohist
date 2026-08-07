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
public class IssueMetricsCompletionSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueMetricsCompletionSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    // Guard against the regression that silenced every IssueEvents-backed
    // metric: the querier filters IssueEvents.Type by these constants, so they
    // must equal what IssueEventSerializer.BusType emits (and EventStore
    // persists). When they drifted apart, completion/throughput/quality all
    // returned empty while delivery-time (which reads db.Issues directly) kept
    // working — a contradiction the closed-loop fixtures below could not catch
    // because they seeded via the very constant under test.
    [Fact]
    public void WorkCompletedConstant_MatchesIssueEventSerializerBusType()
    {
        Assert.Equal(
            IssueMetricsQuerier.WorkCompletedType,
            IssueEventSerializer.BusType(new IssueCompleted(WorkflowRunId: "wr_guard")));
    }

    [Fact]
    public void ClosedConstant_MatchesIssueEventSerializerBusType()
    {
        Assert.Equal(
            IssueMetricsQuerier.ClosedType,
            IssueEventSerializer.BusType(new IssueCancelled(Reason: null)));
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_DayBucketing_ReturnsThirtyTrailingDays()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-day-{Guid.NewGuid():N}", Name = "Day Project" };
        var issue = SeedIssue(db, project, "issue_day_1");
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueMetricsQuerier.CompletionBucket.Day, now);

        Assert.Equal("day", result.Bucket);
        Assert.Equal(30, result.Buckets.Count);
        Assert.Equal("2026-05-21", result.Buckets[0].Boundary);
        Assert.Equal("2026-06-19", result.Buckets[^1].Boundary);
        Assert.All(result.Buckets, b =>
        {
            Assert.Equal(0, b.Completed);
            Assert.Equal(0, b.Failed);
        });
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_DayBucketing_BucketsCompletionAndFailureByIssueEventTime()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-day-fill-{Guid.NewGuid():N}", Name = "Day Fill Project" };
        var i1 = SeedIssue(db, project, "issue_df_1");
        var i2 = SeedIssue(db, project, "issue_df_2");
        var i3 = SeedIssue(db, project, "issue_df_3");
        await db.SaveChangesAsync();

        SeedEvent(db, i1, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i2, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 18, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i3, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueMetricsQuerier.CompletionBucket.Day, now);

        var d17 = Assert.Single(result.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(2, d17.Completed);
        Assert.Equal(0, d17.Failed);
        var d19 = Assert.Single(result.Buckets, b => b.Boundary == "2026-06-19");
        Assert.Equal(0, d19.Completed);
        Assert.Equal(1, d19.Failed);
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_IssueEditedAfterCompletion_StaysInCompletionBucket()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-edit-{Guid.NewGuid():N}", Name = "Edit Project" };
        var i1 = SeedIssue(db, project, "issue_edit_1", updatedAt: new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        // The completion event is in week 1 (early June).
        SeedEvent(db, i1, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 8, 10, 0, 0, TimeSpan.Zero));
        // The issue's `updatedAt` is in week 2 (a later edit/archive
        // touched it). The metric MUST keep the issue in the week 1
        // bucket, because bucketing reads `IssueEvents.Time` (terminal
        // transition time) — not issue `updatedAt`.
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueMetricsQuerier.CompletionBucket.Week, now);

        var total = result.Buckets.Sum(b => b.Completed + b.Failed);
        Assert.Equal(1, total);
        // 2026-06-08 is a Monday; verify the boundary of the only
        // non-zero bucket is exactly that Monday.
        var firstHit = result.Buckets.First(b => b.Completed + b.Failed > 0);
        Assert.Equal("2026-06-08", firstHit.Boundary);
        Assert.Equal(1, firstHit.Completed);
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_WeekBucketing_ReopenedIssueCountsOnlyLatestTerminalBucket()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-flap-{Guid.NewGuid():N}", Name = "Flap Project" };
        var i1 = SeedIssue(db, project, "issue_flap_1");
        await db.SaveChangesAsync();

        // The issue closed in week 1, was reopened, and closed again
        // in week 2. The endpoint counts only the latest terminal
        // event, so the earlier terminal bucket must not retain a stale
        // failure count.
        SeedEvent(db, i1, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(2026, 6, 8, 10, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1, "com.mohist.issue.reopened", new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueMetricsQuerier.CompletionBucket.Week, now);

        var week1 = Assert.Single(result.Buckets, b => b.Boundary == "2026-06-08");
        Assert.Equal(0, week1.Failed);
        var week2 = Assert.Single(result.Buckets, b => b.Boundary == "2026-06-15");
        Assert.Equal(1, week2.Failed);
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_DayBucketing_RecompletedIssueCountsOnlyLatestTerminalBucket()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-recomplete-{Guid.NewGuid():N}", Name = "Recomplete Project" };
        var i1 = SeedIssue(db, project, "issue_recomplete_1");
        await db.SaveChangesAsync();

        SeedEvent(db, i1, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1, "com.mohist.issue.reopened", new DateTimeOffset(2026, 6, 18, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 19, 8, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueMetricsQuerier.CompletionBucket.Day, now);

        var day17 = Assert.Single(result.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(0, day17.Completed);
        var day19 = Assert.Single(result.Buckets, b => b.Boundary == "2026-06-19");
        Assert.Equal(1, day19.Completed);
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_DistinctPerBucket_CollapsesRepeatedEventsForSameIssueAndType()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-distinct-{Guid.NewGuid():N}", Name = "Distinct Project" };
        var i1 = SeedIssue(db, project, "issue_distinct_1");
        await db.SaveChangesAsync();

        // Two same-type terminal events for the same issue in the
        // same day: must count as 1, not 2.
        SeedEvent(db, i1, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();
        SeedEvent(db, i1, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 16, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueMetricsQuerier.CompletionBucket.Day, now);

        var day = Assert.Single(result.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(1, day.Completed);
        Assert.Equal(0, day.Failed);
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_ProjectScoping_OnlyCountsTargetProjectsIssues()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var projectA = new ProjectInfo { Id = $"proj-scope-a-{Guid.NewGuid():N}", Name = "Scope A" };
        var projectB = new ProjectInfo { Id = $"proj-scope-b-{Guid.NewGuid():N}", Name = "Scope B" };
        var a1 = SeedIssue(db, projectA, "issue_scope_a_1");
        var b1 = SeedIssue(db, projectB, "issue_scope_b_1");
        await db.SaveChangesAsync();

        SeedEvent(db, a1, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, b1, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var resultA = await service.GetCompletionBucketsAsync(projectA.Id, IssueMetricsQuerier.CompletionBucket.Day, now);
        var resultB = await service.GetCompletionBucketsAsync(projectB.Id, IssueMetricsQuerier.CompletionBucket.Day, now);

        var dayA = Assert.Single(resultA.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(1, dayA.Completed);
        var dayB = Assert.Single(resultB.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(1, dayB.Completed);

        // Project A's series must not include B's event.
        Assert.DoesNotContain(resultA.Buckets, b => b.Completed > 1);
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_NonTerminalEvents_AreNotCounted()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-noise-{Guid.NewGuid():N}", Name = "Noise Project" };
        var i1 = SeedIssue(db, project, "issue_noise_1");
        await db.SaveChangesAsync();

        // Only the two terminal types should count; other types
        // (work-started, archived, reopened, …) must not contribute
        // to completed/failed counts.
        SeedEvent(db, i1, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1, "com.mohist.issue.archived", new DateTimeOffset(2026, 6, 17, 9, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1, "com.mohist.issue.reopened", new DateTimeOffset(2026, 6, 17, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueMetricsQuerier.CompletionBucket.Day, now);

        var total = result.Buckets.Sum(b => b.Completed + b.Failed);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_WeekBucketing_ReturnsTwelveTrailingWeeks()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-week-{Guid.NewGuid():N}", Name = "Week Project" };
        SeedIssue(db, project, "issue_week_1");
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        // 2026-06-19 is a Friday. Current ISO week starts on 2026-06-15
        // (Monday). 12 trailing weeks => boundaries 2026-03-30 …
        // 2026-06-15.
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueMetricsQuerier.CompletionBucket.Week, now);

        Assert.Equal("week", result.Bucket);
        Assert.Equal(12, result.Buckets.Count);
        Assert.Equal("2026-03-30", result.Buckets[0].Boundary);
        Assert.Equal("2026-06-15", result.Buckets[^1].Boundary);
    }

    [Fact]
    public void StartOfIsoWeek_ReturnsMondayForAnyInput()
    {
        // 2026-06-19 is a Friday; the Monday of the same week is
        // 2026-06-15.
        var friday = new DateTime(2026, 6, 19);
        Assert.Equal(new DateTime(2026, 6, 15), IssueMetricsQuerier.ISOWeekHelper.StartOfIsoWeek(friday));

        // 2026-06-15 is itself a Monday.
        var monday = new DateTime(2026, 6, 15);
        Assert.Equal(new DateTime(2026, 6, 15), IssueMetricsQuerier.ISOWeekHelper.StartOfIsoWeek(monday));

        // 2026-06-21 is a Sunday — the Monday of that week is
        // 2026-06-15, not 2026-06-22.
        var sunday = new DateTime(2026, 6, 21);
        Assert.Equal(new DateTime(2026, 6, 15), IssueMetricsQuerier.ISOWeekHelper.StartOfIsoWeek(sunday));
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_DayBucketing_ReturnsBothWindowTotalsFromSeededEvents()
    {
        // now = 2026-06-30 00:00 UTC: the current day-window is
        // [2026-06-01, 2026-07-01), the previous one [2026-05-02, 2026-06-01).
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-totals-{Guid.NewGuid():N}", Name = "Window Totals Project" };
        var currentIssue = SeedIssue(db, project, "issue_wt_current");
        var previousIssue = SeedIssue(db, project, "issue_wt_previous");
        await db.SaveChangesAsync();

        SeedEvent(db, currentIssue, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 15, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, previousIssue, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(2026, 5, 20, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueMetricsQuerier.CompletionBucket.Day, now);

        Assert.Equal(1, result.CurrentTotal.Completed);
        Assert.Equal(0, result.CurrentTotal.Failed);
        Assert.Equal(1, result.CurrentTotal.SampleCount);
        Assert.Equal(0, result.PreviousTotal.Completed);
        Assert.Equal(1, result.PreviousTotal.Failed);
        Assert.Equal(1, result.PreviousTotal.SampleCount);

        // The per-bucket series is unchanged alongside the totals.
        Assert.Equal(30, result.Buckets.Count);
        Assert.Equal("2026-06-01", result.Buckets[0].Boundary);
        Assert.Equal("2026-06-30", result.Buckets[^1].Boundary);
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_PreviousWindowEmptyAndGenuineZeroFailures_AreDistinct()
    {
        // Project A has terminal events only in the current window → the
        // previous window is empty (SampleCount 0). Project B has two
        // previous-window cancellations → a genuine zero completion with
        // SampleCount 2. The two must be distinguishable.
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var projectA = new ProjectInfo { Id = $"proj-empty-prev-{Guid.NewGuid():N}", Name = "Empty Previous" };
        var issueA = SeedIssue(db, projectA, "issue_ep_current");
        var projectB = new ProjectInfo { Id = $"proj-zero-prev-{Guid.NewGuid():N}", Name = "Zero Previous" };
        var issueB1 = SeedIssue(db, projectB, "issue_zp_1");
        var issueB2 = SeedIssue(db, projectB, "issue_zp_2");
        await db.SaveChangesAsync();

        SeedEvent(db, issueA, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 15, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, issueB1, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));
        SeedEvent(db, issueB2, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(2026, 5, 25, 11, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

        var empty = await service.GetCompletionBucketsAsync(projectA.Id, IssueMetricsQuerier.CompletionBucket.Day, now);
        Assert.Equal(1, empty.CurrentTotal.SampleCount);
        Assert.Equal(0, empty.PreviousTotal.Completed);
        Assert.Equal(0, empty.PreviousTotal.Failed);
        Assert.Equal(0, empty.PreviousTotal.SampleCount);

        var genuine = await service.GetCompletionBucketsAsync(projectB.Id, IssueMetricsQuerier.CompletionBucket.Day, now);
        Assert.Equal(0, genuine.CurrentTotal.SampleCount);
        Assert.Equal(0, genuine.PreviousTotal.Completed);
        Assert.Equal(2, genuine.PreviousTotal.Failed);
        Assert.Equal(2, genuine.PreviousTotal.SampleCount);
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_WeekBucketing_ReturnsBothWindowTotalsFromSeededEvents()
    {
        // now = 2026-06-30 (a Tuesday): the current ISO week starts
        // 2026-06-29 (Monday); the current 12-week window is
        // [2026-04-13, 2026-07-06), the previous one [2026-01-19, 2026-04-13).
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-week-totals-{Guid.NewGuid():N}", Name = "Week Window Totals" };
        var currentIssue = SeedIssue(db, project, "issue_wwt_current");
        var previousIssue = SeedIssue(db, project, "issue_wwt_previous");
        await db.SaveChangesAsync();

        SeedEvent(db, currentIssue, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 29, 10, 0, 0, TimeSpan.Zero));
        SeedEvent(db, previousIssue, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 3, 30, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        var result = await service.GetCompletionBucketsAsync(project.Id, IssueMetricsQuerier.CompletionBucket.Week, now);

        Assert.Equal(12, result.Buckets.Count);
        Assert.Equal(1, result.CurrentTotal.Completed);
        Assert.Equal(1, result.CurrentTotal.SampleCount);
        Assert.Equal(1, result.PreviousTotal.Completed);
        Assert.Equal(1, result.PreviousTotal.SampleCount);
    }
}
