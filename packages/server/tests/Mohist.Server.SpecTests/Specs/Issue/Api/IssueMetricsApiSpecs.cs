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
