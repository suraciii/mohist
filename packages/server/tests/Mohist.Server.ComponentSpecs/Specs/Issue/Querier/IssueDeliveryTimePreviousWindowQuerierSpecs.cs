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
public class IssueDeliveryTimePreviousWindowQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueDeliveryTimePreviousWindowQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_ReturnsPreviousAverageCycleDays()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-delivery-previous-{Guid.NewGuid():N}", Name = "Delivery Previous Window" };
        var currentCompletedAt = new DateTime(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc);
        var previousCompletedAt = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var current = SeedDeliveredIssue(db, project, $"{project.Id}-current", currentCompletedAt.AddDays(-4), currentCompletedAt);
        var previous = SeedDeliveredIssue(db, project, $"{project.Id}-previous", previousCompletedAt.AddDays(-4), previousCompletedAt);
        SeedEvent(db, current.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(currentCompletedAt.AddDays(-2)));
        SeedEvent(db, previous.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(previousCompletedAt.AddDays(-2)));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetDeliveryTimesAsync(
            project.Id,
            new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));

        var point = Assert.Single(result.Points);
        Assert.Equal(current.Number, point.IssueNumber);
        Assert.Equal(2.0, point.CycleDays);
        Assert.Equal(2.0, result.PreviousAverageCycleDays);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_EmptyPreviousWindow_ReturnsNullAverage()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-delivery-previous-empty-{Guid.NewGuid():N}", Name = "Delivery Empty Previous Window" };
        var completedAt = new DateTime(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(db, project, $"{project.Id}-current", completedAt.AddDays(-4), completedAt);
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(completedAt.AddDays(-2)));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result = await service.GetDeliveryTimesAsync(
            project.Id,
            new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero));

        Assert.Single(result.Points);
        Assert.Null(result.PreviousAverageCycleDays);
    }

    [Fact]
    public async Task GetDeliveryTimesAsync_UsesRequestedWindowLength()
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var project = new ProjectInfo { Id = $"proj-delivery-range-{Guid.NewGuid():N}", Name = "Delivery Range" };
        var completedAt = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var issue = SeedDeliveredIssue(db, project, $"{project.Id}-old", completedAt.AddDays(-4), completedAt);
        SeedEvent(db, issue.Id, EventCatalog.ReverseDns.IssueWorkStarted, new DateTimeOffset(completedAt.AddDays(-2)));
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var now = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var defaultWindow = await service.GetDeliveryTimesAsync(project.Id, now);
        var ninetyDayWindow = await service.GetDeliveryTimesAsync(project.Id, now, windowDays: 90);

        Assert.Empty(defaultWindow.Points);
        Assert.Single(ninetyDayWindow.Points);
    }
}
