using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.GitHub;

[Collection("GitHubCommand")]
public sealed class GitHubIssueCommandSpecs
{
    private const string RepoName = "hello-world";
    private const int GithubIssueNumber = 42;

    private readonly GitHubCommandFixture _fixture;

    public GitHubIssueCommandSpecs(GitHubCommandFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
        fixture.Comments.StateLabels.Clear();
        fixture.Comments.Closes.Clear();
    }

    private HttpClient Client => _fixture.Client;

    private async Task<(string ProjectId, string ConnectionId, string Secret, string Owner)> ConnectNewAsync()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-command-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        var created = await Client.PostDataAsync<JsonElement>($"/api/projects/{project.Id}/github-connections", new
        {
            owner,
            repo = RepoName,
        });
        return (project.Id, created.GetProperty("id").GetString()!, created.GetProperty("webhookSecret").GetString()!, owner);
    }

    private async Task DeliverCommentAsync(
        string connectionId,
        string secret,
        string deliveryId,
        string body,
        string association = "MEMBER",
        long commentId = 1001,
        string[]? labels = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            action = "created",
            issue = new
            {
                number = GithubIssueNumber,
                title = "Fix the bug",
                body = "Steps to reproduce",
                state = "open",
                labels = (labels ?? ["p1"]).Select(name => new { name }).ToArray(),
            },
            comment = new
            {
                id = commentId,
                body,
                author_association = association,
                user = new { login = "alice" },
            },
            repository = new
            {
                name = RepoName,
                full_name = $"octocat/{RepoName}",
                owner = new { login = "octocat" },
            },
        });
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/github-connections/{connectionId}/ingress")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", "issue_comment");
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        request.Headers.Add("X-Hub-Signature-256", Sign(bytes, secret));
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task PumpAsync()
    {
        var dispatcher = _fixture.Services.GetRequiredService<IEventDispatcher>();
        await dispatcher.DrainAsync();
        await dispatcher.DrainAsync();
    }

    private async Task<DomainIssue?> LoadIssueAsync(string projectId, int number)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IIssueStore>()
            .LoadAsync(GrainKey.Issue(new IssueKey(projectId, number)));
    }

    private async Task<GitHubIssueLink?> LoadLinkAsync(string projectId, string owner)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>()
            .GetAsync(projectId, RepoName, GithubIssueNumber);
    }

    private async Task<int> CountIssuesAsync(string projectId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var context = await db.CreateDbContextAsync();
        return await context.Issues.CountAsync(row => row.ProjectId == projectId);
    }

    private static string Sign(byte[] payload, string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)).ToLowerInvariant();

    [Fact]
    public async Task AuthorizedStart_CreatesLinksAndStartsDefaultWorkflowWithPriority()
    {
        var (projectId, connectionId, secret, owner) = await ConnectNewAsync();

        await DeliverCommentAsync(connectionId, secret, "command-start-1", "/mohist start", labels: ["p1"]);
        await PumpAsync();

        var link = await LoadLinkAsync(projectId, owner);
        Assert.NotNull(link);
        var issue = await LoadIssueAsync(projectId, link!.IssueNumber);
        Assert.NotNull(issue);
        Assert.Equal("Fix the bug", issue!.Title);
        Assert.Equal("Steps to reproduce", issue.Body);
        Assert.Equal("p1", issue.Priority);
        Assert.Equal(RepoName, issue.RepositoryRef);
        Assert.Equal(IssueStatus.InProgress, issue.Status);
        Assert.False(string.IsNullOrWhiteSpace(issue.WorkflowRunId));
        var reply = Assert.Single(_fixture.Comments.Comments,
            comment => comment.ConnectionId == connectionId
                && comment.Body.Contains("已创建并启动", StringComparison.Ordinal));
        Assert.Equal(GithubIssueNumber, reply.GithubIssueNumber);
        Assert.True(link.HasPostedComment(GitHubCommentKinds.CommandReply("1001")));
    }

    [Fact]
    public async Task RepeatedStartDelivery_IsIdempotentAndDoesNotStartAgain()
    {
        var (projectId, connectionId, secret, owner) = await ConnectNewAsync();

        await DeliverCommentAsync(connectionId, secret, "command-start-a", "/mohist start", commentId: 1002);
        await DeliverCommentAsync(connectionId, secret, "command-start-b", "/mohist start", commentId: 1002);
        await PumpAsync();
        await PumpAsync();

        var link = await LoadLinkAsync(projectId, owner);
        Assert.NotNull(link);
        Assert.Equal(1, await CountIssuesAsync(projectId));
        Assert.Single(_fixture.Comments.Comments,
            comment => comment.ConnectionId == connectionId
                && comment.Body.Contains("已创建并启动", StringComparison.Ordinal));
        Assert.DoesNotContain(_fixture.Comments.Comments,
            comment => comment.ConnectionId == connectionId
                && comment.Body.Contains("已接入", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("OWNER")]
    [InlineData("MEMBER")]
    [InlineData("COLLABORATOR")]
    public async Task AuthorizedAssociations_CanStart(string association)
    {
        var (projectId, connectionId, secret, owner) = await ConnectNewAsync();

        await DeliverCommentAsync(connectionId, secret, "command-authorized-" + association, "/mohist start", association, commentId: 1003);
        await PumpAsync();

        var link = await LoadLinkAsync(projectId, owner);
        Assert.NotNull(link);
        Assert.Equal(IssueStatus.InProgress, (await LoadIssueAsync(projectId, link!.IssueNumber))!.Status);
    }

    [Fact]
    public async Task UnauthorizedAssociation_IsIgnored()
    {
        var (projectId, connectionId, secret, owner) = await ConnectNewAsync();

        await DeliverCommentAsync(connectionId, secret, "command-unauthorized", "/mohist start", "CONTRIBUTOR", commentId: 1004);
        await PumpAsync();

        Assert.Null(await LoadLinkAsync(projectId, owner));
        Assert.Equal(0, await CountIssuesAsync(projectId));
        Assert.Empty(_fixture.Comments.Comments);
    }

    [Fact]
    public async Task UnknownCommand_ReceivesRefusalWithoutCreatingIssue()
    {
        var (projectId, connectionId, secret, owner) = await ConnectNewAsync();

        await DeliverCommentAsync(connectionId, secret, "command-unknown", "/mohist stop", commentId: 1005);
        await PumpAsync();

        Assert.Null(await LoadLinkAsync(projectId, owner));
        Assert.Equal(0, await CountIssuesAsync(projectId));
        var reply = Assert.Single(_fixture.Comments.Comments);
        Assert.Contains("不支持命令", reply.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartWithUnavailableRepository_ReceivesRefusalAndLeavesBacklog()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-command-{Guid.NewGuid():N}", repoName: "primary", gitUrl: "git@example.com:primary.git");
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

        await DeliverCommentAsync(connectionId, secret, "command-no-repository", "/mohist start", commentId: 1006);
        await PumpAsync();

        var link = await LoadLinkAsync(project.Id, owner);
        Assert.NotNull(link);
        Assert.Equal(IssueStatus.Backlog, (await LoadIssueAsync(project.Id, link!.IssueNumber))!.Status);
        var reply = Assert.Single(_fixture.Comments.Comments,
            comment => comment.ConnectionId == connectionId
                && comment.Body.Contains("已创建，但无法启动", StringComparison.Ordinal));
        Assert.Contains("not found", reply.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssuesLabelEvent_DoesNotCreateMohistIssue()
    {
        var (projectId, connectionId, secret, owner) = await ConnectNewAsync();
        var payload = $$"""
            {
              "action": "labeled",
              "issue": { "number": {{GithubIssueNumber}}, "title": "Label only", "labels": [ { "name": "mohist" } ] },
              "repository": { "name": "hello-world", "full_name": "octocat/hello-world", "owner": { "login": "octocat" } }
            }
            """;
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/github-connections/{connectionId}/ingress")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", "issues");
        request.Headers.Add("X-GitHub-Delivery", "label-noop");
        request.Headers.Add("X-Hub-Signature-256", Sign(bytes, secret));
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await PumpAsync();

        Assert.Null(await LoadLinkAsync(projectId, owner));
        Assert.Equal(0, await CountIssuesAsync(projectId));
    }

    [Fact]
    public async Task RemovedConnectionOption_IsRejectedByApi()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-command-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");

        using var response = await Client.PostAsJsonAsync($"/api/projects/{project.Id}/github-connections", new
        {
            owner,
            repo = RepoName,
            feedMode = "backlog",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
