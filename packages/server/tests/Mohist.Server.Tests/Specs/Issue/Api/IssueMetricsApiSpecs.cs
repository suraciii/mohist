using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.StagePopulation;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Tests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Tests.Specs.Issue.Api;

[Collection("MohistIntegration")]
public class IssueMetricsApiSpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;

    public IssueMetricsApiSpecs(MohistIntegrationFixture fixture)
    {
        _client = fixture.Client;
        _fixture = fixture;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletionMetrics_DayBucket_ReturnsThirtyTrailingDays()
    {
        var project = await CreateProjectAsync($"metrics-day-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=day");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);
        Assert.Equal("day", payload.Bucket);
        Assert.Equal(30, payload.Buckets.Length);
        Assert.Equal(payload.Window.From, payload.Buckets[0].Boundary + "T00:00:00.0000000+00:00");
        Assert.Equal(payload.Buckets[^1].Boundary, DateOnly.Parse(payload.Window.To[..10]).AddDays(-1).ToString("yyyy-MM-dd"));
        Assert.All(payload.Buckets, b =>
        {
            Assert.Equal(0, b.Completed);
            Assert.Equal(0, b.Failed);
        });
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletionMetrics_WeekBucket_ReturnsTwelveTrailingWeeks()
    {
        var project = await CreateProjectAsync($"metrics-week-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=week");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);
        Assert.Equal("week", payload.Bucket);
        Assert.Equal(12, payload.Buckets.Length);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletionMetrics_UnsupportedBucket_ReturnsBadRequest()
    {
        // v1 contract: only `day` and `week` are honored. Any custom
        // bucket size or non-supported name must be rejected.
        var project = await CreateProjectAsync($"metrics-bad-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=month");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletionMetrics_IssueEditedAfterCompletion_StaysInCompletionBucket()
    {
        var project = await CreateProjectAsync($"metrics-edit-{Guid.NewGuid():N}");
        var issue = await CreateIssueAsync(project.Id, "Edited-after-completion issue");

        // The completion event is in week 1 (early June 2026).
        await SeedEventAsync(
            issue.Id,
            IssueQuerier.WorkCompletedType,
            new DateTimeOffset(2026, 6, 8, 10, 0, 0, TimeSpan.Zero));

        // The issue's `updatedAt` is in week 2 (a later edit touched
        // it). The metric MUST keep the issue in the week-1 bucket
        // because bucketing reads `IssueEvents.Time`, not
        // issue `updatedAt`.
        await UpdateIssueUpdatedAtAsync(issue.Id, new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero));

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=week");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);

        var total = payload.Buckets.Sum(b => b.Completed + b.Failed);
        Assert.Equal(1, total);
        var hit = payload.Buckets.First(b => b.Completed + b.Failed > 0);
        Assert.Equal("2026-06-08", hit.Boundary);
        Assert.Equal(1, hit.Completed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletionMetrics_ProjectScoping_OnlyCountsTargetProjectsIssues()
    {
        var projectA = await CreateProjectAsync($"metrics-scope-a-{Guid.NewGuid():N}");
        var projectB = await CreateProjectAsync($"metrics-scope-b-{Guid.NewGuid():N}");
        var issueA = await CreateIssueAsync(projectA.Id, "A issue");
        var issueB = await CreateIssueAsync(projectB.Id, "B issue");

        await SeedEventAsync(issueA.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        await SeedEventAsync(issueB.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 17, 9, 0, 0, TimeSpan.Zero));

        using var responseA = await _client.GetAsync(
            $"/api/projects/{projectA.Id}/issues/metrics/completion?bucket=day");
        responseA.EnsureSuccessStatusCode();
        var payloadA = await ReadDataAsync<CompletionMetricsResponse>(responseA);
        var dayA = Assert.Single(payloadA.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(1, dayA.Completed);
        Assert.DoesNotContain(payloadA.Buckets, b => b.Completed > 1);

        using var responseB = await _client.GetAsync(
            $"/api/projects/{projectB.Id}/issues/metrics/completion?bucket=day");
        responseB.EnsureSuccessStatusCode();
        var payloadB = await ReadDataAsync<CompletionMetricsResponse>(responseB);
        var dayB = Assert.Single(payloadB.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(1, dayB.Completed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletionMetrics_DistinctPerBucket_CollapsesRepeatedEventsForSameIssueAndType()
    {
        var project = await CreateProjectAsync($"metrics-distinct-{Guid.NewGuid():N}");
        var issue = await CreateIssueAsync(project.Id, "Flapping");

        // Two same-type terminal events for the same issue on the
        // same day: must count as 1, not 2.
        await SeedEventAsync(issue.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        await SeedEventAsync(issue.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 17, 16, 0, 0, TimeSpan.Zero));

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=day");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);
        var day = Assert.Single(payload.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(1, day.Completed);
        Assert.Equal(0, day.Failed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletionMetrics_RecompletedIssue_CountsOnlyLatestTerminalBucket()
    {
        var project = await CreateProjectAsync($"metrics-recomplete-{Guid.NewGuid():N}");
        var issue = await CreateIssueAsync(project.Id, "Recompleted");

        await SeedEventAsync(issue.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero));
        await SeedEventAsync(issue.Id, "com.mohist.issue.reopened", new DateTimeOffset(2026, 6, 18, 8, 0, 0, TimeSpan.Zero));
        await SeedEventAsync(issue.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 19, 8, 0, 0, TimeSpan.Zero));

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=day");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);

        var day17 = Assert.Single(payload.Buckets, b => b.Boundary == "2026-06-17");
        Assert.Equal(0, day17.Completed);
        var day19 = Assert.Single(payload.Buckets, b => b.Boundary == "2026-06-19");
        Assert.Equal(1, day19.Completed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletionMetrics_DayBucket_ReturnsBothWindowTotalsFromSeededEvents()
    {
        // The fixture's TimeProvider is fixed at 2026-06-30 00:00:00 UTC,
        // so the current day-window is [2026-06-01, 2026-07-01) and the
        // previous day-window is [2026-05-02, 2026-06-01). Seed events
        // in each window and verify both totals.
        var project = await CreateProjectAsync($"metrics-totals-{Guid.NewGuid():N}");
        var currentIssue = await CreateIssueAsync(project.Id, "Current window issue");
        var previousIssue = await CreateIssueAsync(project.Id, "Previous window issue");

        await SeedEventAsync(currentIssue.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 15, 8, 0, 0, TimeSpan.Zero));
        await SeedEventAsync(previousIssue.Id, IssueQuerier.ClosedType, new DateTimeOffset(2026, 5, 20, 9, 0, 0, TimeSpan.Zero));

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=day");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);

        Assert.NotNull(payload.CurrentTotal);
        Assert.NotNull(payload.PreviousTotal);
        Assert.Equal(1, payload.CurrentTotal.Completed);
        Assert.Equal(0, payload.CurrentTotal.Failed);
        Assert.Equal(1, payload.CurrentTotal.SampleCount);
        Assert.Equal(0, payload.PreviousTotal.Completed);
        Assert.Equal(1, payload.PreviousTotal.Failed);
        Assert.Equal(1, payload.PreviousTotal.SampleCount);

        // The existing per-bucket series and window are preserved
        // unchanged alongside the new totals.
        Assert.Equal("day", payload.Bucket);
        Assert.Equal(30, payload.Buckets.Length);
        Assert.NotNull(payload.Window);
        Assert.Equal("2026-06-01", payload.Buckets[0].Boundary);
        Assert.Equal("2026-06-30", payload.Buckets[^1].Boundary);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletionMetrics_DayBucket_EmptyPreviousWindowReportsZeroSampleCount()
    {
        // No terminal issues in the previous window → SampleCount 0
        // (the empty / zero-sample result), distinguishable from a
        // genuine zero-completion window.
        var project = await CreateProjectAsync($"metrics-empty-prev-{Guid.NewGuid():N}");
        var currentIssue = await CreateIssueAsync(project.Id, "Current only");
        await SeedEventAsync(currentIssue.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 15, 8, 0, 0, TimeSpan.Zero));

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=day");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);

        Assert.Equal(1, payload.CurrentTotal.SampleCount);
        Assert.Equal(0, payload.PreviousTotal.Completed);
        Assert.Equal(0, payload.PreviousTotal.Failed);
        Assert.Equal(0, payload.PreviousTotal.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletionMetrics_DayBucket_GenuineZeroCompletionPreviousWindowIsDistinctFromEmpty()
    {
        // Every terminal issue in the previous window cancelled —
        // a GENUINE zero completion with non-zero SampleCount,
        // distinguishable from the empty (zero-sample) result.
        var project = await CreateProjectAsync($"metrics-zero-prev-{Guid.NewGuid():N}");
        var p1 = await CreateIssueAsync(project.Id, "Cancelled prev 1");
        var p2 = await CreateIssueAsync(project.Id, "Cancelled prev 2");
        await SeedEventAsync(p1.Id, IssueQuerier.ClosedType, new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero));
        await SeedEventAsync(p2.Id, IssueQuerier.ClosedType, new DateTimeOffset(2026, 5, 25, 11, 0, 0, TimeSpan.Zero));

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=day");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);

        Assert.Equal(0, payload.PreviousTotal.Completed);
        Assert.Equal(2, payload.PreviousTotal.Failed);
        Assert.Equal(2, payload.PreviousTotal.SampleCount);
        // Genuine zero has non-zero sample count; the empty result
        // (SampleCount 0) must be distinguishable from it on the wire.
        Assert.NotEqual(0, payload.PreviousTotal.SampleCount);
        Assert.Equal(0, payload.CurrentTotal.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletionMetrics_DayBucket_BothWindowTotalsAreAdditiveToExistingResponse()
    {
        // The new totals must not displace any existing field. Verify
        // the existing per-bucket series, window, and bucket granularity
        // remain intact when totals are populated.
        var project = await CreateProjectAsync($"metrics-additive-{Guid.NewGuid():N}");
        var issue = await CreateIssueAsync(project.Id, "Additive check");
        await SeedEventAsync(issue.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 15, 8, 0, 0, TimeSpan.Zero));

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=day");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);

        // Existing fields unchanged.
        Assert.Equal("day", payload.Bucket);
        Assert.Equal(30, payload.Buckets.Length);
        Assert.Equal(payload.Buckets[0].Boundary, payload.Window.From[..10]);
        Assert.Equal(payload.Buckets[^1].Boundary, DateOnly.Parse(payload.Window.To[..10]).AddDays(-1).ToString("yyyy-MM-dd"));
        // Totals added on top.
        Assert.NotNull(payload.CurrentTotal);
        Assert.NotNull(payload.PreviousTotal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CompletionMetrics_WeekBucket_ReturnsBothWindowTotalsFromSeededEvents()
    {
        // Fixture TimeProvider = 2026-06-30 (a Tuesday). The current
        // ISO week starts on 2026-06-29 (Monday); the current
        // 12-week window is [2026-04-13, 2026-07-06). The previous
        // 12-week window is [2026-01-19, 2026-04-13).
        var project = await CreateProjectAsync($"metrics-week-totals-{Guid.NewGuid():N}");
        var currentIssue = await CreateIssueAsync(project.Id, "Current week issue");
        var previousIssue = await CreateIssueAsync(project.Id, "Previous window issue");

        // 2026-06-29 (Monday) → current week window.
        await SeedEventAsync(currentIssue.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 6, 29, 10, 0, 0, TimeSpan.Zero));
        // 2026-03-30 (Monday) → previous 12-week window.
        await SeedEventAsync(previousIssue.Id, IssueQuerier.WorkCompletedType, new DateTimeOffset(2026, 3, 30, 10, 0, 0, TimeSpan.Zero));

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=week");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);

        Assert.Equal("week", payload.Bucket);
        Assert.Equal(12, payload.Buckets.Length);
        Assert.Equal(1, payload.CurrentTotal.Completed);
        Assert.Equal(1, payload.CurrentTotal.SampleCount);
        Assert.Equal(1, payload.PreviousTotal.Completed);
        Assert.Equal(1, payload.PreviousTotal.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ApprovalWaitMetrics_HasCompletedApprovals_ReturnsWindowSampleCountAndStats()
    {
        var project = await CreateProjectAsync($"approval-wait-present-{Guid.NewGuid():N}");
        var requestedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var approvedWait = TimeSpan.FromHours(3.2);
        var rejectedWait = TimeSpan.FromHours(1.4);
        var issueId = $"issue_approval_present_{Guid.NewGuid():N}";
        var workflowRunId = $"wr_approval_present_{Guid.NewGuid():N}";
        var rejectedIssueId = $"issue_approval_rejected_{Guid.NewGuid():N}";
        var rejectedWorkflowRunId = $"wr_approval_rejected_{Guid.NewGuid():N}";

        await SeedIssueWithCompletedApprovalAsync(
            project.Id,
            number: 1,
            issueId,
            workflowRunId,
            requestedAt,
            approvedWait,
            "approved");
        await SeedIssueWithCompletedApprovalAsync(
            project.Id,
            number: 2,
            rejectedIssueId,
            rejectedWorkflowRunId,
            requestedAt,
            rejectedWait,
            "rejected");

        var before = DateTimeOffset.UtcNow;
        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/approval-wait");
        var after = DateTimeOffset.UtcNow;
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<ApprovalWaitMetricsResponse>(response);
        var windowTo = DateTimeOffset.Parse(payload.Window.To);
        var windowFrom = DateTimeOffset.Parse(payload.Window.From);
        Assert.True(windowTo >= before && windowTo <= after, "Window.To should be the server request time.");
        Assert.Equal(windowTo.AddDays(-7), windowFrom);
        Assert.Equal(2, payload.SampleCount);
        Assert.Equal((approvedWait.TotalSeconds + rejectedWait.TotalSeconds) / 2, payload.AverageSeconds);
        Assert.Equal((approvedWait.TotalSeconds + rejectedWait.TotalSeconds) / 2, payload.MedianSeconds);
        Assert.Equal(approvedWait.TotalSeconds, payload.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ApprovalWaitMetrics_MultipleCompletedApprovalStagesInOneRun_CountsEachGate()
    {
        var project = await CreateProjectAsync($"approval-wait-multi-{Guid.NewGuid():N}");
        var requestedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var planWait = TimeSpan.FromHours(1);
        var checkWait = TimeSpan.FromHours(4);
        var issueId = $"issue_approval_multi_{Guid.NewGuid():N}";
        var workflowRunId = $"wr_approval_multi_{Guid.NewGuid():N}";

        await SeedIssueWithCompletedApprovalsAsync(
            project.Id,
            number: 1,
            issueId,
            workflowRunId,
            requestedAt,
            planWait,
            checkWait);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/approval-wait");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<ApprovalWaitMetricsResponse>(response);
        var expectedAverage = (planWait.TotalSeconds + checkWait.TotalSeconds) / 2;
        Assert.Equal(2, payload.SampleCount);
        Assert.Equal(expectedAverage, payload.AverageSeconds);
        Assert.Equal(expectedAverage, payload.MedianSeconds);
        Assert.Equal(checkWait.TotalSeconds, payload.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task ApprovalWaitMetrics_NoQualifyingApprovals_ReturnsEmptyResult()
    {
        var project = await CreateProjectAsync($"approval-wait-empty-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/approval-wait");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<ApprovalWaitMetricsResponse>(response);
        Assert.Equal(0, payload.SampleCount);
        Assert.Null(payload.AverageSeconds);
        Assert.Null(payload.MedianSeconds);
        Assert.Null(payload.MaxSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task QualityMetrics_ShippedIssuesWithRepairs_ReturnsBothWindowsWithRates()
    {
        var project = await CreateProjectAsync($"quality-present-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();
        var requestedAt = now.AddDays(-1);
        var issueId = $"issue_quality_present_{Guid.NewGuid():N}";
        var workflowRunId = $"wr_quality_present_{Guid.NewGuid():N}";

        await SeedIssueWithQualityRunAsync(
            project.Id,
            number: 1,
            issueId,
            workflowRunId,
            requestedAt,
            [
                ("plan", [("plan-ok", "Plan ok", 0)]),
                ("build", [("build-ok", "Build ok", 1)]),
            ]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/quality");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<QualityMetricsResponse>(response);
        Assert.NotNull(payload.Window7d);
        Assert.NotNull(payload.Window30d);

        Assert.Equal(1, payload.Window7d.SampleCount);
        Assert.Equal(0.0, payload.Window7d.FirstTimeRightRate);
        Assert.Contains(payload.Window7d.Stages, s => s.Stage == "plan" && s.EnteredCount == 1 && s.ReworkRate == 0.0);
        Assert.Contains(payload.Window7d.Stages, s => s.Stage == "build" && s.EnteredCount == 1 && s.ReworkRate == 1.0);
        Assert.Contains(payload.Window7d.Stages, s => s.Stage == "check" && s.EnteredCount == 0 && s.ReworkRate == null);
        Assert.Contains(payload.Window7d.Stages, s => s.Stage == "integrate" && s.EnteredCount == 0 && s.ReworkRate == null);

        Assert.Equal(1, payload.Window30d.SampleCount);
        Assert.NotNull(payload.Window30d.FirstTimeRightRate);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task QualityMetrics_NoShippedIssues_ReturnsEmptyResultPerWindow()
    {
        var project = await CreateProjectAsync($"quality-empty-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/quality");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<QualityMetricsResponse>(response);
        Assert.Equal(0, payload.Window7d.SampleCount);
        Assert.Null(payload.Window7d.FirstTimeRightRate);
        Assert.Contains(payload.Window7d.Stages, s => s.Stage == "plan" && s.EnteredCount == 0 && s.ReworkRate == null);
        Assert.Contains(payload.Window7d.Stages, s => s.Stage == "build" && s.EnteredCount == 0 && s.ReworkRate == null);
        Assert.Contains(payload.Window7d.Stages, s => s.Stage == "check" && s.EnteredCount == 0 && s.ReworkRate == null);
        Assert.Contains(payload.Window7d.Stages, s => s.Stage == "integrate" && s.EnteredCount == 0 && s.ReworkRate == null);
        Assert.Equal(0, payload.Window30d.SampleCount);
        Assert.Null(payload.Window30d.FirstTimeRightRate);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task QualityMetrics_ShippedIssuesWithRepairs_ReturnsTrendAlongsideWindows()
    {
        var project = await CreateProjectAsync($"quality-trend-present-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();
        var firstBoundary = DateOnly.FromDateTime(now.UtcDateTime.Date).AddDays(-29);
        var shipTime = new DateTimeOffset(firstBoundary.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var issueId = $"issue_quality_trend_present_{Guid.NewGuid():N}";
        var workflowRunId = $"wr_quality_trend_present_{Guid.NewGuid():N}";

        await SeedIssueWithQualityRunAsync(
            project.Id,
            number: 1,
            issueId,
            workflowRunId,
            shipTime,
            [
                ("plan", [("plan-ok", "Plan ok", 0)]),
                ("build", [("build-repair", "Build repair", 1)]),
            ]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/quality");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<QualityMetricsResponse>(response);
        Assert.NotNull(payload.Window7d);
        Assert.NotNull(payload.Window30d);
        Assert.NotNull(payload.Trend);

        Assert.Equal("day", payload.Trend.Bucket);
        Assert.Equal(30, payload.Trend.Points.Length);
        Assert.Equal(payload.Window30d.From, payload.Trend.From);
        Assert.Equal(payload.Window30d.To, payload.Trend.To);

        var shippedBoundary = shipTime.UtcDateTime.Date.ToString("yyyy-MM-dd");
        var shippedPoint = Assert.Single(payload.Trend.Points, p => p.Boundary == shippedBoundary);
        Assert.Equal(1, payload.Window30d.SampleCount);
        Assert.Equal(1, shippedPoint.SampleCount);
        Assert.Equal(0.0, shippedPoint.FirstTimeRightRate);
        Assert.Equal(1.0, shippedPoint.ReworkRate);

        // The route uses the injected `TimeProvider`; a sample in the first
        // visible calendar bucket must be counted by both the scalar and trend.
        Assert.Equal(shippedBoundary, payload.Trend.Points[0].Boundary);

        var emptyPoint = payload.Trend.Points[1];
        Assert.NotEqual(shippedBoundary, emptyPoint.Boundary);
        Assert.Equal(0, emptyPoint.SampleCount);
        Assert.Null(emptyPoint.FirstTimeRightRate);
        Assert.Null(emptyPoint.ReworkRate);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task QualityMetrics_NoShippedIssues_ReturnsTwoHundredWithNullTrendRates()
    {
        var project = await CreateProjectAsync($"quality-trend-empty-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/quality");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var payload = await ReadDataAsync<QualityMetricsResponse>(response);
        Assert.NotNull(payload.Trend);
        Assert.Equal("day", payload.Trend.Bucket);
        Assert.Equal(30, payload.Trend.Points.Length);
        Assert.All(payload.Trend.Points, p =>
        {
            Assert.Equal(0, p.SampleCount);
            Assert.Null(p.FirstTimeRightRate);
            Assert.Null(p.ReworkRate);
        });
        Assert.Equal(0, payload.Window7d.SampleCount);
        Assert.Null(payload.Window7d.FirstTimeRightRate);
        Assert.Equal(0, payload.Window30d.SampleCount);
        Assert.Null(payload.Window30d.FirstTimeRightRate);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task QualityMetrics_BothWindowsReturned_DeltaDerivableAcrossAdjacent30DayWindows()
    {
        // Fixture `now` is 2026-06-30 UTC. Current 30d window
        // [2026-05-31, 2026-06-30]; previous 30d window
        // [2026-05-01, 2026-05-31). Seed one shipped issue in each
        // window with different FTR outcomes and verify both rates
        // are returned so a consumer can derive the percentage-point
        // delta.
        var project = await CreateProjectAsync($"quality-both-{Guid.NewGuid():N}");

        var currentShipTime = new DateTimeOffset(2026, 6, 14, 14, 0, 0, TimeSpan.Zero);
        await SeedIssueWithQualityRunAsync(
            project.Id,
            number: 1,
            $"issue_quality_both_current_{Guid.NewGuid():N}",
            $"wr_quality_both_current_{Guid.NewGuid():N}",
            currentShipTime,
            [
                ("plan", [("plan-ok", "Plan ok", 0)]),
                ("build", [("build-repair", "Build repair", 1)]),
            ]);

        var previousShipTime = new DateTimeOffset(2026, 5, 20, 14, 0, 0, TimeSpan.Zero);
        await SeedIssueWithQualityRunAsync(
            project.Id,
            number: 2,
            $"issue_quality_both_previous_{Guid.NewGuid():N}",
            $"wr_quality_both_previous_{Guid.NewGuid():N}",
            previousShipTime,
            [
                ("plan", [("plan-ok", "Plan ok", 0)]),
                ("build", [("build-ok", "Build ok", 0)]),
            ]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/quality");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<QualityMetricsResponse>(response);
        // Current 30d window: 1 sample, FTR = 0.0 (build had a repair).
        Assert.Equal(1, payload.Window30d.SampleCount);
        Assert.Equal(0.0, payload.Window30d.FirstTimeRightRate);
        // Previous 30d window: 1 sample, FTR = 1.0 (no repairs).
        Assert.Equal(1, payload.PreviousSampleCount);
        Assert.Equal(1.0, payload.PreviousFirstTimeRightRate);
        // Delta is 1.0 - 0.0 = 1.0 percentage-point — derivable in
        // a single read from the two rates.
        Assert.Equal(
            1.0 - 0.0,
            payload.PreviousFirstTimeRightRate!.Value - payload.Window30d.FirstTimeRightRate!.Value,
            precision: 5);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task QualityMetrics_PreviousWindowEmpty_ReportsNullRateIndependentOfCurrentWindow()
    {
        // Only seed a current-window issue. The previous window is
        // empty (no shipped issues fell in it). `previousSampleCount`
        // must be 0 and `previousFirstTimeRightRate` must be the
        // defined `null` (empty), evaluated independently of the
        // current window's populated rate.
        var project = await CreateProjectAsync($"quality-prev-empty-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();
        var shipTime = now.AddDays(-1);
        await SeedIssueWithQualityRunAsync(
            project.Id,
            number: 1,
            $"issue_quality_prev_empty_{Guid.NewGuid():N}",
            $"wr_quality_prev_empty_{Guid.NewGuid():N}",
            shipTime,
            [
                ("plan", [("plan-ok", "Plan ok", 0)]),
                ("build", [("build-ok", "Build ok", 0)]),
            ]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/quality");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<QualityMetricsResponse>(response);
        // Current window is populated.
        Assert.Equal(1, payload.Window30d.SampleCount);
        Assert.Equal(1.0, payload.Window30d.FirstTimeRightRate);
        // Previous window is empty — discriminator sampleCount is 0
        // and the rate is null (NOT a fabricated 0.0 / 1.0).
        Assert.Equal(0, payload.PreviousSampleCount);
        Assert.Null(payload.PreviousFirstTimeRightRate);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task QualityMetrics_GenuineZeroAndGenuineOneRatesInPreviousWindow_AreDistinctFromEmpty()
    {
        // The previous window has at least one shipped issue, so the
        // empty (zero-sample) result is NOT what we are seeing. Two
        // cases: (a) all previous-window issues had a check that
        // triggered a repair → genuine rate 0.0 with sampleCount > 0;
        // (b) all previous-window issues were FTR → genuine rate 1.0
        // with sampleCount > 0. Both must be reported as rates (not
        // null) and be distinguishable from the empty result.
        var project = await CreateProjectAsync($"quality-prev-distinct-{Guid.NewGuid():N}");

        // (a) Genuine 0.0: one previous-window issue with a repair.
        var zeroShipTime = new DateTimeOffset(2026, 5, 5, 14, 0, 0, TimeSpan.Zero);
        await SeedIssueWithQualityRunAsync(
            project.Id,
            number: 1,
            $"issue_quality_prev_zero_{Guid.NewGuid():N}",
            $"wr_quality_prev_zero_{Guid.NewGuid():N}",
            zeroShipTime,
            [
                ("plan", [("plan-ok", "Plan ok", 0)]),
                ("build", [("build-repair", "Build repair", 1)]),
            ]);

        // (b) Genuine 1.0: a separate previous-window issue, all FTR.
        var oneShipTime = new DateTimeOffset(2026, 5, 20, 14, 0, 0, TimeSpan.Zero);
        await SeedIssueWithQualityRunAsync(
            project.Id,
            number: 2,
            $"issue_quality_prev_one_{Guid.NewGuid():N}",
            $"wr_quality_prev_one_{Guid.NewGuid():N}",
            oneShipTime,
            [
                ("plan", [("plan-ok", "Plan ok", 0)]),
                ("build", [("build-ok", "Build ok", 0)]),
            ]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/quality");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<QualityMetricsResponse>(response);
        // 2 contributing samples → genuine rate 0.5 (1 FTR, 1 not).
        // This proves the rate is computed (not fabricated) and is
        // structurally distinct from the empty (sampleCount 0, null
        // rate) result the previous-empty test pins.
        Assert.Equal(2, payload.PreviousSampleCount);
        Assert.NotNull(payload.PreviousFirstTimeRightRate);
        Assert.Equal(0.5, payload.PreviousFirstTimeRightRate!.Value, precision: 5);
        // sampleCount must be > 0 — i.e. the rate is genuine, not the
        // empty-result null.
        Assert.NotEqual(0, payload.PreviousSampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task QualityMetrics_AdditivePreservation_ExistingWindowsAndTrendUnchanged()
    {
        // The previous-window addition must be strictly additive: the
        // existing window7d/window30d/Trend shapes are preserved
        // byte-for-byte for a consumer that does not read the
        // previous-window fields. Seed an issue 1 day ago so it
        // falls in both 7d and 30d and the trend has a populated
        // point, exactly as `ShippedIssuesWithRepairs` does.
        var project = await CreateProjectAsync($"quality-additive-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();
        var shipTime = now.AddDays(-1);
        await SeedIssueWithQualityRunAsync(
            project.Id,
            number: 1,
            $"issue_quality_additive_{Guid.NewGuid():N}",
            $"wr_quality_additive_{Guid.NewGuid():N}",
            shipTime,
            [
                ("plan", [("plan-ok", "Plan ok", 0)]),
                ("build", [("build-repair", "Build repair", 1)]),
            ]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/quality");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<QualityMetricsResponse>(response);
        // Existing window7d preserved.
        Assert.Equal(1, payload.Window7d.SampleCount);
        Assert.Equal(0.0, payload.Window7d.FirstTimeRightRate);
        Assert.Contains(payload.Window7d.Stages, s => s.Stage == "plan" && s.EnteredCount == 1 && s.ReworkRate == 0.0);
        Assert.Contains(payload.Window7d.Stages, s => s.Stage == "build" && s.EnteredCount == 1 && s.ReworkRate == 1.0);
        Assert.Contains(payload.Window7d.Stages, s => s.Stage == "check" && s.EnteredCount == 0 && s.ReworkRate == null);
        Assert.Contains(payload.Window7d.Stages, s => s.Stage == "integrate" && s.EnteredCount == 0 && s.ReworkRate == null);
        // Existing window30d preserved.
        Assert.Equal(1, payload.Window30d.SampleCount);
        Assert.Equal(0.0, payload.Window30d.FirstTimeRightRate);
        Assert.Contains(payload.Window30d.Stages, s => s.Stage == "plan" && s.EnteredCount == 1 && s.ReworkRate == 0.0);
        Assert.Contains(payload.Window30d.Stages, s => s.Stage == "build" && s.EnteredCount == 1 && s.ReworkRate == 1.0);
        // Existing trend series preserved (30 dense per-day buckets).
        Assert.NotNull(payload.Trend);
        Assert.Equal("day", payload.Trend.Bucket);
        Assert.Equal(30, payload.Trend.Points.Length);
        Assert.Equal(payload.Window30d.From, payload.Trend.From);
        Assert.Equal(payload.Window30d.To, payload.Trend.To);
        // New previous-window fields: empty result (no previous-window
        // issues seeded). The previous-window addition does not touch
        // any existing field.
        Assert.Equal(0, payload.PreviousSampleCount);
        Assert.Null(payload.PreviousFirstTimeRightRate);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task QualityMetrics_UnknownProject_ReturnsNotFound()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj-quality-unknown-{Guid.NewGuid():N}/issues/metrics/quality");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DeliveryTimeMetrics_DeliveredIssueWithWorkStart_ReturnsLeadAndCycle()
    {
        var project = await CreateProjectAsync($"delivery-time-present-{Guid.NewGuid():N}");
        var completedAt = DeliveryTimeCompletedAt();
        var createdAt = completedAt.AddDays(-4).AddHours(-6);
        var workStartedAt = new DateTimeOffset(completedAt, TimeSpan.Zero)
            .AddDays(-2)
            .AddHours(-4);
        var issueId = $"issue_dt_present_{Guid.NewGuid():N}";
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 1,
            issueId,
            createdAt,
            completedAt,
            [workStartedAt]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/delivery-time");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<DeliveryTimeMetricsResponse>(response);
        var point = Assert.Single(payload.Points);
        Assert.Equal(1, point.IssueNumber);
        Assert.Equal(
            new DateTimeOffset(completedAt, TimeSpan.Zero).ToString("o"),
            point.CompletedAt);
        Assert.Equal(4.25, point.LeadDays, precision: 5);
        Assert.NotNull(point.CycleDays);
        Assert.Equal(2.1667, point.CycleDays!.Value, precision: 3);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DeliveryTimeMetrics_NoDeliveredIssues_ReturnsEmptyPoints()
    {
        var project = await CreateProjectAsync($"delivery-time-empty-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/delivery-time");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<DeliveryTimeMetricsResponse>(response);
        Assert.Empty(payload.Points);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DeliveryTimeMetrics_DeliveredIssueWithoutWorkStart_ReportsNullCycle()
    {
        var project = await CreateProjectAsync($"delivery-time-noStart-{Guid.NewGuid():N}");
        var completedAt = DeliveryTimeCompletedAt();
        var createdAt = completedAt.AddDays(-3).AddHours(-6);
        var issueId = $"issue_dt_nostart_{Guid.NewGuid():N}";
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 1,
            issueId,
            createdAt,
            completedAt,
            Array.Empty<DateTimeOffset>());

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/delivery-time");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<DeliveryTimeMetricsResponse>(response);
        var point = Assert.Single(payload.Points);
        // `null` cycle time is the "undefined" marker — not a fabricated zero.
        Assert.Null(point.CycleDays);
        // Lead time is always defined.
        Assert.Equal(3.25, point.LeadDays, precision: 5);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DeliveryTimeMetrics_GenuineZeroDurationCycle_ReportsZeroAndIsDistinctFromEmpty()
    {
        var project = await CreateProjectAsync($"delivery-time-zero-{Guid.NewGuid():N}");
        var zeroMoment = DeliveryTimeCompletedAt();
        var createdAt = zeroMoment.AddDays(-4).AddHours(-6);
        var issueId = $"issue_dt_zero_{Guid.NewGuid():N}";
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 1,
            issueId,
            createdAt,
            zeroMoment,
            [new DateTimeOffset(zeroMoment, TimeSpan.Zero)]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/delivery-time");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<DeliveryTimeMetricsResponse>(response);
        var point = Assert.Single(payload.Points);
        // Genuine zero, not the empty-array null marker.
        Assert.NotNull(point.CycleDays);
        Assert.Equal(0.0, point.CycleDays!.Value, precision: 5);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DeliveryTimeMetrics_UsesInjectedRouteClockForTrailingWindow()
    {
        var project = await CreateProjectAsync($"delivery-time-clock-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();
        var insideCompletedAt = new DateTimeOffset(DeliveryTimeCompletedAt(), TimeSpan.Zero);
        var outsideCompletedAt = now.AddDays(-40);
        var insideIssueId = $"issue_dt_clock_inside_{Guid.NewGuid():N}";
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 1,
            insideIssueId,
            insideCompletedAt.UtcDateTime.AddDays(-4).AddHours(-6),
            insideCompletedAt.UtcDateTime,
            [insideCompletedAt.AddDays(-2).AddHours(-4)]);
        var outsideIssueId = $"issue_dt_clock_outside_{Guid.NewGuid():N}";
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 2,
            outsideIssueId,
            outsideCompletedAt.UtcDateTime.AddDays(-10),
            outsideCompletedAt.UtcDateTime,
            [outsideCompletedAt.AddDays(-9)]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/delivery-time");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<DeliveryTimeMetricsResponse>(response);
        var point = Assert.Single(payload.Points);
        Assert.Equal(1, point.IssueNumber);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DeliveryTimeMetrics_UnknownProject_ReturnsNotFound()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj-dt-unknown-{Guid.NewGuid():N}/issues/metrics/delivery-time");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DeliveryTimeMetrics_BothWindowsReturned_DeltaDerivableAcrossAdjacent30DayWindows()
    {
        // Fixture `now` is 2026-06-30 UTC: current window [2026-05-31, 2026-06-30],
        // previous window [2026-05-01, 2026-05-31). Seed one delivered issue
        // (with work-start) inside each window with different cycle durations
        // and verify both averages are present in the response so a consumer
        // can derive the delta.
        var project = await CreateProjectAsync($"delivery-time-both-{Guid.NewGuid():N}");

        var currentCompletedAt = new DateTimeOffset(2026, 6, 14, 14, 0, 0, TimeSpan.Zero);
        var currentWorkStart = currentCompletedAt.AddDays(-2).AddHours(-4);
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 1,
            $"issue_dt_both_current_{Guid.NewGuid():N}",
            currentCompletedAt.UtcDateTime.AddDays(-4).AddHours(-6),
            currentCompletedAt.UtcDateTime,
            [currentWorkStart]);

        var previousCompletedAt = new DateTimeOffset(2026, 5, 20, 14, 0, 0, TimeSpan.Zero);
        var previousWorkStart = previousCompletedAt.AddDays(-6);
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 2,
            $"issue_dt_both_previous_{Guid.NewGuid():N}",
            previousCompletedAt.UtcDateTime.AddDays(-10),
            previousCompletedAt.UtcDateTime,
            [previousWorkStart]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/delivery-time");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<DeliveryTimeMetricsResponse>(response);
        // Only the current-window issue contributes a `Point`; the
        // previous-window issue contributes only to the average.
        var point = Assert.Single(payload.Points);
        Assert.NotNull(payload.PreviousCycleDays);
        // Previous window's only delivered cycle was 6 days exactly,
        // since work-start is the moment − 6 days before completion.
        Assert.Equal(6.0, payload.PreviousCycleDays!.Value, precision: 5);
        Assert.NotNull(point.CycleDays);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DeliveryTimeMetrics_PreviousWindowEmpty_ReportsNullIndependentOfCurrentWindow()
    {
        // Only seed a current-window issue; the previous window is empty.
        // `previousCycleDays` must remain the defined `null` (empty), not a
        // fabricated zero, regardless of the current-window activity.
        var project = await CreateProjectAsync($"delivery-time-prev-empty-{Guid.NewGuid():N}");
        var completedAt = DeliveryTimeCompletedAt();
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 1,
            $"issue_dt_prev_empty_{Guid.NewGuid():N}",
            completedAt.AddDays(-4).AddHours(-6),
            completedAt,
            [new DateTimeOffset(completedAt, TimeSpan.Zero).AddDays(-2).AddHours(-4)]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/delivery-time");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<DeliveryTimeMetricsResponse>(response);
        var point = Assert.Single(payload.Points);
        Assert.NotNull(point.CycleDays);
        // Independent evaluation — current window has its cycle, previous window is empty.
        Assert.Null(payload.PreviousCycleDays);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DeliveryTimeMetrics_CurrentWindowEmpty_PreviousWindowAverageStillReturned()
    {
        // Reverse asymmetry from the test above: current window has no
        // delivered issues, but the previous window does. The previous
        // average must still be returned and `Points` empty (preserved
        // empty-result semantics).
        var project = await CreateProjectAsync($"delivery-time-curr-empty-{Guid.NewGuid():N}");
        var insideCurrentCompletedAt = DeliveryTimeCompletedAt();
        var outsideCompletedAt = _fixture.TimeProvider.GetUtcNow().AddDays(-45);
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 1,
            $"issue_dt_curr_empty_outside_{Guid.NewGuid():N}",
            outsideCompletedAt.UtcDateTime.AddDays(-10),
            outsideCompletedAt.UtcDateTime,
            [outsideCompletedAt.AddDays(-3)]);

        // Sanity: the seeded issue is outside the current window and
        // inside the previous [now − 60d, now − 30d) window (45d ago is
        // within [30, 60) days before fixture-now).
        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/delivery-time");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<DeliveryTimeMetricsResponse>(response);
        Assert.Empty(payload.Points);
        Assert.NotNull(payload.PreviousCycleDays);
        Assert.Equal(3.0, payload.PreviousCycleDays!.Value, precision: 5);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task DeliveryTimeMetrics_AdditivePreservation_PointsAndTrailingWindowUnchanged()
    {
        // The previous-window addition must be strictly additive: the
        // Points series shape, ordering, and field semantics are preserved
        // byte-for-byte for an existing consumer that does not read
        // `previousCycleDays`.
        var project = await CreateProjectAsync($"delivery-time-additive-{Guid.NewGuid():N}");
        var firstCompletedAt = DeliveryTimeCompletedAt();
        var firstWorkStart = new DateTimeOffset(firstCompletedAt, TimeSpan.Zero)
            .AddDays(-1)
            .AddHours(-12);
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 1,
            $"issue_dt_add_first_{Guid.NewGuid():N}",
            firstCompletedAt.AddDays(-2).AddHours(-6),
            firstCompletedAt,
            [firstWorkStart]);

        var secondCompletedAt = firstCompletedAt.AddHours(48);
        var secondWorkStart = new DateTimeOffset(secondCompletedAt, TimeSpan.Zero)
            .AddDays(-3);
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 2,
            $"issue_dt_add_second_{Guid.NewGuid():N}",
            secondCompletedAt.AddDays(-5),
            secondCompletedAt,
            [secondWorkStart]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/delivery-time");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<DeliveryTimeMetricsResponse>(response);
        Assert.Equal(2, payload.Points.Length);
        // Ordered ascending by CompletedAt (existing requirement).
        Assert.True(payload.Points[0].CompletedAt.CompareTo(payload.Points[1].CompletedAt) <= 0);
        // Each point preserves LeadDays / CycleDays / IssueNumber / CompletedAt.
        foreach (var p in payload.Points)
        {
            Assert.NotEqual(0, p.LeadDays);
            Assert.NotNull(p.CycleDays);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StageDurationMetrics_DeliveredIssueWithStageEvents_ReturnsStagesRatioAndWait()
    {
        var project = await CreateProjectAsync($"stage-duration-present-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();
        var shipTime = now.AddDays(-2);
        var issueId = $"issue_sd_present_{Guid.NewGuid():N}";
        var workflowRunId = $"wr_sd_present_{Guid.NewGuid():N}";
        var createdAt = shipTime.AddHours(-10).UtcDateTime;
        var completedAt = shipTime.UtcDateTime;

        await SeedDeliveredIssueWithStageRunAsync(
            project.Id,
            number: 1,
            issueId,
            workflowRunId,
            createdAt,
            completedAt,
            shipTime,
            [
                ("plan", shipTime.AddHours(-10), shipTime.AddHours(-7)),
                ("build", shipTime.AddHours(-7), shipTime.AddHours(-3)),
            ],
            approvalWait: TimeSpan.FromHours(1));

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/stage-duration");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<StageDurationMetricsResponse>(response);
        Assert.Equal(2, payload.Stages.Length);

        var planStage = Assert.Single(payload.Stages, s => s.Stage == "plan");
        Assert.Equal(1, planStage.SampleCount);
        Assert.NotNull(planStage.AverageSeconds);
        Assert.Equal(3 * 3600, planStage.AverageSeconds!.Value, precision: 3);
        Assert.NotNull(planStage.MedianSeconds);
        Assert.Equal(3 * 3600, planStage.MedianSeconds!.Value, precision: 3);

        var buildStage = Assert.Single(payload.Stages, s => s.Stage == "build");
        Assert.Equal(1, buildStage.SampleCount);
        Assert.NotNull(buildStage.AverageSeconds);
        Assert.Equal(4 * 3600, buildStage.AverageSeconds!.Value, precision: 3);

        // Σ stages = 7h, approval wait = 1h → activeWork = 6h.
        // Cycle = 10h. Ratio = 0.6.
        Assert.NotNull(payload.FlowEfficiencyRatio);
        Assert.Equal(0.6, payload.FlowEfficiencyRatio!.Value, precision: 3);

        Assert.NotNull(payload.WaitBreakout);
        Assert.NotNull(payload.WaitBreakout!.AverageApprovalGateWaitSeconds);
        Assert.Equal(3600, payload.WaitBreakout!.AverageApprovalGateWaitSeconds!.Value, precision: 3);
        // inactiveGap = cycle(10) - stages(7) = 3h.
        Assert.NotNull(payload.WaitBreakout.AverageInactiveGapSeconds);
        Assert.Equal(3 * 3600, payload.WaitBreakout.AverageInactiveGapSeconds!.Value, precision: 3);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StageDurationMetrics_RunOnlyInLifecyclePayload_DiscoversStageEvents()
    {
        var project = await CreateProjectAsync($"stage-duration-event-run-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();
        var shipTime = now.AddDays(-2);
        var issueId = $"issue_sd_event_run_{Guid.NewGuid():N}";
        var workflowRunId = $"wr_sd_event_run_{Guid.NewGuid():N}";
        var staleRunId = $"wr_sd_event_run_stale_{Guid.NewGuid():N}";

        await SeedDeliveredIssueWithStageRunAsync(
            project.Id,
            number: 1,
            issueId,
            workflowRunId,
            shipTime.AddHours(-6).UtcDateTime,
            shipTime.UtcDateTime,
            shipTime,
            [("plan", shipTime.AddHours(-4), shipTime.AddHours(-1))],
            approvalWait: TimeSpan.Zero,
            issueWorkflowRunId: staleRunId);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/stage-duration");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<StageDurationMetricsResponse>(response);
        var planStage = Assert.Single(payload.Stages, s => s.Stage == "plan");
        Assert.Equal(1, planStage.SampleCount);
        Assert.Equal(3 * 3600, planStage.AverageSeconds!.Value, precision: 3);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StageDurationMetrics_NoDeliveredIssuesInWindow_ReturnsEmptyResult()
    {
        var project = await CreateProjectAsync($"stage-duration-empty-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/stage-duration");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<StageDurationMetricsResponse>(response);
        Assert.Empty(payload.Stages);
        Assert.Null(payload.FlowEfficiencyRatio);
        Assert.NotNull(payload.WaitBreakout);
        Assert.Null(payload.WaitBreakout!.AverageApprovalGateWaitSeconds);
        Assert.Null(payload.WaitBreakout.AverageInactiveGapSeconds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StageDurationMetrics_UsesInjectedRouteClockForTrailingWindow()
    {
        // The route uses the injected `TimeProvider`, never the wall
        // clock. Use the fixture's current fake clock so completed issues
        // can be placed on the boundary of the trailing window.
        var project = await CreateProjectAsync($"stage-duration-clock-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();

        var insideIssueId = $"issue_sd_clock_inside_{Guid.NewGuid():N}";
        var insideRunId = $"wr_sd_clock_inside_{Guid.NewGuid():N}";
        await SeedDeliveredIssueWithStageRunAsync(
            project.Id,
            number: 1,
            insideIssueId,
            insideRunId,
            now.AddDays(-3).UtcDateTime,
            now.AddDays(-1).UtcDateTime,
            now.AddDays(-1),
            [("plan", now.AddDays(-1).AddHours(-2), now.AddDays(-1))],
            approvalWait: TimeSpan.Zero);

        var outsideIssueId = $"issue_sd_clock_outside_{Guid.NewGuid():N}";
        var outsideRunId = $"wr_sd_clock_outside_{Guid.NewGuid():N}";
        await SeedDeliveredIssueWithStageRunAsync(
            project.Id,
            number: 2,
            outsideIssueId,
            outsideRunId,
            now.AddDays(-100).UtcDateTime,
            now.AddDays(-60).UtcDateTime,
            now.AddDays(-60),
            [("plan", now.AddDays(-60).AddHours(-2), now.AddDays(-60))],
            approvalWait: TimeSpan.Zero);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/stage-duration");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<StageDurationMetricsResponse>(response);
        var planStage = Assert.Single(payload.Stages, s => s.Stage == "plan");
        // Only the in-window issue contributes.
        Assert.Equal(1, planStage.SampleCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task StageDurationMetrics_UnknownProject_ReturnsNotFound()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj-sd-unknown-{Guid.NewGuid():N}/issues/metrics/stage-duration");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CumulativeFlow_NoSnapshots_ReturnsEmptySeriesWithFixedWindow()
    {
        // No snapshot has landed for this project — the endpoint must
        // still return 200 with a defined empty series, never an error
        // and never a fabricated snapshot. The window bounds must be
        // exactly the trailing-90-day window pinned by the server.
        var project = await CreateProjectAsync($"cf-empty-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();
        var expectedFrom = now.UtcDateTime.Date.AddDays(-89).ToString("yyyy-MM-dd");
        var expectedTo = now.UtcDateTime.Date.ToString("yyyy-MM-dd");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/cumulative-flow");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<CumulativeFlowResponse>(response);
        Assert.Empty(payload.Snapshots);
        Assert.Equal(expectedFrom, payload.RangeFrom);
        Assert.Equal(expectedTo, payload.RangeTo);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CumulativeFlow_PopulatedProject_ReturnsOrderedSnapshotsWithPerStageCounts()
    {
        // Seed three consecutive daily snapshots with distinct per-stage
        // counts so the test can prove (a) the endpoint reads the
        // snapshot table directly, (b) the rows come back ordered
        // oldest-first, and (c) every per-stage count is carried on
        // each snapshot.
        var project = await CreateProjectAsync($"cf-pop-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();
        var day0 = now.UtcDateTime.Date.AddDays(-2);
        var day1 = now.UtcDateTime.Date.AddDays(-1);
        var day2 = now.UtcDateTime.Date;
        var day0String = day0.ToString("yyyy-MM-dd");
        var day1String = day1.ToString("yyyy-MM-dd");
        var day2String = day2.ToString("yyyy-MM-dd");

        await SeedStagePopulationSnapshotAsync(project.Id, day0String,
            backlog: 5, plan: 1, build: 2, check: 0, integrate: 0, done: 3);
        await SeedStagePopulationSnapshotAsync(project.Id, day1String,
            backlog: 4, plan: 2, build: 1, check: 1, integrate: 0, done: 4);
        await SeedStagePopulationSnapshotAsync(project.Id, day2String,
            backlog: 6, plan: 1, build: 3, check: 2, integrate: 1, done: 5);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/cumulative-flow");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<CumulativeFlowResponse>(response);
        Assert.Equal(3, payload.Snapshots.Length);

        // Ordered oldest-first; the window bounds are independent of
        // how many snapshots actually landed.
        Assert.Equal(day0String, payload.Snapshots[0].Day);
        Assert.Equal(day1String, payload.Snapshots[1].Day);
        Assert.Equal(day2String, payload.Snapshots[2].Day);

        Assert.Equal((5, 1, 2, 0, 0, 3),
            (payload.Snapshots[0].Backlog, payload.Snapshots[0].Plan,
             payload.Snapshots[0].Build, payload.Snapshots[0].Check,
             payload.Snapshots[0].Integrate, payload.Snapshots[0].Done));
        Assert.Equal((4, 2, 1, 1, 0, 4),
            (payload.Snapshots[1].Backlog, payload.Snapshots[1].Plan,
             payload.Snapshots[1].Build, payload.Snapshots[1].Check,
             payload.Snapshots[1].Integrate, payload.Snapshots[1].Done));
        Assert.Equal((6, 1, 3, 2, 1, 5),
            (payload.Snapshots[2].Backlog, payload.Snapshots[2].Plan,
             payload.Snapshots[2].Build, payload.Snapshots[2].Check,
             payload.Snapshots[2].Integrate, payload.Snapshots[2].Done));

        // Window bounds cover the fixed 90-day trailing window pinned
        // by the server; the consuming chart renders a fixed x-axis.
        Assert.Equal(day0.AddDays(-87).ToString("yyyy-MM-dd"), payload.RangeFrom);
        Assert.Equal(day2String, payload.RangeTo);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CumulativeFlow_SnapshotsOutsideTrailingWindow_AreExcluded()
    {
        // A snapshot older than the 90-day window must NOT appear in
        // the series; only the in-window rows come back. This proves
        // the window is the trailing 90-day pinned constant — not
        // unbounded history.
        var project = await CreateProjectAsync($"cf-window-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();
        var today = now.UtcDateTime.Date;
        var insideDay = today.AddDays(-30).ToString("yyyy-MM-dd");
        var edgeDay = today.AddDays(-89).ToString("yyyy-MM-dd");
        var outsideDay = today.AddDays(-120).ToString("yyyy-MM-dd");
        var farOutsideDay = today.AddDays(-365).ToString("yyyy-MM-dd");

        await SeedStagePopulationSnapshotAsync(project.Id, insideDay,
            backlog: 1, plan: 0, build: 0, check: 0, integrate: 0, done: 0);
        await SeedStagePopulationSnapshotAsync(project.Id, edgeDay,
            backlog: 2, plan: 0, build: 0, check: 0, integrate: 0, done: 0);
        await SeedStagePopulationSnapshotAsync(project.Id, outsideDay,
            backlog: 99, plan: 0, build: 0, check: 0, integrate: 0, done: 0);
        await SeedStagePopulationSnapshotAsync(project.Id, farOutsideDay,
            backlog: 99, plan: 0, build: 0, check: 0, integrate: 0, done: 0);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/cumulative-flow");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<CumulativeFlowResponse>(response);
        var days = payload.Snapshots.Select(s => s.Day).ToArray();
        Assert.Equal(2, days.Length);
        Assert.Contains(insideDay, days);
        Assert.Contains(edgeDay, days);
        Assert.DoesNotContain(outsideDay, days);
        Assert.DoesNotContain(farOutsideDay, days);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CumulativeFlow_UnknownProject_ReturnsNotFound()
    {
        // The 404 is produced by ProjectResolutionEndpointFilter
        // before the handler runs — the response body is the
        // filter's envelope, not the cumulative-flow payload.
        using var response = await _client.GetAsync(
            $"/api/projects/proj-cf-unknown-{Guid.NewGuid():N}/issues/metrics/cumulative-flow");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public async Task CumulativeFlow_PreservesOtherMetricsEndpoints_StageDurationStillAvailable()
    {
        // The cumulative-flow read is strictly additive — adding it
        // must not displace any existing project metrics endpoint.
        // Pick the stage-duration endpoint as the witness since it
        // shares the same route group prefix.
        var project = await CreateProjectAsync($"cf-additive-{Guid.NewGuid():N}");

        using var stageDuration = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/stage-duration");
        Assert.Equal(System.Net.HttpStatusCode.OK, stageDuration.StatusCode);

        using var cumulativeFlow = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/cumulative-flow");
        Assert.Equal(System.Net.HttpStatusCode.OK, cumulativeFlow.StatusCode);
    }

    private async Task SeedStagePopulationSnapshotAsync(
        string projectId,
        string day,
        int backlog,
        int plan,
        int build,
        int check,
        int integrate,
        int done)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        db.StagePopulationSnapshots.Add(new StagePopulationSnapshotRow
        {
            ProjectId = projectId,
            Day = day,
            Backlog = backlog,
            Plan = plan,
            Build = build,
            Check = check,
            Integrate = integrate,
            Done = done,
        });
        await db.SaveChangesAsync();
    }

    private DateTime DeliveryTimeCompletedAt() =>
        _fixture.TimeProvider.GetUtcNow().UtcDateTime.AddDays(-14);

    private async Task<ProjectDto> CreateProjectAsync(string name)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/projects", new { name }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var project = await ReadDataAsync<ProjectDto>(response);

        // Projects have no default repository by default, but the
        // issue create endpoint requires a resolvable repository.
        // Add one with a unique git URL per test to keep tests
        // independent of shared state.
        using var repoResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/repositories",
            new
            {
                name = "main",
                gitUrl = $"file://{Guid.NewGuid():N}",
                baseBranch = "trunk",
                isDefault = true,
            },
            JsonOptions);
        repoResponse.EnsureSuccessStatusCode();
        return project;
    }

    private async Task<IssueDto> CreateIssueAsync(string projectId, string title)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues",
            new { title, isDraft = false },
            JsonOptions);
        response.EnsureSuccessStatusCode();
        return await ReadDataAsync<IssueDto>(response);
    }

    private async Task SeedEventAsync(string issueId, string type, DateTimeOffset time)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var source = IssueQuerier.IssueSourcePrefix + issueId;
        var dbMax = await db.IssueEvents
            .AsNoTracking()
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .MaxAsync();
        var nextId = (dbMax ?? 0) + 1;
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
            Data = JsonDocument.Parse("null").RootElement,
            ExtensionsJson = "{}",
        });
        await db.SaveChangesAsync();
    }

    private async Task UpdateIssueUpdatedAtAsync(string issueId, DateTimeOffset updatedAt)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var issue = await db.Issues.AsNoTracking()
            .FirstOrDefaultAsync(r => r.IssueId == issueId)
            ?? throw new InvalidOperationException($"Issue {issueId} not found");
        var state = IssueStore.Deserialize(issue.State)
            ?? throw new InvalidOperationException($"Issue {issueId} state could not be deserialized");
        var updated = new DomainIssue
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
            UpdatedAt = updatedAt.UtcDateTime,
            ArchivedAt = state.ArchivedAt,
            PrerequisiteNumbers = state.PrerequisiteNumbers,
            IsDraft = state.IsDraft,
            RepositoryRef = state.RepositoryRef,
            Labels = new Dictionary<string, string>(state.Labels, StringComparer.Ordinal),
        };
        issue.State = IssueStore.Serialize(updated);
        db.Issues.Update(issue);
        await db.SaveChangesAsync();
    }

    private async Task SeedIssueWithCompletedApprovalAsync(
        string projectId,
        int number,
        string issueId,
        string workflowRunId,
        DateTimeOffset requestedAt,
        TimeSpan wait,
        string result)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var issue = new DomainIssue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = number,
            Title = "Approval metric issue",
            Status = IssueStatus.Done,
            CreatedAt = requestedAt.UtcDateTime,
            UpdatedAt = requestedAt.UtcDateTime,
            WorkflowRunId = workflowRunId,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        const string stage = "plan";
        var respondedAt = requestedAt + wait;
        var runState = new
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
                        RespondedAt = respondedAt.ToString("O"),
                    },
                }
            }
        };

        var json = JsonSerializer.Serialize(runState, JSON.Options);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            workflowRunId, json);
    }

    private async Task SeedIssueWithCompletedApprovalsAsync(
        string projectId,
        int number,
        string issueId,
        string workflowRunId,
        DateTimeOffset requestedAt,
        TimeSpan planWait,
        TimeSpan checkWait)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var issue = new DomainIssue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = number,
            Title = "Approval metric issue",
            Status = IssueStatus.Done,
            CreatedAt = requestedAt.UtcDateTime,
            UpdatedAt = requestedAt.UtcDateTime,
            WorkflowRunId = workflowRunId,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        const string planStage = "plan";
        const string checkStage = "check";
        var checkRequestedAt = requestedAt.AddHours(2);
        var runState = new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = requestedAt.AddMinutes(-5), Name = "test" },
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
                        RequestedAt = requestedAt.ToString("O"),
                        RespondedAt = (requestedAt + planWait).ToString("O"),
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

        var json = JsonSerializer.Serialize(runState, JSON.Options);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            workflowRunId, json);
    }

    private async Task SeedIssueWithQualityRunAsync(
        string projectId,
        int number,
        string issueId,
        string workflowRunId,
        DateTimeOffset shipTime,
        (string Stage, (string Name, string Title, int RepairCount)[] Checks)[] stages)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var issue = new DomainIssue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = number,
            Title = "Quality metric issue",
            Status = IssueStatus.Done,
            CreatedAt = shipTime.UtcDateTime,
            UpdatedAt = shipTime.UtcDateTime,
            WorkflowRunId = workflowRunId,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var source = IssueQuerier.IssueSourcePrefix + issueId;
        var dbMax = await db.IssueEvents
            .AsNoTracking()
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .MaxAsync();
        db.IssueEvents.Add(new IssueEventRow
        {
            Id = (dbMax ?? 0) + 1,
            Source = source,
            EventId = Guid.NewGuid().ToString(),
            Type = IssueQuerier.WorkCompletedType,
            Time = shipTime,
            SpecVersion = "1.0",
            Subject = number.ToString(),
            DataContentType = "application/json",
            Data = JsonSerializer.SerializeToElement(new { workflowRunId }, JSON.Options),
            ExtensionsJson = "{}",
        });
        await db.SaveChangesAsync();

        var stageObjects = stages.Select(s =>
        {
            var checks = s.Checks.Select(c => (object)new
            {
                Name = c.Name,
                Title = c.Title,
                Status = "Passed",
                RepairCount = c.RepairCount,
            }).ToArray();

            return (object)new
            {
                Id = s.Stage,
                Attempt = 1,
                RequiresApproval = false,
                Initialized = true,
                Status = "Completed",
                Tasks = new[]
                {
                    new { Id = $"{s.Stage}-task", DefinitionId = $"{s.Stage}-task", Attempt = 1, Title = $"{s.Stage} task", Status = "Completed", Uses = "mohist/acp-agent" },
                },
                Checks = checks,
            };
        }).ToArray();

        var runState = new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = shipTime.AddMinutes(-5), Name = "test" },
            Status = "Completed",
            CurrentStageId = stages.Last().Stage,
            Stages = stageObjects,
        };

        var json = JsonSerializer.Serialize(runState, JSON.Options);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            workflowRunId, json);
    }

    private async Task SeedDeliveredIssueWithCyclesAsync(
        string projectId,
        int number,
        string issueId,
        DateTime createdAt,
        DateTime completedAt,
        IReadOnlyList<DateTimeOffset> workStartTimes)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var issue = new DomainIssue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = number,
            Title = "Delivery time metric issue",
            Status = IssueStatus.Done,
            CreatedAt = createdAt,
            UpdatedAt = completedAt,
            CompletedAt = completedAt,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var source = IssueQuerier.IssueSourcePrefix + issueId;
        var dbMax = await db.IssueEvents
            .AsNoTracking()
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .MaxAsync();
        var nextId = (dbMax ?? 0) + 1;
        foreach (var start in workStartTimes)
        {
            db.IssueEvents.Add(new IssueEventRow
            {
                Id = nextId++,
                Source = source,
                EventId = Guid.NewGuid().ToString(),
                Type = IssueQuerier.WorkStartedType,
                Time = start,
                SpecVersion = "1.0",
                Subject = number.ToString(),
                DataContentType = "application/json",
                Data = JsonDocument.Parse("null").RootElement,
                ExtensionsJson = "{}",
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task SeedDeliveredIssueWithStageRunAsync(
        string projectId,
        int number,
        string issueId,
        string workflowRunId,
        DateTime createdAt,
        DateTime completedAt,
        DateTimeOffset shipTime,
        (string Stage, DateTimeOffset StartedAt, DateTimeOffset CompletedAt)[] stageSpans,
        TimeSpan approvalWait,
        string? issueWorkflowRunId = null)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var issue = new DomainIssue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = number,
            Title = "Stage duration metric issue",
            Status = IssueStatus.Done,
            CreatedAt = createdAt,
            UpdatedAt = completedAt,
            CompletedAt = completedAt,
            WorkflowRunId = issueWorkflowRunId ?? workflowRunId,
        };
        db.Issues.Add(new IssueRow
        {
            IssueId = issue.Id,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        // IssueWorkStarted event anchoring the cycle time. The earliest
        // stage's StageStarted timestamp is the natural candidate.
        var firstStageStart = stageSpans[0].StartedAt;
        var source = IssueQuerier.IssueSourcePrefix + issueId;
        var dbMax = await db.IssueEvents.AsNoTracking()
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .MaxAsync();
        var nextId = (dbMax ?? 0) + 1;
        db.IssueEvents.Add(new IssueEventRow
        {
            Id = nextId++,
            Source = source,
            EventId = Guid.NewGuid().ToString(),
            Type = IssueQuerier.WorkStartedType,
            Time = firstStageStart,
            SpecVersion = "1.0",
            Subject = number.ToString(),
            DataContentType = "application/json",
            Data = IssueEventSerializer.ToData(new IssueWorkStarted(workflowRunId)),
            ExtensionsJson = "{}",
        });
        db.IssueEvents.Add(new IssueEventRow
        {
            Id = nextId++,
            Source = source,
            EventId = Guid.NewGuid().ToString(),
            Type = IssueQuerier.WorkCompletedType,
            Time = shipTime,
            SpecVersion = "1.0",
            Subject = number.ToString(),
            DataContentType = "application/json",
            Data = IssueEventSerializer.ToData(new IssueWorkCompleted(workflowRunId)),
            ExtensionsJson = "{}",
        });
        await db.SaveChangesAsync();

        // Build a workflow run with one approval gate on the first
        // stage. The approval's requestedAt sits inside the first
        // stage's window; respondedAt = requestedAt + approvalWait.
        var firstStage = stageSpans[0];
        var approvalRequestedAt = firstStage.StartedAt;
        var approvalRespondedAt = approvalRequestedAt + approvalWait;
        var stageObjects = stageSpans.Select((s, idx) => (object)new
        {
            Id = s.Stage,
            Attempt = 1,
            RequiresApproval = idx == 0,
            Status = "Completed",
            Tasks = new[]
            {
                new { Id = $"{s.Stage}-task", DefinitionId = $"{s.Stage}-task", Attempt = 1, Title = $"{s.Stage} task", Status = "Completed", Uses = "mohist/acp-agent" },
            },
            Checks = idx == 0
                ? new[] { new { Name = $"{s.Stage}-ok", Title = $"{s.Stage} ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" } }
                : new object[0],
            ApprovalStatus = idx == 0
                ? new
                {
                    Result = "approved",
                    RequestedAt = approvalRequestedAt.ToString("O"),
                    RespondedAt = approvalRespondedAt.ToString("O"),
                }
                : null,
        }).ToArray();

        var runState = new
        {
            Id = workflowRunId,
            Metadata = new { CreatedAt = createdAt.AddMinutes(-5), Name = "test" },
            Status = "Completed",
            CurrentStageId = stageSpans[^1].Stage,
            Stages = stageObjects,
        };
        var json = JsonSerializer.Serialize(runState, JSON.Options);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
            workflowRunId, json);

        // Per-run stage events (StageStarted / StageCompleted).
        var seq = 1L;
        foreach (var s in stageSpans)
        {
            db.WorkflowRunEvents.Add(new WorkflowRunEventRow
            {
                Id = seq++,
                Source = WorkflowRunEventPersistence.WorkflowRunSource(workflowRunId),
                EventId = Guid.NewGuid().ToString(),
                Type = EventCatalog.ReverseDns.StageStarted,
                Time = s.StartedAt,
                SpecVersion = "1.0",
                Subject = null,
                DataContentType = "application/json",
                Data = JsonSerializer.SerializeToElement(new { stage = s.Stage }, JSON.Options),
                ExtensionsJson = "{}",
            });
            db.WorkflowRunEvents.Add(new WorkflowRunEventRow
            {
                Id = seq++,
                Source = WorkflowRunEventPersistence.WorkflowRunSource(workflowRunId),
                EventId = Guid.NewGuid().ToString(),
                Type = EventCatalog.ReverseDns.StageCompleted,
                Time = s.CompletedAt,
                SpecVersion = "1.0",
                Subject = null,
                DataContentType = "application/json",
                Data = JsonSerializer.SerializeToElement(new { stage = s.Stage }, JSON.Options),
                ExtensionsJson = "{}",
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(JsonOptions);
        if (envelope is null) throw new InvalidOperationException("Empty API response");
        if (!envelope.Success) throw new InvalidOperationException(envelope.Error ?? "API request failed");
        return envelope.Data!;
    }

    private sealed record ApiEnvelope<T>(bool Success, T? Data, string? Error = null, string? Code = null, object? Details = null);
    private sealed record ProjectDto(string Id, string Name);
    private sealed record IssueDto(string Id, int Number, string Title);

    private sealed record CompletionMetricsBucketDto(string Boundary, int Completed, int Failed);
    private sealed record CompletionMetricsWindowDto(string From, string To);
    private sealed record CompletionMetricsTotalsDto(int Completed, int Failed, int SampleCount);
    private sealed record CompletionMetricsResponse(
        string Bucket,
        CompletionMetricsWindowDto Window,
        CompletionMetricsBucketDto[] Buckets,
        CompletionMetricsTotalsDto CurrentTotal,
        CompletionMetricsTotalsDto PreviousTotal);

    private sealed record ApprovalWaitMetricsWindowDto(string From, string To);
    private sealed record ApprovalWaitMetricsResponse(
        ApprovalWaitMetricsWindowDto Window,
        int SampleCount,
        double? AverageSeconds,
        double? MedianSeconds,
        double? MaxSeconds);

    private sealed record QualityMetricsWindowDto(string From, string To, int SampleCount, double? FirstTimeRightRate, StageReworkRateDto[] Stages);
    private sealed record StageReworkRateDto(string Stage, int EnteredCount, double? ReworkRate);
    private sealed record QualityTrendPointDto(string Boundary, int SampleCount, double? FirstTimeRightRate, double? ReworkRate);
    private sealed record QualityTrendDto(string Bucket, string From, string To, QualityTrendPointDto[] Points);
    private sealed record QualityMetricsResponse(
        QualityMetricsWindowDto Window7d,
        QualityMetricsWindowDto Window30d,
        double? PreviousFirstTimeRightRate,
        int PreviousSampleCount,
        QualityTrendDto Trend);

    private sealed record DeliveryTimePointDto(int IssueNumber, string CompletedAt, double LeadDays, double? CycleDays);
    private sealed record DeliveryTimeMetricsResponse(DeliveryTimePointDto[] Points, double? PreviousCycleDays);

    private sealed record StageDurationStageDto(string Stage, int SampleCount, double? AverageSeconds, double? MedianSeconds);
    private sealed record StageDurationWaitBreakoutDto(double? AverageApprovalGateWaitSeconds, double? AverageInactiveGapSeconds);
    private sealed record StageDurationMetricsResponse(
        StageDurationMetricsWindowDto Window,
        StageDurationStageDto[] Stages,
        double? FlowEfficiencyRatio,
        StageDurationWaitBreakoutDto? WaitBreakout);
    private sealed record StageDurationMetricsWindowDto(string From, string To);

    private sealed record CumulativeFlowDayDto(string Day, int Backlog, int Plan, int Build, int Check, int Integrate, int Done);
    private sealed record CumulativeFlowResponse(CumulativeFlowDayDto[] Snapshots, string RangeFrom, string RangeTo);
}
