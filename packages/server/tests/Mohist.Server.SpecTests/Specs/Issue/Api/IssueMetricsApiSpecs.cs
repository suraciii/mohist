using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
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

    [Fact]
    public async Task CumulativeFlowEndpoint_Removed_ReturnsNotFound()
    {
        var project = await CreateProjectAsync($"cumulative-flow-removed-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/cumulative-flow");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CompletionMetrics_DefaultDayBucket_SerializesContract()
    {
        var project = await CreateProjectAsync($"metrics-day-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);
        Assert.Equal("day", payload.Bucket);
        Assert.Equal(30, payload.Buckets.Length);
        Assert.Equal(payload.Window.From[..10], payload.Buckets[0].Boundary);
        Assert.Equal(
            DateOnly.Parse(payload.Window.To[..10]).AddDays(-1).ToString("yyyy-MM-dd"),
            payload.Buckets[^1].Boundary);
        Assert.Equal(new CompletionMetricsTotalsDto(0, 0, 0), payload.CurrentTotal);
        Assert.Equal(new CompletionMetricsTotalsDto(0, 0, 0), payload.PreviousTotal);
    }

    [Fact]
    public async Task CompletionMetrics_WeekBucket_RangeOverride_SerializesContract()
    {
        var project = await CreateProjectAsync($"metrics-week-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=week&range=90d");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<CompletionMetricsResponse>(response);
        Assert.Equal("week", payload.Bucket);
        Assert.Equal(13, payload.Buckets.Length);
        Assert.Equal(new CompletionMetricsTotalsDto(0, 0, 0), payload.CurrentTotal);
        Assert.Equal(new CompletionMetricsTotalsDto(0, 0, 0), payload.PreviousTotal);
    }

    [Fact]
    public async Task CompletionMetrics_UnsupportedBucket_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync($"metrics-bad-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?bucket=month");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApprovalWaitMetrics_EmptyResult_SerializesNullableStats()
    {
        var project = await CreateProjectAsync($"approval-wait-empty-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/approval-wait");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<ApprovalWaitMetricsResponse>(response);
        Assert.Equal(_fixture.TimeProvider.GetUtcNow(), DateTimeOffset.Parse(payload.Window.To));
        Assert.Equal(0, payload.SampleCount);
        Assert.Null(payload.AverageSeconds);
        Assert.Null(payload.MedianSeconds);
        Assert.Null(payload.MaxSeconds);
    }

    [Fact]
    public async Task QualityMetrics_EmptyResult_SerializesNullRatesAndTrend()
    {
        var project = await CreateProjectAsync($"quality-empty-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/quality");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<QualityMetricsResponse>(response);
        Assert.Equal(0, payload.Window.SampleCount);
        Assert.Null(payload.Window.FirstTimeRightRate);
        Assert.Equal(0, payload.PreviousSampleCount);
        Assert.Null(payload.PreviousFirstTimeRightRate);
        Assert.Equal("day", payload.Trend.Bucket);
        Assert.Equal(30, payload.Trend.Points.Length);
        Assert.All(payload.Trend.Points, point =>
        {
            Assert.Equal(0, point.SampleCount);
            Assert.Null(point.FirstTimeRightRate);
            Assert.Null(point.ReworkRate);
        });
    }

    [Fact]
    public async Task DeliveryTimeMetrics_CycleNullAndZero_SerializeDistinctly()
    {
        var project = await CreateProjectAsync($"delivery-time-{Guid.NewGuid():N}");
        var completedAt = _fixture.TimeProvider.GetUtcNow().AddDays(-14).UtcDateTime;

        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 1,
            issueId: $"issue_dt_missing_{Guid.NewGuid():N}",
            createdAt: completedAt.AddDays(-3),
            completedAt,
            workStartTimes: []);
        await SeedDeliveredIssueWithCyclesAsync(
            project.Id,
            number: 2,
            issueId: $"issue_dt_zero_{Guid.NewGuid():N}",
            createdAt: completedAt.AddDays(-4),
            completedAt,
            workStartTimes: [new DateTimeOffset(completedAt, TimeSpan.Zero)]);

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/delivery-time");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<DeliveryTimeMetricsResponse>(response);
        var missing = Assert.Single(payload.Points, point => point.IssueNumber == 1);
        var zero = Assert.Single(payload.Points, point => point.IssueNumber == 2);
        Assert.Null(missing.CycleDays);
        Assert.Equal(0, zero.CycleDays);
    }

    [Fact]
    public async Task StageDurationMetrics_DeliveredIssue_SerializesAggregates()
    {
        var project = await CreateProjectAsync($"stage-duration-{Guid.NewGuid():N}");
        var shipTime = _fixture.TimeProvider.GetUtcNow().AddDays(-2);
        var workflowRunId = $"wr_sd_{Guid.NewGuid():N}";

        await SeedDeliveredIssueWithStageRunAsync(
            project.Id,
            number: 1,
            issueId: $"issue_sd_{Guid.NewGuid():N}",
            workflowRunId,
            createdAt: shipTime.AddHours(-10).UtcDateTime,
            completedAt: shipTime.UtcDateTime,
            shipTime,
            stageSpans:
            [
                ("plan", shipTime.AddHours(-10), shipTime.AddHours(-7)),
                ("build", shipTime.AddHours(-7), shipTime.AddHours(-3)),
            ],
            approvalWait: TimeSpan.FromHours(1));

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/stage-duration");
        response.EnsureSuccessStatusCode();

        var payload = await ReadDataAsync<StageDurationMetricsResponse>(response);
        var plan = Assert.Single(payload.Stages, stage => stage.Stage == "plan");
        var build = Assert.Single(payload.Stages, stage => stage.Stage == "build");
        Assert.Equal(3 * 3600, plan.AverageSeconds);
        Assert.Equal(4 * 3600, build.AverageSeconds);
        Assert.Equal(0.6, payload.FlowEfficiencyRatio);
        Assert.Equal(3600, payload.WaitBreakout.AverageApprovalGateWaitSeconds);
        Assert.Equal(3 * 3600, payload.WaitBreakout.AverageInactiveGapSeconds);
    }

    [Fact]
    public async Task MetricsRange_UnknownValue_ReturnsBadRequest()
    {
        var project = await CreateProjectAsync($"range-bad-{Guid.NewGuid():N}");

        using var response = await _client.GetAsync(
            $"/api/projects/{project.Id}/issues/metrics/completion?range=bad");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MetricsEndpoint_UnknownProject_ReturnsNotFound()
    {
        using var response = await _client.GetAsync(
            $"/api/projects/proj-metrics-unknown-{Guid.NewGuid():N}/issues/metrics/quality");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<ProjectDto> CreateProjectAsync(string name)
    {
        using var response = await _client.PostAsJsonAsync("/api/projects", new { name }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var project = await ReadDataAsync<ProjectDto>(response);

        using var repositoryResponse = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/repositories",
            new
            {
                name = "main",
                gitUrl = $"file://{Guid.NewGuid():N}",
                baseBranch = "trunk",
                isDefault = true,
            },
            JsonOptions);
        repositoryResponse.EnsureSuccessStatusCode();
        return project;
    }

    private async Task SeedDeliveredIssueWithCyclesAsync(
        string projectId,
        int number,
        string issueId,
        DateTime createdAt,
        DateTime completedAt,
        IReadOnlyList<DateTimeOffset> workStartTimes)
    {
        await _fixture.UseDbAsync(async db =>
        {
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

            var source = IssueMetricsQuerier.IssueSourcePrefix + issueId;
            var nextId = (await db.IssueEvents
                .AsNoTracking()
                .Where(eventRow => eventRow.Source == source)
                .Select(eventRow => (long?)eventRow.Id)
                .MaxAsync() ?? 0) + 1;
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
        });
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
        TimeSpan approvalWait)
    {
        await _fixture.UseDbAsync(async db =>
        {
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
                WorkflowRunId = workflowRunId,
            };
            db.Issues.Add(new IssueRow
            {
                IssueId = issue.Id,
                State = IssueStore.Serialize(issue),
            });

            var source = IssueMetricsQuerier.IssueSourcePrefix + issueId;
            db.IssueEvents.AddRange(
                new IssueEventRow
                {
                    Id = 1,
                    Source = source,
                    EventId = Guid.NewGuid().ToString(),
                    Type = EventCatalog.ReverseDns.IssueWorkStarted,
                    Time = stageSpans[0].StartedAt,
                    SpecVersion = "1.0",
                    Subject = number.ToString(),
                    DataContentType = "application/json",
                    Data = IssueEventSerializer.ToData(new IssueWorkStarted(workflowRunId)),
                    ExtensionsJson = "{}",
                },
                new IssueEventRow
                {
                    Id = 2,
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

            var firstStage = stageSpans[0];
            var stageObjects = stageSpans.Select((stage, index) => (object)new
            {
                Id = stage.Stage,
                Attempt = 1,
                RequiresApproval = index == 0,
                Status = "Completed",
                Tasks = new[]
                {
                    new { Id = $"{stage.Stage}-task", DefinitionId = $"{stage.Stage}-task", Attempt = 1, Title = $"{stage.Stage} task", Status = "Completed", Uses = "mohist/acp-agent" },
                },
                Checks = index == 0
                    ? new[] { new { Name = $"{stage.Stage}-ok", Title = $"{stage.Stage} ok", Uses = "mohist/openspec-checks", Status = "Passed", Message = "ok" } }
                    : new object[0],
                ApprovalStatus = index == 0
                    ? new
                    {
                        Result = "approved",
                        RequestedAt = firstStage.StartedAt.ToString("O"),
                        RespondedAt = firstStage.StartedAt.Add(approvalWait).ToString("O"),
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
            await db.Database.ExecuteSqlRawAsync(
                "INSERT OR REPLACE INTO WorkflowRuns (WorkflowRunId, State, ETag) VALUES ({0}, {1}, 0)",
                workflowRunId,
                JsonSerializer.Serialize(runState, JSON.Options));

            var eventId = 1L;
            foreach (var stage in stageSpans)
            {
                db.WorkflowRunEvents.AddRange(
                    new WorkflowRunEventRow
                    {
                        Id = eventId++,
                        Source = WorkflowRunEventPersistence.WorkflowRunSource(workflowRunId),
                        EventId = Guid.NewGuid().ToString(),
                        Type = EventCatalog.ReverseDns.StageStarted,
                        Time = stage.StartedAt,
                        SpecVersion = "1.0",
                        DataContentType = "application/json",
                        Data = JsonSerializer.SerializeToElement(new { stage = stage.Stage }, JSON.Options),
                        ExtensionsJson = "{}",
                    },
                    new WorkflowRunEventRow
                    {
                        Id = eventId++,
                        Source = WorkflowRunEventPersistence.WorkflowRunSource(workflowRunId),
                        EventId = Guid.NewGuid().ToString(),
                        Type = EventCatalog.ReverseDns.StageCompleted,
                        Time = stage.CompletedAt,
                        SpecVersion = "1.0",
                        DataContentType = "application/json",
                        Data = JsonSerializer.SerializeToElement(new { stage = stage.Stage }, JSON.Options),
                        ExtensionsJson = "{}",
                    });
            }
            await db.SaveChangesAsync();
        });
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(JsonOptions);
        if (envelope is null || envelope.Data is null)
            throw new InvalidOperationException(envelope?.Error ?? "API request failed");
        return envelope.Data;
    }

    private sealed record ApiEnvelope<T>(bool Success, T? Data, string? Error = null);
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
    private sealed record QualityMetricsWindowDto(string From, string To, int SampleCount, double? FirstTimeRightRate);
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
        StageDurationWaitBreakoutDto WaitBreakout);
    private sealed record StageDurationMetricsWindowDto(string From, string To);
}
