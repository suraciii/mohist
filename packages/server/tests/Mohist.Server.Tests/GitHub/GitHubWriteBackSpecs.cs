using System.Net;
using Mohist.Server.Infrastructure.Events;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.GitHub;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Tests.GitHub;

[Collection("GitHubCommand")]
[Trait("level", "L1")]
public sealed class GitHubWriteBackSpecs
{
    private const string RepoName = "hello-world";
    private const int GithubIssueNumber = 42;

    private readonly GitHubCommandFixture _fixture;

    public GitHubWriteBackSpecs(GitHubCommandFixture fixture)
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
        // yet (best-effort contract). The mirror link may not exist yet.
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
        var dispatcher = _fixture.Services.GetRequiredService<IEventDispatcher>();
        await dispatcher.DrainAsync();
        await dispatcher.DrainAsync();
    }

    [Fact]
    public async Task Completed_WithDeliveryPullRequest_PostsSummaryCommentWithPrUrlAndCloses()
    {
        var (projectId, connectionId) = await ConnectNewAsync();
        _fixture.Comments.DeliveryPrUrl = "https://github.com/octocat/hello-world/pull/123";
        var issueNumber = await SeedIssueAsync(projectId, issue =>
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
        var closes = _fixture.Comments.Closes.Where(c => c.ConnectionId == connectionId).ToList();
        if (closes.Count != 1)
            throw new Xunit.Sdk.XunitException(
                $"Expected exactly one close for connection {connectionId}, found {closes.Count}. " +
                $"{await DescribeWriteBackStateAsync(projectId, connectionId, issueNumber)}");
        var close = closes[0];
        Assert.Equal("completed", close.StateReason);
    }

    /// <summary>
    /// Renders the durable write-back state for this connection so an empty
    /// Closes assertion names its cause: the handler swallows transient
    /// operation failures into the audit stores (no auto-retry), and the
    /// reservation rows show whether an operation was skipped or stranded.
    /// </summary>
    private async Task<string> DescribeWriteBackStateAsync(string projectId, string connectionId, int issueNumber)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var failures = (await sp.GetRequiredService<GitHubWriteBackFailureStore>()
                .ListRecentAsync(projectId, limit: 20))
            .Where(f => f.ConnectionId == connectionId)
            .ToList();
        await using var db = await sp.GetRequiredService<IDbContextFactory<MohistDbContext>>()
            .CreateDbContextAsync();
        var link = await db.GitHubIssueLinks.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ProjectId == projectId && row.IssueNumber == issueNumber);
        var ops = link is null
            ? "link=<none>"
            : string.Join("; ", db.GitHubIssueCommentOperations.AsNoTracking()
                .Where(row => row.LinkId == link.Id)
                .Select(row => $"{row.CommentKey}:{row.Kind}:err={row.LastError ?? "-"}"));
        return $"failures=[{string.Join("|", failures.Select(f => $"{f.Operation}:{f.ErrorCode}:{f.ErrorDetail}"))}] ops=[{ops}]";
    }
}
