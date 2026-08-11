using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.GitHub;

[Collection("GitHubFeed")]
public sealed class GitHubWriteBackSpecs
{
    private const string RepoName = "hello-world";
    private const int GithubIssueNumber = 42;

    private readonly GitHubFeedFixture _fixture;

    public GitHubWriteBackSpecs(GitHubFeedFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
        fixture.Comments.StateLabels.Clear();
        fixture.Comments.Closes.Clear();
        fixture.Comments.DeliveryPrUrl = null;
    }

    private HttpClient Client => _fixture.Client;

    private async Task<(string ProjectId, string ConnectionId)> ConnectNewAsync()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-writeback-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var created = await Client.PostDataAsync<JsonElement>($"/api/projects/{project.Id}/github-connections", new
        {
            owner,
            repo = RepoName,
        });
        return (project.Id, created.GetProperty("id").GetString()!);
    }

    private async Task<int> SeedIssueAsync(string projectId, Action<DomainIssue> transition)
    {
        var grains = _fixture.Grains;
        var issueNumber = await grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId)).NextAsync();
        var issue = DomainIssue.Create(projectId, issueNumber, "Write back me", repositoryRef: RepoName, isDraft: false);
        transition(issue);
        // Link must exist before the issue events become dispatchable:
        // SaveAsync fires a fire-and-forget dispatch poke, and the
        // write-back handler drops the event for good when no link exists
        // yet (best-effort contract). The feed handler orders the same way.
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
            await links.CreateAsync(projectId, RepoName, GithubIssueNumber, issueNumber);
        }
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIssueStore>();
            await store.SaveAsync(GrainKey.Issue(new IssueKey(projectId, issueNumber)), issue, issue.PendingEvents);
        }
        return issueNumber;
    }

    private async Task PumpAsync()
    {
        var dispatcher = _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global);
        await dispatcher.DispatchNowAsync();
        await dispatcher.DispatchNowAsync();
    }

    [Fact]
    public async Task Completed_WithDeliveryPullRequest_PostsSummaryCommentWithPrUrlAndCloses()
    {
        var (projectId, connectionId) = await ConnectNewAsync();
        _fixture.Comments.DeliveryPrUrl = "https://github.com/octocat/hello-world/pull/123";
        await SeedIssueAsync(projectId, issue =>
        {
            issue.StartWorkflow("wr_done");
            issue.Complete("wr_done");
        });

        await PumpAsync();

        var comment = Assert.Single(
            _fixture.Comments.Comments,
            c => c.ConnectionId == connectionId && c.Body.Contains("已完成"));
        Assert.Contains("https://github.com/octocat/hello-world/pull/123", comment.Body);
        Assert.Contains(
            GitHubStateLabels.Done,
            _fixture.Comments.StateLabels.Where(s => s.ConnectionId == connectionId).Select(s => s.StateLabel));
        var close = Assert.Single(_fixture.Comments.Closes, c => c.ConnectionId == connectionId);
        Assert.Equal("completed", close.StateReason);
    }

    [Fact]
    public async Task Completed_WithoutDeliveryPullRequest_PostsLegalCommentWithoutPrUrl()
    {
        var (projectId, connectionId) = await ConnectNewAsync();
        await SeedIssueAsync(projectId, issue =>
        {
            issue.StartWorkflow("wr_done");
            issue.Complete("wr_done");
        });

        await PumpAsync();

        var comment = Assert.Single(
            _fixture.Comments.Comments,
            c => c.ConnectionId == connectionId && c.Body.Contains("已完成"));
        Assert.DoesNotContain("交付 PR", comment.Body);
        Assert.Single(_fixture.Comments.Closes, c => c.ConnectionId == connectionId);
    }

    [Fact]
    public async Task Cancelled_WithReason_PostsCancelCommentWithReasonAndClosesNotPlanned()
    {
        var (projectId, connectionId) = await ConnectNewAsync();
        await SeedIssueAsync(projectId, issue => issue.Close("需求方撤回"));

        await PumpAsync();

        var comment = Assert.Single(
            _fixture.Comments.Comments,
            c => c.ConnectionId == connectionId && c.Body.Contains("已取消"));
        Assert.Contains("需求方撤回", comment.Body);
        var close = Assert.Single(_fixture.Comments.Closes, c => c.ConnectionId == connectionId);
        Assert.Equal("not_planned", close.StateReason);
    }
}
