using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
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
        var requestedAt = DateTimeOffset.UtcNow.AddDays(-1);
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
    public async Task DeliveryTimeMetrics_DeliveredIssueWithWorkStart_ReturnsLeadAndCycle()
    {
        var project = await CreateProjectAsync($"delivery-time-present-{Guid.NewGuid():N}");
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var workStartedAt = new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero);
        var completedAt = new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc);
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
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var completedAt = new DateTime(2026, 6, 4, 14, 0, 0, DateTimeKind.Utc);
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
        var zeroMoment = new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc);
        var createdAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
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
        var insideIssueId = $"issue_dt_clock_inside_{Guid.NewGuid():N}";
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 1,
            insideIssueId,
            new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 5, 14, 0, 0, DateTimeKind.Utc),
            [new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero)]);
        var outsideIssueId = $"issue_dt_clock_outside_{Guid.NewGuid():N}";
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 2,
            outsideIssueId,
            new DateTime(2026, 4, 20, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 14, 0, 0, DateTimeKind.Utc),
            [new DateTimeOffset(2026, 4, 21, 10, 0, 0, TimeSpan.Zero)]);

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
    private sealed record CompletionMetricsResponse(string Bucket, CompletionMetricsWindowDto Window, CompletionMetricsBucketDto[] Buckets);

    private sealed record ApprovalWaitMetricsWindowDto(string From, string To);
    private sealed record ApprovalWaitMetricsResponse(
        ApprovalWaitMetricsWindowDto Window,
        int SampleCount,
        double? AverageSeconds,
        double? MedianSeconds,
        double? MaxSeconds);

    private sealed record QualityMetricsWindowDto(string From, string To, int SampleCount, double? FirstTimeRightRate, StageReworkRateDto[] Stages);
    private sealed record StageReworkRateDto(string Stage, int EnteredCount, double? ReworkRate);
    private sealed record QualityMetricsResponse(QualityMetricsWindowDto Window7d, QualityMetricsWindowDto Window30d);

    private sealed record DeliveryTimePointDto(int IssueNumber, string CompletedAt, double LeadDays, double? CycleDays);
    private sealed record DeliveryTimeMetricsResponse(DeliveryTimePointDto[] Points);
}
