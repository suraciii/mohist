using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Inbox;
using Mohist.Server.Issue.Domain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Api;

[Collection("IntegrationApi")]
public sealed class IssueLowBandwidthApiSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public IssueLowBandwidthApiSpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ParentCandidates_ReturnOnlyEligibleNumberAndTitle()
    {
        var projectId = await CreateProjectAsync("parent-candidates");
        const int parentNumber = 1;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
            var issue = new DomainIssue
            {
                ProjectId = projectId,
                Number = parentNumber,
                Title = "Eligible parent",
                Status = IssueStatus.Backlog,
                Priority = "p2",
                CreatedAt = TestTime.UtcDateTime,
                UpdatedAt = TestTime.UtcDateTime,
            };
            db.Issues.Add(new IssueRow
            {
                ProjectId = projectId,
                Number = parentNumber,
                Status = "backlog",
                IsArchived = false,
                State = IssueStore.Serialize(issue),
            });
            await db.SaveChangesAsync();
        }

        var candidates = await _fixture.Client.GetDataAsync<JsonElement[]>(
            $"/api/projects/{projectId}/issues/parent-candidates");

        var candidate = Assert.Single(candidates);
        Assert.Equal(parentNumber, candidate.GetProperty("number").GetInt32());
        Assert.Equal("Eligible parent", candidate.GetProperty("title").GetString());
        Assert.Equal(new[] { "number", "title" }, candidate.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task ParentCandidates_UnknownProjectReturns404()
    {
        using var response = await _fixture.Client.GetAsync(
            "/api/projects/project_does_not_exist/issues/parent-candidates");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnreadCount_IsProjectScopedAndContainsOnlyCount()
    {
        var projectId = await CreateProjectAsync("inbox-count");
        var otherProjectId = await CreateProjectAsync("inbox-count-other");
        var unreadId = await SeedInboxAsync(projectId, "unread", "count-unread");
        var readId = await SeedInboxAsync(projectId, "read", "count-read");
        await SeedInboxAsync(otherProjectId, "other", "count-other");

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<InboxStore>();
            await store.MarkReadAsync(projectId, readId);
        }

        var payload = await _fixture.Client.GetDataAsync<JsonElement>(
            $"/api/projects/{projectId}/inbox/unread-count");

        Assert.Equal(1, payload.GetProperty("unreadCount").GetInt32());
        Assert.Equal(new[] { "unreadCount" }, payload.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.NotEqual(unreadId, readId);
    }

    [Fact]
    public async Task UnreadCount_UnknownProjectReturns404()
    {
        using var response = await _fixture.Client.GetAsync(
            "/api/projects/project_does_not_exist/inbox/unread-count");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StaticAssetsUseImmutableCacheAndBrotli()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/assets/app-12345678.css");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "public,max-age=31536000,immutable",
            response.Headers.CacheControl?.ToString().Replace(" ", "", StringComparison.Ordinal));
        Assert.Contains("br", response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task HtmlFallbackIsNoCacheAndUnknownApiIs404()
    {
        using var fallback = await _fixture.Client.GetAsync("/project-1/unknown-page");
        using var api = await _fixture.Client.GetAsync("/api/issue-473-missing");

        Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
        Assert.Equal("no-cache", fallback.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
    }

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var project = await _fixture.Client.CreateProjectWithDefaultRepositoryAsync<JsonElement>(
            "/api/projects",
            $"{prefix}-{Guid.NewGuid():N}");
        return project.GetProperty("id").GetString()!;
    }

    private async Task<string> SeedInboxAsync(string projectId, string title, string eventId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<InboxStore>();
        var result = await store.InsertAsync(new InboxItemDraft(
            ProjectId: projectId,
            IssueNumber: 1,
            IssueTitle: title,
            NotificationKind: NotificationKinds.WorkflowFailed,
            SourceEventSource: $"/mohist/projects/{projectId}/issues/1",
            SourceEventId: eventId,
            CreatedAt: TestTime.UtcNow));
        return result.Id;
    }
}
