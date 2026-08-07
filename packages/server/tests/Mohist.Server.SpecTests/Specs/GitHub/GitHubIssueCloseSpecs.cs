using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.GitHub.Infrastructure;
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
public sealed class GitHubIssueCloseSpecs
{
    private const string RepoName = "hello-world";
    private const int GithubIssueNumber = 42;

    private readonly GitHubFeedFixture _fixture;

    public GitHubIssueCloseSpecs(GitHubFeedFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
        fixture.Comments.StateLabels.Clear();
        fixture.Comments.Closes.Clear();
    }

    private HttpClient Client => _fixture.Client;

    private async Task<(string ProjectId, string ConnectionId, string Secret)> ConnectNewAsync(string? feedMode = null)
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-close-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var body = new Dictionary<string, object?>
        {
            ["owner"] = owner,
            ["repo"] = RepoName,
        };
        if (feedMode is not null) body["feedMode"] = feedMode;
        var created = await Client.PostDataAsync<JsonElement>($"/api/projects/{project.Id}/github-connections", body);
        return (project.Id, created.GetProperty("id").GetString()!, created.GetProperty("webhookSecret").GetString()!);
    }

    private async Task DeliverClosedAsync(string connectionId, string secret, string deliveryId)
    {
        var payload = $$"""
            {
              "action": "closed",
              "number": {{GithubIssueNumber}},
              "issue": {
                "number": {{GithubIssueNumber}},
                "title": "Close me",
                "state": "closed",
                "labels": [ { "name": "mohist" } ]
              },
              "repository": {
                "name": "hello-world",
                "full_name": "octocat/hello-world",
                "owner": { "login": "octocat" }
              }
            }
            """;
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/github-connections/{connectionId}/ingress")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", "issues");
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        request.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes)).ToLowerInvariant());
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<int> FeedAsync(string projectId, string connectionId, string secret)
    {
        var payload = $$"""
            {
              "action": "labeled",
              "number": {{GithubIssueNumber}},
              "issue": {
                "number": {{GithubIssueNumber}},
                "title": "Close me",
                "state": "open",
                "labels": [ { "name": "mohist" } ]
              },
              "repository": {
                "name": "hello-world",
                "full_name": "octocat/hello-world",
                "owner": { "login": "octocat" }
              }
            }
            """;
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/github-connections/{connectionId}/ingress")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", "issues");
        request.Headers.Add("X-GitHub-Delivery", "close-feed-delivery");
        request.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes)).ToLowerInvariant());
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await PumpAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
        var link = await links.GetAsync(projectId, RepoName, GithubIssueNumber);
        Assert.NotNull(link);
        return link!.IssueNumber;
    }

    private Task PumpAsync() =>
        _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

    private async Task SeedLinkAsync(string projectId, int issueNumber)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
        await links.CreateAsync(projectId, RepoName, GithubIssueNumber, issueNumber);
    }

    private async Task<DomainIssue?> LoadIssueAsync(string projectId, int number)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIssueStore>();
        return await store.LoadAsync(GrainKey.Issue(new IssueKey(projectId, number)));
    }

    [Fact]
    public async Task ClosedEvent_CancelsLinkedBacklogIssue()
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync(feedMode: "backlog");
        var issueNumber = await FeedAsync(projectId, connectionId, secret);

        await DeliverClosedAsync(connectionId, secret, "close-delivery-1");
        await PumpAsync();

        var issue = await LoadIssueAsync(projectId, issueNumber);
        Assert.Equal(IssueStatus.Cancelled, issue!.Status);
    }

    [Fact]
    public async Task ClosedEvent_OnRunningIssue_IsNoOp()
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync();
        var issueNumber = await FeedAsync(projectId, connectionId, secret);

        await DeliverClosedAsync(connectionId, secret, "close-delivery-running");
        await PumpAsync();

        var issue = await LoadIssueAsync(projectId, issueNumber);
        Assert.Equal(IssueStatus.InProgress, issue!.Status);
    }

    [Fact]
    public async Task ClosedEvent_OnDoneIssue_IsNoOp_SelfLoopSafe()
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync();
        var grains = _fixture.Grains;
        var issueNumber = await grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId)).NextAsync();
        var issue = DomainIssue.Create(projectId, issueNumber, "Already done", repositoryRef: RepoName, isDraft: false);
        issue.StartWorkflow("wr_done");
        issue.Complete("wr_done");
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIssueStore>();
            await store.SaveAsync(GrainKey.Issue(new IssueKey(projectId, issueNumber)), issue, issue.PendingEvents);
        }
        await SeedLinkAsync(projectId, issueNumber);

        await DeliverClosedAsync(connectionId, secret, "close-delivery-done");
        await PumpAsync();

        Assert.Equal(IssueStatus.Done, (await LoadIssueAsync(projectId, issueNumber))!.Status);
    }

    [Fact]
    public async Task ClosedEvent_OnCancelledIssue_IsNoOp()
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync();
        var grains = _fixture.Grains;
        var issueNumber = await grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId)).NextAsync();
        var issueGrain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await issueGrain.CreateAsync(projectId, issueNumber, "Already cancelled", null, null, "p2", RepoName, isDraft: false);
        await issueGrain.CancelAsync();
        await SeedLinkAsync(projectId, issueNumber);

        await DeliverClosedAsync(connectionId, secret, "close-delivery-cancelled");
        await PumpAsync();

        Assert.Equal(IssueStatus.Cancelled, (await LoadIssueAsync(projectId, issueNumber))!.Status);
    }

    [Fact]
    public async Task ClosedEvent_WithoutLink_IsNoOp_OrderingAccepted()
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync();

        await DeliverClosedAsync(connectionId, secret, "close-delivery-no-link");
        await PumpAsync();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
        Assert.Null(await links.GetAsync(projectId, RepoName, GithubIssueNumber));
    }
}
