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
public class IssueCompletionBucketsQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueCompletionBucketsQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
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

        SeedEvent(db, i1.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i2.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 18, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i3.Id, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(2026, 6, 19, 9, 0, 0, TimeSpan.Zero));
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
        SeedEvent(db, i1.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 8, 10, 0, 0, TimeSpan.Zero));
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
        SeedEvent(db, i1.Id, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(2026, 6, 8, 10, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1.Id, "com.mohist.issue.reopened", new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1.Id, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero));
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

        SeedEvent(db, i1.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1.Id, "com.mohist.issue.reopened", new DateTimeOffset(2026, 6, 18, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 19, 8, 0, 0, TimeSpan.Zero));
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
        SeedEvent(db, i1.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();
        SeedEvent(db, i1.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 16, 0, 0, TimeSpan.Zero));
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

        SeedEvent(db, a1.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, b1.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 17, 9, 0, 0, TimeSpan.Zero));
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
        SeedEvent(db, i1.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1.Id, "com.mohist.issue.archived", new DateTimeOffset(2026, 6, 17, 9, 0, 0, TimeSpan.Zero));
        SeedEvent(db, i1.Id, "com.mohist.issue.reopened", new DateTimeOffset(2026, 6, 17, 10, 0, 0, TimeSpan.Zero));
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

}
