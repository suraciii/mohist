using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.GitHub;

[Collection("GitHubFeed")]
public sealed class GitHubIssueFeedSpecs
{
    private const string RepoName = "hello-world";
    private const int GithubIssueNumber = 42;

    private readonly GitHubFeedFixture _fixture;

    public GitHubIssueFeedSpecs(GitHubFeedFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
    }

    private HttpClient Client => _fixture.Client;

    private async Task<(string ProjectId, string ConnectionId, string Secret, string Owner)> ConnectNewAsync(
        string? feedMode = null,
        string? intakeLabel = null)
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-feed-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var body = new Dictionary<string, object?>
        {
            ["owner"] = owner,
            ["repo"] = RepoName,
        };
        if (feedMode is not null) body["feedMode"] = feedMode;
        if (intakeLabel is not null) body["intakeLabel"] = intakeLabel;
        var created = await Client.PostDataAsync<JsonElement>($"/api/projects/{project.Id}/github-connections", body);
        return (project.Id, created.GetProperty("id").GetString()!, created.GetProperty("webhookSecret").GetString()!, owner);
    }

    private async Task DeliverAsync(string connectionId, string secret, string deliveryId, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/github-connections/{connectionId}/ingress")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", "issues");
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        request.Headers.Add("X-Hub-Signature-256", Sign(bytes, secret));
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task PumpAsync() =>
        _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

    private async Task<GitHubIssueLink?> LoadLinkAsync(string projectId, string repositoryName)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
        return await store.GetAsync(projectId, repositoryName, GithubIssueNumber);
    }

    private async Task<DomainIssue?> LoadIssueAsync(string projectId, int number)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIssueStore>();
        return await store.LoadAsync(GrainKey.Issue(new IssueKey(projectId, number)));
    }

    private async Task<int> CountIssuesAsync(string projectId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var context = await db.CreateDbContextAsync();
        return await context.Issues.CountAsync(r => r.ProjectId == projectId);
    }

    private static string Sign(byte[] payload, string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)).ToLowerInvariant();

    private static string LabeledPayload(string title, string body, params string[] labels)
    {
        var labelsJson = string.Join(",", labels.Select(l => $"{{\"name\":\"{l}\"}}"));
        return $$"""
            {
              "action": "labeled",
              "number": {{GithubIssueNumber}},
              "issue": {
                "number": {{GithubIssueNumber}},
                "title": "{{title}}",
                "body": "{{body}}",
                "state": "open",
                "labels": [ {{labelsJson}} ]
              },
              "repository": {
                "name": "hello-world",
                "full_name": "octocat/hello-world",
                "owner": { "login": "octocat" }
              }
            }
            """;
    }

    [Fact]
    public async Task IntakeLabel_CreatesAndStartsIssue_WithSnapshotPriorityRepositoryAndSource()
    {
        var (projectId, connectionId, secret, owner) = await ConnectNewAsync();

        await DeliverAsync(connectionId, secret, "feed-delivery-1", LabeledPayload("Fix the bug", "Steps to reproduce", "mohist", "p1"));
        await PumpAsync();

        var link = await LoadLinkAsync(projectId, RepoName);
        Assert.NotNull(link);
        var issue = await LoadIssueAsync(projectId, link!.IssueNumber);
        Assert.NotNull(issue);
        Assert.Equal("Fix the bug", issue.Title);
        Assert.Equal("Steps to reproduce", issue.Body);
        Assert.Equal("p1", issue.Priority);
        Assert.Equal(RepoName, issue.RepositoryRef);
        Assert.Equal(IssueStatus.InProgress, issue.Status);
        Assert.False(string.IsNullOrWhiteSpace(issue.WorkflowRunId));
        Assert.True(issue.Labels.TryGetValue(GitHubIssueSource.LabelKey, out var source));
        Assert.Equal($"{owner}/{RepoName}#{GithubIssueNumber}", source);
        Assert.Empty(_fixture.Comments.Comments);
    }

    [Fact]
    public async Task IntakeLabel_WithoutPriorityLabel_UsesDefaultPriority()
    {
        var (projectId, connectionId, secret, _) = await ConnectNewAsync();

        await DeliverAsync(connectionId, secret, "feed-delivery-default-priority", LabeledPayload("No priority", "body", "mohist"));
        await PumpAsync();

        var link = await LoadLinkAsync(projectId, RepoName);
        var issue = await LoadIssueAsync(projectId, link!.IssueNumber);
        Assert.Equal(IssuePriority.Default.Value, issue!.Priority);
    }

    [Fact]
    public async Task FeedModeBacklog_LeavesIssueInBacklog_WithoutComment()
    {
        var (projectId, connectionId, secret, _) = await ConnectNewAsync(feedMode: "backlog");

        await DeliverAsync(connectionId, secret, "feed-delivery-backlog", LabeledPayload("Backlog feed", "body", "mohist"));
        await PumpAsync();

        var link = await LoadLinkAsync(projectId, RepoName);
        var issue = await LoadIssueAsync(projectId, link!.IssueNumber);
        Assert.Equal(IssueStatus.Backlog, issue!.Status);
        Assert.Null(issue.WorkflowRunId);
        Assert.Empty(_fixture.Comments.Comments);
    }

    [Fact]
    public async Task DuplicateDelivery_DoesNotCreateSecondIssue()
    {
        var (projectId, connectionId, secret, _) = await ConnectNewAsync();

        await DeliverAsync(connectionId, secret, "feed-delivery-dup-a", LabeledPayload("Dupe", "body", "mohist"));
        await DeliverAsync(connectionId, secret, "feed-delivery-dup-b", LabeledPayload("Dupe", "body", "mohist"));
        await PumpAsync();
        await PumpAsync();

        var link = await LoadLinkAsync(projectId, RepoName);
        Assert.NotNull(link);
        Assert.Equal(1, await CountIssuesAsync(projectId));
        Assert.NotNull(await LoadIssueAsync(projectId, link!.IssueNumber));
    }

    [Fact]
    public async Task RelabelAfterUnlabel_DoesNotCreateSecondIssue()
    {
        var (projectId, connectionId, secret, _) = await ConnectNewAsync();

        await DeliverAsync(connectionId, secret, "feed-delivery-relabel-a", LabeledPayload("Relabel", "body", "mohist"));
        await PumpAsync();
        await DeliverAsync(connectionId, secret, "feed-delivery-relabel-b", LabeledPayload("Relabel", "body", "mohist"));
        await PumpAsync();

        var link = await LoadLinkAsync(projectId, RepoName);
        Assert.NotNull(link);
        Assert.Equal(1, await CountIssuesAsync(projectId));
    }

    [Fact]
    public async Task NonMatchingLabel_DoesNotFeed()
    {
        var (projectId, connectionId, secret, _) = await ConnectNewAsync();

        await DeliverAsync(connectionId, secret, "feed-delivery-other", LabeledPayload("Other label", "body", "needs-review"));
        await PumpAsync();

        Assert.Null(await LoadLinkAsync(projectId, RepoName));
        Assert.Equal(0, await CountIssuesAsync(projectId));
        Assert.Empty(_fixture.Comments.Comments);
    }

    [Fact]
    public async Task CustomIntakeLabel_FeedsOnlyThatLabel()
    {
        var (projectId, connectionId, secret, _) = await ConnectNewAsync(intakeLabel: "intake");

        await DeliverAsync(connectionId, secret, "feed-delivery-custom", LabeledPayload("Custom label", "body", "intake"));
        await PumpAsync();

        var link = await LoadLinkAsync(projectId, RepoName);
        Assert.NotNull(link);
        var issue = await LoadIssueAsync(projectId, link!.IssueNumber);
        Assert.Equal(IssueStatus.InProgress, issue!.Status);
    }

    [Fact]
    public async Task StartRejected_UnmetPrerequisite_LeavesBacklogAndPostsComment()
    {
        var (projectId, connectionId, secret, _) = await ConnectNewAsync();
        var grains = _fixture.Grains;
        var prerequisiteNumber = await grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId)).NextAsync();
        await grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, prerequisiteNumber)))
            .CreateAsync(projectId, prerequisiteNumber, "Prerequisite", null, null, "p2", RepoName, isDraft: false);
        var issueNumber = await grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId)).NextAsync();
        await grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)))
            .CreateAsync(projectId, issueNumber, "Dependent", null, null, "p2", RepoName, isDraft: false, prerequisiteNumbers: [prerequisiteNumber]);
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
            await links.CreateAsync(projectId, RepoName, GithubIssueNumber, issueNumber);
        }

        await DeliverAsync(connectionId, secret, "feed-delivery-prereq", LabeledPayload("Dependent", "body", "mohist"));
        await PumpAsync();

        Assert.Equal(2, await CountIssuesAsync(projectId));
        var issue = await LoadIssueAsync(projectId, issueNumber);
        Assert.Equal(IssueStatus.Backlog, issue!.Status);
        Assert.Null(issue.WorkflowRunId);
        var comment = Assert.Single(_fixture.Comments.Comments);
        Assert.Equal(connectionId, comment.ConnectionId);
        Assert.Equal(GithubIssueNumber, comment.GithubIssueNumber);
        Assert.Contains($"#{issueNumber}", comment.Body);
        var link = await LoadLinkAsync(projectId, RepoName);
        Assert.True(link!.HasPostedComment(GitHubCommentKinds.FeedRejected));
    }

    [Fact]
    public async Task StartRejected_UnavailableRepository_LeavesBacklogAndPostsComment()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-feed-{Guid.NewGuid():N}", repoName: "primary", gitUrl: "git@example.com:primary.git");
        await Client.PostOkAsync($"/api/projects/{project.Id}/repositories", new
        {
            name = RepoName,
            gitUrl = $"https://github.com/{owner}/{RepoName}.git",
            baseBranch = "main",
        });
        var created = await Client.PostDataAsync<JsonElement>($"/api/projects/{project.Id}/github-connections", new
        {
            owner,
            repo = RepoName,
        });
        var connectionId = created.GetProperty("id").GetString()!;
        var secret = created.GetProperty("webhookSecret").GetString()!;
        using var remove = await Client.DeleteAsync($"/api/projects/{project.Id}/repositories/{RepoName}");
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);

        await DeliverAsync(connectionId, secret, "feed-delivery-no-repo", LabeledPayload("No repo", "body", "mohist"));
        await PumpAsync();

        var link = await LoadLinkAsync(project.Id, RepoName);
        Assert.NotNull(link);
        var issue = await LoadIssueAsync(project.Id, link!.IssueNumber);
        Assert.Equal(IssueStatus.Backlog, issue!.Status);
        Assert.Null(issue.WorkflowRunId);
        var comment = Assert.Single(_fixture.Comments.Comments);
        Assert.Equal(connectionId, comment.ConnectionId);
        Assert.Contains($"#{link.IssueNumber}", comment.Body);
        Assert.Contains("not found", comment.Body);
    }
}
