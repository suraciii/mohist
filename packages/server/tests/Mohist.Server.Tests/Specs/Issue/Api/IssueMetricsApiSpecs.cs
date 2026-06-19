using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
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
}
