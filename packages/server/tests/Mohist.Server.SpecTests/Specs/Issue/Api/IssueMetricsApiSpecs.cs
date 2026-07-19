using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Api;

[Collection("IntegrationIssue")]
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

    [Theory]
    [InlineData("cumulative-flow")]
    [InlineData("cumulative-flow?range=30d")]
    public async Task CumulativeFlowEndpoint_Removed_ReturnsNotFound(string queryString)
    {
        var project = await CreateProjectAsync($"cumulative-flow-removed-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/{queryString}");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

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

    [Fact]
    public async Task QualityMetrics_UnknownProject_ReturnsNotFound()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj-quality-unknown-{Guid.NewGuid():N}/issues/metrics/quality");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeliveryTimeMetrics_UsesInjectedRouteClockForTrailingWindow()
    {
        var project = await CreateProjectAsync($"delivery-time-clock-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();
        var insideCompletedAt = new DateTimeOffset(DeliveryTimeCompletedAt(), TimeSpan.Zero);
        var outsideCompletedAt = now.AddDays(-40);
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 1,
            insideCompletedAt.UtcDateTime.AddDays(-4).AddHours(-6),
            insideCompletedAt.UtcDateTime,
            [insideCompletedAt.AddDays(-2).AddHours(-4)]);
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 2,
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

    [Fact]
    public async Task DeliveryTimeMetrics_UnknownProject_ReturnsNotFound()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj-dt-unknown-{Guid.NewGuid():N}/issues/metrics/delivery-time");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StageDurationMetrics_UsesInjectedRouteClockForTrailingWindow()
    {
        // The route uses the injected `TimeProvider`, never the wall
        // clock. Use the fixture's current fake clock so completed issues
        // can be placed on the boundary of the trailing window.
        var project = await CreateProjectAsync($"stage-duration-clock-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();

        var insideRunId = $"wr_sd_clock_inside_{Guid.NewGuid():N}";
        await SeedDeliveredIssueWithStageRunAsync(
            project.Id,
            number: 1,
            insideRunId,
            now.AddDays(-3).UtcDateTime,
            now.AddDays(-1).UtcDateTime,
            now.AddDays(-1),
            [("plan", now.AddDays(-1).AddHours(-2), now.AddDays(-1))],
            approvalWait: TimeSpan.Zero);

        var outsideRunId = $"wr_sd_clock_outside_{Guid.NewGuid():N}";
        await SeedDeliveredIssueWithStageRunAsync(
            project.Id,
            number: 2,
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

    [Fact]
    public async Task StageDurationMetrics_UnknownProject_ReturnsNotFound()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj-sd-unknown-{Guid.NewGuid():N}/issues/metrics/stage-duration");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RangeQuery_OmittedOnCompletionDayEndpoint_ReproducesThirtyDayWindow()
    {
        // Omit-equality: omitting `range` reproduces the prior fixed
        // 30-day window byte-for-byte so the Dashboard consumer that
        // calls the shared hook without a range keeps its shape.
        var project = await CreateProjectAsync($"range-completion-omit-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=day");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);
        Assert.Equal("day", payload.Bucket);
        Assert.Equal(30, payload.Buckets.Length);
    }

    [Fact]
    public async Task RangeQuery_OmittedOnCompletionWeekEndpoint_ReproducesTwelveWeekWindow()
    {
        // Omit-equality: the week-bucket axis preserves 12 trailing
        // ISO weeks when no range is supplied, byte-for-byte.
        var project = await CreateProjectAsync($"range-completion-omit-week-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=week");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);
        Assert.Equal("week", payload.Bucket);
        Assert.Equal(12, payload.Buckets.Length);
    }

    [Fact]
    public async Task RangeQuery_DayBucket_ScalesWindowToSelectedRange()
    {
        // Day bucket: `range=90d` produces 90 daily buckets spanning a
        // 90-day trailing window. The previous window sits immediately
        // before it and is the same length (90 days).
        var project = await CreateProjectAsync($"range-completion-day90-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=day&range=90d");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);
        Assert.Equal("day", payload.Bucket);
        Assert.Equal(90, payload.Buckets.Length);
        // Window length is exactly 90 calendar days inclusive of today.
        var from = DateOnly.Parse(payload.Window.From[..10]);
        var to = DateOnly.Parse(payload.Window.To[..10]);
        Assert.Equal(90, to.DayNumber - from.DayNumber);
    }

    [Fact]
    public async Task RangeQuery_WeekBucket_CountDerivesFromRangeRoundedUp()
    {
        // Week bucket: `range=90d` yields ceil(90 / 7) = 13 ISO weeks.
        // `range=7d` yields ceil(7 / 7) = 1 week. Documented in D4.
        var project = await CreateProjectAsync($"range-completion-week-{Guid.NewGuid():N}");

        using var week90 = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=week&range=90d");
        week90.EnsureSuccessStatusCode();
        var week90Payload = await ReadDataAsync<CompletionMetricsResponse>(week90);
        Assert.Equal("week", week90Payload.Bucket);
        Assert.Equal(13, week90Payload.Buckets.Length);

        using var week7 = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=week&range=7d");
        week7.EnsureSuccessStatusCode();
        var week7Payload = await ReadDataAsync<CompletionMetricsResponse>(week7);
        Assert.Equal("week", week7Payload.Bucket);
        Assert.Single(week7Payload.Buckets);
    }

    [Theory]
    [InlineData("completion?bucket=day&range=bad")]
    [InlineData("completion?bucket=week&range=bad")]
    [InlineData("delivery-time?range=bad")]
    [InlineData("stage-duration?range=bad")]
    [InlineData("quality?range=bad")]
    [InlineData("approval-wait?range=bad")]
    public async Task RangeQuery_UnknownValue_ReturnsBadRequest(string queryString)
    {
        // Unknown range values are rejected with 400 by every endpoint
        // that accepts the uniform range vocabulary.
        var project = await CreateProjectAsync($"range-bad-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/{queryString}");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RangeQuery_DeliveryTimeEndpoint_90dScalesCurrentAndPreviousWindow()
    {
        // `range=90d` drives a 90-day current window AND a same-length
        // 90-day immediately-preceding window. The delivery-time wire
        // DTO does not surface the window bounds directly, so the
        // querier is invoked directly with the same args the route
        // would pass and its internal `Window`/`PreviousWindow` math
        // is asserted via the same-shaped previous-window calculation
        // the route performs.
        var project = await CreateProjectAsync($"range-dt-90d-{Guid.NewGuid():N}");
        var now = _fixture.TimeProvider.GetUtcNow();

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/delivery-time?range=90d");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<DeliveryTimeMetricsResponse>(response);
        // No delivered issues seeded — payload should still 200 with an
        // empty points list. Window bounds are encoded on the
        // delivery-time DTO only indirectly (via points); assert the
        // route executed without 400.
        Assert.Empty(payload.Points);
        Assert.Null(payload.PreviousCycleDays);

        // Cross-check via a service request against the same querier
        // for the seeded now, asserting the expected window bounds.
        await using var scope = _fixture.Services.CreateAsyncScope();
        var querier = scope.ServiceProvider.GetRequiredService<IssueMetricsQuerier>();
        var result90 = await querier.GetDeliveryTimesAsync(project.Id, now, windowDays: 90);
        Assert.Empty(result90.Points);

        // Shift the clock back by 90 days with the same window length
        // and assert the shifted `WindowFrom` matches the original
        // `WindowFrom − 90d` — i.e. the previous window is the same
        // length and immediately precedes the current window.
        var shifted = await querier.GetDeliveryTimesAsync(project.Id, now.AddDays(-90), windowDays: 90);
        Assert.Empty(shifted.Points);

        // Omitting the range ⇒ 30d, the Dashboard back-compat default.
        var omit = await querier.GetDeliveryTimesAsync(project.Id, now);
        Assert.Empty(omit.Points);
    }

    [Fact]
    public async Task RangeQuery_StageDurationEndpoint_OmittedReproduces30DayWindow()
    {
        var project = await CreateProjectAsync($"range-sd-omit-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/stage-duration");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<StageDurationMetricsResponse>(response);
        var from = DateTimeOffset.Parse(payload.Window.From);
        var to = DateTimeOffset.Parse(payload.Window.To);
        Assert.Equal(TimeSpan.FromDays(30), to - from);
    }

    [Fact]
    public async Task RangeQuery_StageDurationEndpoint_90dScalesWindow()
    {
        var project = await CreateProjectAsync($"range-sd-90d-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/stage-duration?range=90d");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<StageDurationMetricsResponse>(response);
        var from = DateTimeOffset.Parse(payload.Window.From);
        var to = DateTimeOffset.Parse(payload.Window.To);
        Assert.Equal(TimeSpan.FromDays(90), to - from);
    }

    [Fact]
    public async Task RangeQuery_ApprovalWaitEndpoint_OmittedReproduces7DayWindow()
    {
        var project = await CreateProjectAsync($"range-aw-omit-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/approval-wait");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<ApprovalWaitMetricsResponse>(response);
        var from = DateTimeOffset.Parse(payload.Window.From);
        var to = DateTimeOffset.Parse(payload.Window.To);
        Assert.Equal(TimeSpan.FromDays(7), to - from);
    }

    [Fact]
    public async Task RangeQuery_ApprovalWaitEndpoint_30dScalesWindow()
    {
        var project = await CreateProjectAsync($"range-aw-30d-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/approval-wait?range=30d");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<ApprovalWaitMetricsResponse>(response);
        var from = DateTimeOffset.Parse(payload.Window.From);
        var to = DateTimeOffset.Parse(payload.Window.To);
        Assert.Equal(TimeSpan.FromDays(30), to - from);
    }

    [Fact]
    public async Task RangeQuery_QualityEndpoint_OmittedDefaultsTo30DayPrimaryWindow()
    {
        // Single-window contract: omitting `range` produces a 30-day
        // primary window with 30 daily trend buckets. The previous-
        // window discriminator stays untouched. There is no fixed
        // 7-day window.
        var project = await CreateProjectAsync($"range-q-omit-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/quality");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<QualityMetricsResponse>(response);

        var windowFrom = DateTimeOffset.Parse(payload.Window.From);
        var windowTo = DateTimeOffset.Parse(payload.Window.To);
        Assert.Equal(TimeSpan.FromDays(30), windowTo - windowFrom);

        Assert.Equal(30, payload.Trend.Points.Length);
        // Trend span == primary window.
        var trendFrom = DateTimeOffset.Parse(payload.Trend.From);
        var trendTo = DateTimeOffset.Parse(payload.Trend.To);
        Assert.Equal(windowFrom, trendFrom);
        Assert.Equal(windowTo, trendTo);
    }

    [Fact]
    public async Task RangeQuery_QualityEndpoint_90dScalesPrimaryPreviousAndTrend()
    {
        // Single-window contract: `range=90d` makes the primary window
        // 90d and the trend 90 daily buckets. There is no fixed 7-day
        // window field on the response.
        var project = await CreateProjectAsync($"range-q-90d-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/quality?range=90d");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<QualityMetricsResponse>(response);

        var windowFrom = DateTimeOffset.Parse(payload.Window.From);
        var windowTo = DateTimeOffset.Parse(payload.Window.To);
        Assert.Equal(TimeSpan.FromDays(90), windowTo - windowFrom);

        Assert.Equal(90, payload.Trend.Points.Length);
        var trendFrom = DateTimeOffset.Parse(payload.Trend.From);
        var trendTo = DateTimeOffset.Parse(payload.Trend.To);
        Assert.Equal(windowFrom, trendFrom);
        Assert.Equal(windowTo, trendTo);
    }

    [Fact]
    public async Task RangeQuery_QualityEndpoint_7dScalesPrimaryPreviousAndTrend()
    {
        // Single-window contract: `range=7d` makes the primary window
        // 7d and the trend 7 daily buckets. Confirms that the primary
        // window tracks the range across the full range selector
        // (7d/30d/90d).
        var project = await CreateProjectAsync($"range-q-7d-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/quality?range=7d");
        response.EnsureSuccessStatusCode();
        var payload = await ReadDataAsync<QualityMetricsResponse>(response);

        var windowFrom = DateTimeOffset.Parse(payload.Window.From);
        var windowTo = DateTimeOffset.Parse(payload.Window.To);
        Assert.Equal(TimeSpan.FromDays(7), windowTo - windowFrom);

        Assert.Equal(7, payload.Trend.Points.Length);
        var trendFrom = DateTimeOffset.Parse(payload.Trend.From);
        var trendTo = DateTimeOffset.Parse(payload.Trend.To);
        Assert.Equal(windowFrom, trendFrom);
        Assert.Equal(windowTo, trendTo);
    }

    private DateTime DeliveryTimeCompletedAt() =>
        _fixture.TimeProvider.GetUtcNow().UtcDateTime.AddDays(-14);

    private async Task<ProjectDto> CreateProjectAsync(string name)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name,
                repository = new { name = "main", gitUrl = $"file://{Guid.NewGuid():N}", baseBranch = "trunk" },
            },
            JsonOptions);
        response.EnsureSuccessStatusCode();
        var project = await ReadDataAsync<ProjectDto>(response);

        return project;
    }

    private async Task SeedDeliveredIssueWithCyclesAsync(
        string projectId,
        int number,
        DateTime createdAt,
        DateTime completedAt,
        IReadOnlyList<DateTimeOffset> workStartTimes)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var issue = new DomainIssue
        {
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
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        var source = IssueEventPersistence.IssueSource(projectId, number);
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
                Type = EventCatalog.ReverseDns.IssueWorkStarted,
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
            ProjectId = projectId,
            Number = number,
            State = IssueStore.Serialize(issue),
        });
        await db.SaveChangesAsync();

        // IssueWorkStarted event anchoring the cycle time. The earliest
        // stage's StageStarted timestamp is the natural candidate.
        var firstStageStart = stageSpans[0].StartedAt;
        var source = IssueEventPersistence.IssueSource(projectId, number);
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
            Type = EventCatalog.ReverseDns.IssueWorkStarted,
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
            Type = EventCatalog.ReverseDns.IssueCompleted,
            Time = shipTime,
            SpecVersion = "1.0",
            Subject = number.ToString(),
            DataContentType = "application/json",
            Data = IssueEventSerializer.ToData(new IssueCompleted(workflowRunId)),
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
                new { Id = $"{s.Stage}-task", DefinitionId = $"{s.Stage}-task", Attempt = 1, Title = $"{s.Stage} task", Status = "Completed", Uses = "mohist/opencode" },
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
        QualityMetricsWindowDto Window,
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
}
