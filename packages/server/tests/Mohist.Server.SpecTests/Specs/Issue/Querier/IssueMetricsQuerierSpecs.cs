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

[Collection("MohistDb")]
public class IssueMetricsQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueMetricsQuerierSpecs(MohistDbFixture fixture)
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
    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void WorkCompletedConstant_MatchesIssueEventSerializerBusType()
    {
        Assert.Equal(
            IssueMetricsQuerier.WorkCompletedType,
            IssueEventSerializer.BusType(new IssueCompleted(WorkflowRunId: "wr_guard")));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void ClosedConstant_MatchesIssueEventSerializerBusType()
    {
        Assert.Equal(
            IssueMetricsQuerier.ClosedType,
            IssueEventSerializer.BusType(new IssueCancelled(Reason: null)));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_TrailingSevenDayWindow_IncludesOnlyRecentResponses()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-window-{Guid.NewGuid():N}", Name = "Approval Window Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var i1 = SeedIssue(db, project, "issue_aw_win_1", workflowRunId: "wr_aw_win_1");
        var i2 = SeedIssue(db, project, "issue_aw_win_2", workflowRunId: "wr_aw_win_2");
        var i3 = SeedIssue(db, project, "issue_aw_win_3", workflowRunId: "wr_aw_win_3");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_aw_win_1", ApprovalRunState("wr_aw_win_1", now.AddDays(-1), TimeSpan.FromHours(1)));
        await SeedWorkflowRunAsync(db, "wr_aw_win_2", ApprovalRunState("wr_aw_win_2", now.AddDays(-6), TimeSpan.FromHours(2)));
        await SeedWorkflowRunAsync(db, "wr_aw_win_3", ApprovalRunState("wr_aw_win_3", now.AddDays(-10), TimeSpan.FromHours(4)));

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        Assert.Equal(2, result.SampleCount);
        Assert.Equal(now.AddDays(-7), result.Window.From);
        Assert.Equal(now, result.Window.To);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_Statistics_ReturnsAverageMedianMaxFromSameSamples()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-stats-{Guid.NewGuid():N}", Name = "Approval Stats Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var waits = new[] { TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(16) };

        for (var i = 0; i < waits.Length; i++)
        {
            var runId = $"wr_aw_stats_{i}";
            SeedIssue(db, project, $"issue_aw_stats_{i}", workflowRunId: runId);
            await SeedWorkflowRunAsync(db, runId, ApprovalRunState(runId, now.AddDays(-1), waits[i]));
        }

        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        Assert.Equal(5, result.SampleCount);
        Assert.Equal(TimeSpan.FromHours(5).TotalSeconds, result.AverageSeconds);
        Assert.Equal(TimeSpan.FromHours(2).TotalSeconds, result.MedianSeconds);
        Assert.Equal(TimeSpan.FromHours(16).TotalSeconds, result.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_SingleSample_YieldsIdenticalAverageMedianMax()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-single-{Guid.NewGuid():N}", Name = "Approval Single Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        SeedIssue(db, project, "issue_aw_single_1", workflowRunId: "wr_aw_single_1");
        await SeedWorkflowRunAsync(db, "wr_aw_single_1", ApprovalRunState("wr_aw_single_1", now.AddDays(-1), TimeSpan.FromHours(3.2)));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        var expected = TimeSpan.FromHours(3.2).TotalSeconds;
        Assert.Equal(1, result.SampleCount);
        Assert.Equal(expected, result.AverageSeconds);
        Assert.Equal(expected, result.MedianSeconds);
        Assert.Equal(expected, result.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_PendingApproval_IsExcludedFromAggregate()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-pending-{Guid.NewGuid():N}", Name = "Approval Pending Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        SeedIssue(db, project, "issue_aw_pending_1", workflowRunId: "wr_aw_pending_1");
        SeedIssue(db, project, "issue_aw_pending_2", workflowRunId: "wr_aw_pending_2");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_aw_pending_1", ApprovalRunState("wr_aw_pending_1", now.AddDays(-1), TimeSpan.FromHours(1), "approved"));
        await SeedWorkflowRunAsync(db, "wr_aw_pending_2", AwaitingApprovalRunState("wr_aw_pending_2", now.AddDays(-1)));

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        Assert.Equal(1, result.SampleCount);
        Assert.Equal(TimeSpan.FromHours(1).TotalSeconds, result.AverageSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_RejectedApproval_ContributesLikeApproved()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-rejected-{Guid.NewGuid():N}", Name = "Approval Rejected Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var wait = TimeSpan.FromHours(4);

        SeedIssue(db, project, "issue_aw_rejected_1", workflowRunId: "wr_aw_rejected_1");
        await db.SaveChangesAsync();
        await SeedWorkflowRunAsync(db, "wr_aw_rejected_1", ApprovalRunState("wr_aw_rejected_1", now.AddDays(-1), wait, "rejected"));

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        Assert.Equal(1, result.SampleCount);
        Assert.Equal(wait.TotalSeconds, result.AverageSeconds);
        Assert.Equal(wait.TotalSeconds, result.MedianSeconds);
        Assert.Equal(wait.TotalSeconds, result.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_MultipleCompletedApprovalStagesInOneRun_CountsEachGate()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-multi-{Guid.NewGuid():N}", Name = "Approval Multi Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var planWait = TimeSpan.FromHours(1);
        var checkWait = TimeSpan.FromHours(4);

        SeedIssue(db, project, "issue_aw_multi_1", workflowRunId: "wr_aw_multi_1");
        await db.SaveChangesAsync();
        await SeedWorkflowRunAsync(
            db,
            "wr_aw_multi_1",
            MultiApprovalRunState(
                "wr_aw_multi_1",
                planRequestedAt: now.AddDays(-2),
                planWait,
                checkRequestedAt: now.AddDays(-1),
                checkWait));

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        var expectedAverage = (planWait.TotalSeconds + checkWait.TotalSeconds) / 2;
        Assert.Equal(2, result.SampleCount);
        Assert.Equal(expectedAverage, result.AverageSeconds);
        Assert.Equal(expectedAverage, result.MedianSeconds);
        Assert.Equal(checkWait.TotalSeconds, result.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_ZeroSamples_ReturnsEmptyResultWithNullStats()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-empty-{Guid.NewGuid():N}", Name = "Approval Empty Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        SeedIssue(db, project, "issue_aw_empty_1", workflowRunId: "wr_aw_empty_1");
        await db.SaveChangesAsync();
        await SeedWorkflowRunAsync(db, "wr_aw_empty_1", AwaitingApprovalRunState("wr_aw_empty_1", now.AddDays(-1)));

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        Assert.Equal(0, result.SampleCount);
        Assert.Null(result.AverageSeconds);
        Assert.Null(result.MedianSeconds);
        Assert.Null(result.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_ZeroDurationWait_IsDistinguishableFromEmpty()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-approval-zero-{Guid.NewGuid():N}", Name = "Approval Zero Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        SeedIssue(db, project, "issue_aw_zero_1", workflowRunId: "wr_aw_zero_1");
        await SeedWorkflowRunAsync(db, "wr_aw_zero_1", ApprovalRunState("wr_aw_zero_1", now.AddDays(-1), TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetApprovalWaitAsync(project.Id, now);

        Assert.Equal(1, result.SampleCount);
        Assert.Equal(0, result.AverageSeconds);
        Assert.Equal(0, result.MedianSeconds);
        Assert.Equal(0, result.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetApprovalWaitAsync_ProjectScoping_OnlyCountsTargetProjectsIssues()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var projectA = new ProjectInfo { Id = $"proj-approval-scope-a-{Guid.NewGuid():N}", Name = "Scope A" };
        var projectB = new ProjectInfo { Id = $"proj-approval-scope-b-{Guid.NewGuid():N}", Name = "Scope B" };
        SeedIssue(db, projectA, "issue_aw_scope_a_1", workflowRunId: "wr_aw_scope_a_1");
        SeedIssue(db, projectB, "issue_aw_scope_b_1", workflowRunId: "wr_aw_scope_b_1");
        await db.SaveChangesAsync();

        await SeedWorkflowRunAsync(db, "wr_aw_scope_a_1", ApprovalRunState("wr_aw_scope_a_1", new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1)));
        await SeedWorkflowRunAsync(db, "wr_aw_scope_b_1", ApprovalRunState("wr_aw_scope_b_1", new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(5)));

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var resultA = await service.GetApprovalWaitAsync(projectA.Id, now);
        var resultB = await service.GetApprovalWaitAsync(projectB.Id, now);

        Assert.Equal(1, resultA.SampleCount);
        Assert.Equal(TimeSpan.FromHours(1).TotalSeconds, resultA.AverageSeconds);
        Assert.Equal(1, resultB.SampleCount);
        Assert.Equal(TimeSpan.FromHours(5).TotalSeconds, resultB.AverageSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_PriorLifecycleRunRepair_PreventsFirstTimeRight()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-lifecycle-{Guid.NewGuid():N}", Name = "Quality Lifecycle Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_lifecycle_1", workflowRunId: "wr_quality_lifecycle_final", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, now.AddDays(-5), "wr_quality_lifecycle_first");
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, now.AddDays(-2), "wr_quality_lifecycle_final");
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_lifecycle_final");

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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_HistoricalRepairEventBeforeStageRerun_UsesDurableReworkHistory()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-rerun-repair-{Guid.NewGuid():N}", Name = "Quality Rerun Repair Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_rerun_repair_1", workflowRunId: "wr_quality_rerun_repair_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_rerun_repair_1");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_CheckFailsThenManualRetry_CountsRepeatedCheckRunAsRework()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-check-retry-{Guid.NewGuid():N}", Name = "Quality Check Retry Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_check_retry_1", workflowRunId: "wr_quality_check_retry_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_check_retry_1");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_MissingWorkflowRun_CountsAsNotFirstTimeRight()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-missing-run-{Guid.NewGuid():N}", Name = "Quality Missing Run Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_missing_run_1", workflowRunId: "wr_quality_missing_run_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_missing_run_1");
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        Assert.Equal(1, result.Window.SampleCount);
        Assert.Equal(0.0, result.Window.FirstTimeRightRate);
        Assert.All(result.Window.Stages, stage => Assert.Equal(0, stage.EnteredCount));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_UnreadableWorkflowRun_CountsAsNotFirstTimeRight()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-corrupt-run-{Guid.NewGuid():N}", Name = "Quality Corrupt Run Project" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_corrupt_run_1", workflowRunId: "wr_quality_corrupt_run_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_corrupt_run_1");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), workflowRunId);
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

        SeedEvent(db, a1.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero), "wr_quality_scope_a_1");
        SeedEvent(db, b1.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero), "wr_quality_scope_b_1");

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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, shipTime, "wr_quality_trend_leading_1");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

        SeedEvent(db, ftrDay17.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2).AddHours(1), "wr_quality_trend_ftr_a");
        SeedEvent(db, notFtrDay17.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2).AddHours(1), "wr_quality_trend_ftr_b");
        await SeedWorkflowRunAsync(db, "wr_quality_trend_ftr_a", QualityRunState("wr_quality_trend_ftr_a", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await SeedWorkflowRunAsync(db, "wr_quality_trend_ftr_b", QualityRunState("wr_quality_trend_ftr_b", [("plan", [("plan-repair", "Plan repair", 1)])]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        var day17 = Assert.Single(result.Trend.Points, p => p.Boundary == "2026-06-17");
        Assert.Equal(2, day17.SampleCount);
        Assert.Equal(0.5, day17.FirstTimeRightRate);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

        SeedEvent(db, reworked.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_trend_rework_1");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_Trend_IssueReworkedAtMultipleStagesCountsOnce()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-multistage-{Guid.NewGuid():N}", Name = "Quality Trend MultiStage" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_trend_multistage_1", workflowRunId: "wr_quality_trend_multistage_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-3), "wr_quality_trend_multistage_1");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_Trend_EmptyBucketYieldsNullRatesIndependentOfSiblings()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-empty-{Guid.NewGuid():N}", Name = "Quality Trend Empty" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_trend_empty_1", workflowRunId: "wr_quality_trend_empty_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_trend_empty_1");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        SeedEvent(db, inProgress.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-1), "wr_quality_trend_ns_inprog");
        SeedEvent(db, backlog.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2));
        SeedEvent(db, cancelled.Id, EventCatalog.ReverseDns.IssueCancelled, now.AddDays(-3));
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, shipTime, "wr_quality_trend_anchor_1");
        await SeedWorkflowRunAsync(db, "wr_quality_trend_anchor_1", QualityRunState("wr_quality_trend_anchor_1", [("plan", [("plan-ok", "Plan ok", 0)])]));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetQualityAsync(project.Id, now);

        var day = Assert.Single(result.Trend.Points, p => p.SampleCount > 0);
        Assert.Equal(shipTime.UtcDateTime.Date.ToString("yyyy-MM-dd"), day.Boundary);
        Assert.Equal(1, day.SampleCount);
        Assert.Equal(1.0, day.FirstTimeRightRate);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, shipTime, "wr_quality_trend_today_1");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, shipTime, "wr_quality_trend_midday_1");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task GetQualityAsync_Trend_AdditiveAndLeavesWindowScalarsUnchanged()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-quality-trend-additive-{Guid.NewGuid():N}", Name = "Quality Trend Additive" };
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

        var issue = SeedIssue(db, project, "issue_quality_trend_additive_1", workflowRunId: "wr_quality_trend_additive_1", status: IssueStatus.Done);
        await db.SaveChangesAsync();

        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueCompleted, now.AddDays(-2), "wr_quality_trend_additive_1");
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


    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        SeedDeliveredIssue(
            db, project, "issue_dt_edit",
            createdAt: createdAt,
            completedAt: completedAt);
        await db.SaveChangesAsync();
        UpdateIssueUpdatedAt(
            db,
            $"issue_dt_edit",
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, completedAt.AddHours(-1), workflowRunId: "wr_sd_invalid_stage");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(zeroMoment, TimeSpan.Zero), workflowRunId: "wr_sd_zero");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        SeedEvent(db, inside.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_window_inside");
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
        SeedEvent(db, outside.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(2026, 5, 19, 9, 0, 0, TimeSpan.Zero), workflowRunId: "wr_sd_window_outside");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        SeedEvent(db, insideA.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(completedAt.AddHours(-2), TimeSpan.Zero), workflowRunId: "wr_sd_scope_a");
        SeedEvent(db, insideB.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(completedAt.AddHours(-4), TimeSpan.Zero), workflowRunId: "wr_sd_scope_b");
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

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
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
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, completedAt.AddHours(-10), workflowRunId: "wr_sd_order");
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

    private static Mohist.Server.Issue.Domain.Issue SeedDeliveredIssue(
        MohistDbContext db,
        ProjectInfo project,
        string idSuffix,
        DateTime createdAt,
        DateTime completedAt,
        string? workflowRunId = null)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = idSuffix,
            ProjectId = project.Id,
            Number = ++_seedIssueCounter,
            Title = "Delivered test issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = Mohist.Server.Issue.Domain.IssueStatus.Done,
            CreatedAt = createdAt,
            UpdatedAt = completedAt,
            CompletedAt = completedAt,
            WorkflowRunId = workflowRunId,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        return issue;
    }

    private static void UpdateCompletedAtAndCreatedAt(
        MohistDbContext db,
        string issueId,
        DateTime createdAt,
        DateTime completedAt)
    {
        var row = db.Issues.AsNoTracking()
            .FirstOrDefault(r => r.IssueId == issueId)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");
        var state = IssueStore.Deserialize(row.State)
            ?? throw new InvalidOperationException($"Issue {issueId} state could not be deserialized");
        var updated = new Mohist.Server.Issue.Domain.Issue
        {
            Id = state.Id,
            ProjectId = state.ProjectId,
            Number = state.Number,
            Title = state.Title,
            Body = state.Body,
            Status = state.Status,
            Priority = state.Priority,
            Risk = state.Risk,
            CreatedAt = createdAt,
            UpdatedAt = completedAt,
            ArchivedAt = state.ArchivedAt,
            CompletedAt = completedAt,
            PrerequisiteNumbers = state.PrerequisiteNumbers,
            IsDraft = state.IsDraft,
            RepositoryRef = state.RepositoryRef,
            Labels = new Dictionary<string, string>(state.Labels, StringComparer.Ordinal),
        };
        var tracked = db.Issues.First(r => r.IssueId == issueId);
        tracked.State = IssueStore.Serialize(updated);
    }

    private static void UpdateIssueUpdatedAt(
        MohistDbContext db,
        string issueId,
        DateTime updatedAt)
    {
        var row = db.Issues.AsNoTracking()
            .FirstOrDefault(r => r.IssueId == issueId)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");
        var state = IssueStore.Deserialize(row.State)
            ?? throw new InvalidOperationException($"Issue {issueId} state could not be deserialized");
        var updated = new Mohist.Server.Issue.Domain.Issue
        {
            Id = state.Id,
            ProjectId = state.ProjectId,
            Number = state.Number,
            Title = state.Title,
            Body = state.Body,
            Status = state.Status,
            Priority = state.Priority,
            Risk = state.Risk,
            CreatedAt = state.CreatedAt,
            UpdatedAt = updatedAt,
            ArchivedAt = state.ArchivedAt,
            CompletedAt = state.CompletedAt,
            PrerequisiteNumbers = state.PrerequisiteNumbers,
            IsDraft = state.IsDraft,
            RepositoryRef = state.RepositoryRef,
            Labels = new Dictionary<string, string>(state.Labels, StringComparer.Ordinal),
        };
        var tracked = db.Issues.First(r => r.IssueId == issueId);
        tracked.State = IssueStore.Serialize(updated);
    }

    private static int _seedIssueCounter = 0;
    private static Mohist.Server.Issue.Domain.Issue SeedIssue(
        MohistDbContext db,
        ProjectInfo project,
        string idSuffix,
        DateTimeOffset? updatedAt = null,
        string? workflowRunId = null,
        Mohist.Server.Issue.Domain.IssueStatus? status = null)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = idSuffix,
            ProjectId = project.Id,
            Number = ++_seedIssueCounter,
            Title = "Test issue",
            Labels = new Dictionary<string, string>(StringComparer.Ordinal),
            Priority = "p2",
            Status = status ?? Mohist.Server.Issue.Domain.IssueStatus.Backlog,
            CreatedAt = updatedAt?.UtcDateTime ?? TestTime.UtcDateTime,
            UpdatedAt = updatedAt?.UtcDateTime ?? TestTime.UtcDateTime,
            WorkflowRunId = workflowRunId,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        return issue;
    }

    private static void SeedEvent(
        MohistDbContext db,
        string issueId,
        string type,
        DateTimeOffset time,
        string? workflowRunId = null)
    {
        var source = IssueMetricsQuerier.IssueSourcePrefix + issueId;
        var dbMax = db.IssueEvents
            .AsNoTracking()
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .Max();
        var trackedMax = db.ChangeTracker.Entries<IssueEventRow>()
            .Where(e => e.Entity.Source == source)
            .Select(e => (long?)e.Entity.Id)
            .Max();
        var nextId = (dbMax ?? 0) > (trackedMax ?? 0) ? (dbMax ?? 0) : (trackedMax ?? 0);
        nextId += 1;
        db.IssueEvents.Add(new IssueEventRow
        {
            Id = nextId,
            Source = source,
            EventId = Guid.NewGuid().ToString(),
            Type = type,
            Time = time,
            SpecVersion = "1.0",
            Subject = "1",
            DataContentType = "application/json",
            Data = workflowRunId is null
                ? JsonDocument.Parse("null").RootElement
                : JsonSerializer.SerializeToElement(new { workflowRunId }, JSON.Options),
            ExtensionsJson = "{}",
        });
    }

    private static async Task SeedWorkflowRunAsync(MohistDbContext db, string workflowRunId, object state)
    {
        var json = JsonSerializer.Serialize(state, JSON.Options);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            workflowRunId, json);
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
            Data = JsonSerializer.SerializeToElement(data, JSON.Options),
            ExtensionsJson = "{}",
        });
    }

    private static object ApprovalRunState(string workflowRunId, DateTimeOffset requestedAt, TimeSpan wait, string result = "approved") =>
        RunState(workflowRunId, requestedAt, requestedAt + wait, result);

    private static object AwaitingApprovalRunState(string workflowRunId, DateTimeOffset requestedAt) =>
        RunState(workflowRunId, requestedAt, null, null);

    private static object MultiApprovalRunState(
        string workflowRunId,
        DateTimeOffset planRequestedAt,
        TimeSpan planWait,
        DateTimeOffset checkRequestedAt,
        TimeSpan checkWait)
    {
        const string planStage = "plan";
        const string checkStage = "check";
        return new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = planRequestedAt.AddMinutes(-5), Name = "test" },
            Status = "Completed",
            CurrentStageId = checkStage,
            Stages = new[]
            {
                new
                {
                    Id = planStage,
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "Completed",
                    Tasks = new[]
                    {
                        new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/acp-agent" },
                    },
                    Checks = new[]
                    {
                        new { Name = "plan-ok", Title = "Plan ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" },
                    },
                    ApprovalStatus = new
                    {
                        Result = "approved",
                        RequestedAt = planRequestedAt.ToString("O"),
                        RespondedAt = (planRequestedAt + planWait).ToString("O"),
                    },
                },
                new
                {
                    Id = checkStage,
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "Completed",
                    Tasks = new[]
                    {
                        new { Id = "review", DefinitionId = "review", Attempt = 1, Title = "Check review", Status = "Completed", Uses = "mohist/acp-agent" },
                    },
                    Checks = new[]
                    {
                        new { Name = "check-ok", Title = "Check ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" },
                    },
                    ApprovalStatus = new
                    {
                        Result = "approved",
                        RequestedAt = checkRequestedAt.ToString("O"),
                        RespondedAt = (checkRequestedAt + checkWait).ToString("O"),
                    },
                }
            }
        };
    }

    private static object RunState(string workflowRunId, DateTimeOffset requestedAt, DateTimeOffset? respondedAt, string? result)
    {
        const string stage = "plan";
        return new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = requestedAt.AddMinutes(-5), Name = "test" },
            Status = "Completed",
            CurrentStageId = stage,
            Stages = new[]
            {
                new
                {
                    Id = stage,
                    Attempt = 1,
                    RequiresApproval = true,
                    Status = "Completed",
                    Tasks = new[]
                    {
                        new { Id = "proposal", DefinitionId = "proposal", Attempt = 1, Title = "Plan proposal", Status = "Completed", Uses = "mohist/acp-agent" },
                    },
                    Checks = new[]
                    {
                        new { Name = "plan-ok", Title = "Plan ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" },
                    },
                    ApprovalStatus = new
                    {
                        Result = result,
                        RequestedAt = requestedAt.ToString("O"),
                        RespondedAt = respondedAt?.ToString("O"),
                    },
                }
            }
        };
    }

    private static object QualityRunState(
        string workflowRunId,
        (string Stage, (string Name, string Title, int ReworkCount)[]? Checks)[] stages)
    {
        var now = TestTime.UtcNow;
        var stageObjects = stages.Select(s =>
        {
            var initialized = s.Checks is not null;
            var checks = s.Checks is null
                ? Array.Empty<object>()
                : s.Checks.Select(c => (object)new
                {
                    Name = c.Name,
                    Title = c.Title,
                    Status = "Passed",
                }).ToArray();
            var tasks = new List<object>();
            if (initialized)
            {
                tasks.Add(new { Id = $"{s.Stage}-task", DefinitionId = $"{s.Stage}-task", Attempt = 1, Title = $"{s.Stage} task", Status = "Completed", Uses = "mohist/acp-agent" });
                foreach (var check in s.Checks!.Where(c => c.ReworkCount > 0))
                    tasks.Add(new { Id = $"recover:{check.Name}.1", DefinitionId = $"recover:{check.Name}", Attempt = 1, Title = $"{check.Title} recovery", Status = "Completed", Uses = "mohist/acp-agent" });
            }

            return (object)new
            {
                Id = s.Stage,
                Attempt = 1,
                RequiresApproval = false,
                Initialized = initialized,
                Status = initialized ? "Completed" : "Pending",
                Tasks = tasks.ToArray(),
                Checks = checks,
            };
        }).ToArray();

        var currentStage = stages.LastOrDefault(s => s.Checks is not null).Stage
            ?? stages.First().Stage;

        return new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = now.AddMinutes(-5), Name = "test" },
            Status = "Completed",
            CurrentStageId = currentStage,
            Stages = stageObjects,
        };
    }
}
