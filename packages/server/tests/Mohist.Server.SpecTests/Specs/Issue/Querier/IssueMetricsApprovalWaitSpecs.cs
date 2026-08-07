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
public class IssueMetricsApprovalWaitSpecs
{
    private readonly MohistDbFixture _fixture;

    public IssueMetricsApprovalWaitSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

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
}
