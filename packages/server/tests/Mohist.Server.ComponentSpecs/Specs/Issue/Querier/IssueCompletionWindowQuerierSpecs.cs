using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.ComponentSpecs.Support;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Xunit;
using static Mohist.Server.ComponentSpecs.Specs.Issue.Querier.IssueMetricsQuerierTestData;

namespace Mohist.Server.ComponentSpecs.Specs.Issue.Querier;

[Collection("MohistDb")]
public class IssueCompletionWindowQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueCompletionWindowQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_DayBuckets_ReturnCurrentAndPreviousTotals()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-completion-totals-{Guid.NewGuid():N}", Name = "Completion Totals" };
        var currentCompleted = SeedIssue(db, project, $"{project.Id}-current-completed");
        var currentFailed = SeedIssue(db, project, $"{project.Id}-current-failed");
        var previousCompleted = SeedIssue(db, project, $"{project.Id}-previous-completed");
        await db.SaveChangesAsync();

        SeedEvent(db, currentCompleted.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero));
        SeedEvent(db, currentFailed.Id, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(2026, 6, 17, 10, 0, 0, TimeSpan.Zero));
        SeedEvent(db, previousCompleted.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetCompletionBucketsAsync(
            project.Id,
            IssueMetricsQuerier.CompletionBucket.Day,
            new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(1, result.CurrentTotal.Completed);
        Assert.Equal(1, result.CurrentTotal.Failed);
        Assert.Equal(2, result.CurrentTotal.SampleCount);
        Assert.Equal(1, result.PreviousTotal.Completed);
        Assert.Equal(0, result.PreviousTotal.Failed);
        Assert.Equal(1, result.PreviousTotal.SampleCount);
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_PreviousSampleCount_DistinguishesEmptyFromFailedOnly()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var emptyProject = new ProjectInfo { Id = $"proj-completion-empty-{Guid.NewGuid():N}", Name = "Empty Previous Window" };
        var failedProject = new ProjectInfo { Id = $"proj-completion-failed-{Guid.NewGuid():N}", Name = "Failed Previous Window" };
        var current = SeedIssue(db, emptyProject, $"{emptyProject.Id}-current");
        var previousFailure = SeedIssue(db, failedProject, $"{failedProject.Id}-previous-failure");
        await db.SaveChangesAsync();

        SeedEvent(db, current.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero));
        SeedEvent(db, previousFailure.Id, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var empty = await service.GetCompletionBucketsAsync(emptyProject.Id, IssueMetricsQuerier.CompletionBucket.Day, now);
        var failed = await service.GetCompletionBucketsAsync(failedProject.Id, IssueMetricsQuerier.CompletionBucket.Day, now);

        Assert.Equal(0, empty.PreviousTotal.SampleCount);
        Assert.Equal(0, empty.PreviousTotal.Completed);
        Assert.Equal(0, empty.PreviousTotal.Failed);
        Assert.Equal(1, failed.PreviousTotal.SampleCount);
        Assert.Equal(0, failed.PreviousTotal.Completed);
        Assert.Equal(1, failed.PreviousTotal.Failed);
    }

    [Fact]
    public async Task GetCompletionBucketsAsync_WeekBuckets_ReturnCurrentAndPreviousTotals()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-completion-week-totals-{Guid.NewGuid():N}", Name = "Weekly Completion Totals" };
        var current = SeedIssue(db, project, $"{project.Id}-current");
        var previous = SeedIssue(db, project, $"{project.Id}-previous");
        await db.SaveChangesAsync();

        SeedEvent(db, current.Id, EventCatalog.ReverseDns.IssueCompleted, new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero));
        SeedEvent(db, previous.Id, EventCatalog.ReverseDns.IssueCancelled, new DateTimeOffset(2026, 3, 20, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetCompletionBucketsAsync(
            project.Id,
            IssueMetricsQuerier.CompletionBucket.Week,
            new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(1, result.CurrentTotal.Completed);
        Assert.Equal(1, result.CurrentTotal.SampleCount);
        Assert.Equal(1, result.PreviousTotal.Failed);
        Assert.Equal(1, result.PreviousTotal.SampleCount);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(90)]
    public async Task GetCompletionBucketsAsync_DayBuckets_UseRequestedWindowLength(int windowDays)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-completion-day-range-{windowDays}-{Guid.NewGuid():N}", Name = "Daily Completion Range" };
        SeedIssue(db, project, $"{project.Id}-issue");
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetCompletionBucketsAsync(
            project.Id,
            IssueMetricsQuerier.CompletionBucket.Day,
            new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero),
            windowDays);

        Assert.Equal(windowDays, result.Buckets.Count);
    }

    [Theory]
    [InlineData(7, 1)]
    [InlineData(90, 13)]
    public async Task GetCompletionBucketsAsync_WeekBuckets_RoundRequestedWindowUpToWeeks(int windowDays, int expectedBuckets)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-completion-week-range-{windowDays}-{Guid.NewGuid():N}", Name = "Weekly Completion Range" };
        SeedIssue(db, project, $"{project.Id}-issue");
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetCompletionBucketsAsync(
            project.Id,
            IssueMetricsQuerier.CompletionBucket.Week,
            new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero),
            windowDays);

        Assert.Equal(expectedBuckets, result.Buckets.Count);
    }
}
