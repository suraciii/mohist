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
public class IssueDeliveryTimeWindowQuerierSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueDeliveryTimeWindowQuerierSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
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

}
