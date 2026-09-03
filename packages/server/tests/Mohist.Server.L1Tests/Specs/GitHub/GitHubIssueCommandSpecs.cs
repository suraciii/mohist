using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.L1Tests.Specs.GitHub;

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
        fixture.Comments.ConfirmationFailure = null;
        fixture.Comments.PostThenThrow = false;
    }

    private HttpClient Client => _fixture.Client;

    private async Task<(string ProjectId, string ConnectionId, string Secret, string Owner)> ConnectNewAsync()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var projectId = $"project-{Guid.NewGuid():N}";
        var project = await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            $"github-command-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = RepoName,
                GitUrl = $"https://github.com/{owner}/{RepoName}.git",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        var connection = new GitHubConnection
        {
            Id = $"ghconn_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Owner = owner,
            Repo = RepoName,
        };
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<GitHubConnectionStore>();
        var secret = await store.CreateAsync(
            connection,
            new GitHubRepositoryInstallation(
                $"installation-{owner}",
                owner,
                RepoName,
                $"node-{owner}"));
        return (project.Id, connection.Id, secret, owner);
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

    private async Task<GitHubCommandReply?> LoadReplyAsync(string connectionId, string commentId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var context = await db.CreateDbContextAsync();
        var row = await context.GitHubCommandReplies.AsNoTracking()
            .SingleOrDefaultAsync(reply => reply.ConnectionId == connectionId
                && reply.GithubCommentId == commentId);
        return row is null
            ? null
            : new GitHubCommandReply
            {
                Id = row.Id,
                PostedAt = row.PostedAt,
                AttemptCount = row.AttemptCount,
                NextAttemptAt = row.NextAttemptAt,
                LeaseUntil = row.LeaseUntil,
                FailedAt = row.FailedAt,
                LastError = row.LastError,
                Marker = row.Marker,
                Body = row.Body,
            };
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
        Assert.NotNull(await LoadReplyAsync(connectionId, "1001"));
        Assert.NotNull((await LoadReplyAsync(connectionId, "1001"))!.PostedAt);
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
        Assert.Contains(
            GitHubCommentKinds.CommandReplyMarker(connectionId, GithubIssueNumber, "1005", GitHubCommentKinds.CommandReplyUnknownVerb),
            reply.Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommand_RedeliveryPostsOnlyOneReply()
    {
        var (projectId, connectionId, secret, owner) = await ConnectNewAsync();

        await DeliverCommentAsync(connectionId, secret, "command-unknown-a", "/mohist stop", commentId: 1007);
        await DeliverCommentAsync(connectionId, secret, "command-unknown-b", "/mohist stop", commentId: 1007);
        await PumpAsync();
        await PumpAsync();

        Assert.Null(await LoadLinkAsync(projectId, owner));
        Assert.Single(_fixture.Comments.Comments);
    }

    [Fact]
    public async Task AmbiguousReplyMarker_FailsClosedWithoutPostingAnotherComment()
    {
        var (_, connectionId, secret, _) = await ConnectNewAsync();
        var marker = GitHubCommentKinds.CommandReplyMarker(
            connectionId,
            GithubIssueNumber,
            "1009",
            GitHubCommentKinds.CommandReplyUnknownVerb);
        _fixture.Comments.Comments.Add(new RecordingGitHubCommentPort.PostedComment(
            connectionId,
            GithubIssueNumber,
            $"first\n\n{marker}"));
        _fixture.Comments.Comments.Add(new RecordingGitHubCommentPort.PostedComment(
            connectionId,
            GithubIssueNumber,
            $"second\n\n{marker}"));

        await DeliverCommentAsync(connectionId, secret, "command-ambiguous", "/mohist stop", commentId: 1009);
        await PumpAsync();

        var reply = await LoadReplyAsync(connectionId, "1009");
        Assert.NotNull(reply);
        Assert.Null(reply!.PostedAt);
        Assert.True(reply.IsFailed);
        Assert.Contains("ambiguous", reply.LastError!, StringComparison.Ordinal);
        Assert.Equal(2, _fixture.Comments.Comments.Count);
    }

    [Fact]
    public async Task CommandReplyFailure_IsRetriedByHostedConsumerWithoutDuplicateComment()
    {
        var (projectId, connectionId, secret, owner) = await ConnectNewAsync();
        _fixture.Comments.ConfirmationFailure = new TimeoutException("simulated reply failure");

        await DeliverCommentAsync(connectionId, secret, "command-reply-failure-a", "/mohist start", commentId: 1008);
        await PumpAsync();
        var failedReply = await LoadReplyAsync(connectionId, "1008");
        Assert.NotNull(failedReply);
        Assert.Equal(1, failedReply!.AttemptCount);
        Assert.NotNull(failedReply.NextAttemptAt);
        Assert.Empty(_fixture.Comments.Comments);

        _fixture.Comments.ConfirmationFailure = null;
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var pending = await scope.ServiceProvider.GetRequiredService<GitHubCommandReplyStore>().ListPendingAsync();
            Assert.Contains(pending, pendingReply => pendingReply.GithubCommentId == "1008");
        }
        var worker = _fixture.Services.GetRequiredService<GitHubCommandReplyDeliveryWorker>();
        var processed = await worker.ProcessPendingAsync();
        Assert.Equal(1, processed);
        var retriedReply = await LoadReplyAsync(connectionId, "1008");
        Assert.NotNull(retriedReply);
        Assert.NotNull(retriedReply!.PostedAt);

        var link = await LoadLinkAsync(projectId, owner);
        Assert.NotNull(link);
        Assert.Equal(IssueStatus.InProgress, (await LoadIssueAsync(projectId, link!.IssueNumber))!.Status);
        Assert.Single(_fixture.Comments.Comments, comment =>
            comment.Body.Contains(GitHubCommentKinds.CommandReplyMarker(connectionId, GithubIssueNumber, "1008", GitHubCommentKinds.CommandReplyStarted), StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisabledConnectionRetainsPendingReplyUntilEnabled()
    {
        var (projectId, connectionId, secret, owner) = await ConnectNewAsync();
        _fixture.Comments.ConfirmationFailure = new TimeoutException("simulated reply failure");

        await DeliverCommentAsync(connectionId, secret, "command-reply-disabled", "/mohist start", commentId: 1012);
        await PumpAsync();
        var failed = await LoadReplyAsync(connectionId, "1012");
        Assert.NotNull(failed);
        Assert.Null(failed!.PostedAt);
        Assert.Empty(_fixture.Comments.Comments);

        using (var disabled = await Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/disable", JsonContent.Create(new { })))
            disabled.EnsureSuccessStatusCode();

        _fixture.Comments.ConfirmationFailure = null;
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var worker = _fixture.Services.GetRequiredService<GitHubCommandReplyDeliveryWorker>();
        await worker.ProcessPendingAsync();
        Assert.Empty(_fixture.Comments.Comments);
        var retained = await LoadReplyAsync(connectionId, "1012");
        Assert.NotNull(retained);
        Assert.Null(retained!.PostedAt);
        Assert.Null(retained.LeaseUntil);
        Assert.False(retained.IsFailed);

        using (var enabled = await Client.PostAsync(
            $"/api/projects/{projectId}/github-connections/{connectionId}/enable", JsonContent.Create(new { })))
            enabled.EnsureSuccessStatusCode();

        Assert.Equal(1, await worker.ProcessPendingAsync());
        var delivered = await LoadReplyAsync(connectionId, "1012");
        Assert.NotNull(delivered!.PostedAt);
        Assert.Single(_fixture.Comments.Comments, comment =>
            comment.Body.Contains(GitHubCommentKinds.CommandReplyMarker(connectionId, GithubIssueNumber, "1012", GitHubCommentKinds.CommandReplyStarted), StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommandReplyUnknownPostResult_ReconcilesMarkerWithoutDuplicateComment()
    {
        var (projectId, connectionId, secret, owner) = await ConnectNewAsync();
        _fixture.Comments.PostThenThrow = true;

        await DeliverCommentAsync(connectionId, secret, "command-reply-unknown-result", "/mohist stop", commentId: 1010);
        await PumpAsync();

        var pending = await LoadReplyAsync(connectionId, "1010");
        Assert.NotNull(pending);
        Assert.Null(pending!.PostedAt);
        Assert.Equal(1, pending.AttemptCount);
        Assert.Single(_fixture.Comments.Comments);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var worker = _fixture.Services.GetRequiredService<GitHubCommandReplyDeliveryWorker>();
        Assert.Equal(1, await worker.ProcessPendingAsync());

        var reconciled = await LoadReplyAsync(connectionId, "1010");
        Assert.NotNull(reconciled!.PostedAt);
        Assert.Single(_fixture.Comments.Comments);
        Assert.Null(await LoadLinkAsync(projectId, owner));
    }

    [Fact]
    public async Task StartWithUnavailableRepository_ReceivesRefusalAndLeavesBacklog()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var projectId = $"project-{Guid.NewGuid():N}";
        var projectGrain = _fixture.Grains.GetGrain<IProjectGrain>(projectId);
        var project = await projectGrain.CreateAsync(
            $"github-command-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = "primary",
                GitUrl = "git@example.com:primary.git",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        await projectGrain.AddRepositoryAsync(
            RepoName,
            $"https://github.com/{owner}/{RepoName}.git",
            "main");
        var connection = new GitHubConnection
        {
            Id = $"ghconn_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Owner = owner,
            Repo = RepoName,
        };
        await using var connectionScope = _fixture.Services.CreateAsyncScope();
        var store = connectionScope.ServiceProvider.GetRequiredService<GitHubConnectionStore>();
        var secret = await store.CreateAsync(
            connection,
            new GitHubRepositoryInstallation(
                $"installation-{owner}",
                owner,
                RepoName,
                $"node-{owner}"));
        var connectionId = connection.Id;
        Assert.NotNull(await projectGrain.RemoveRepositoryAsync(RepoName));

        await DeliverCommentAsync(connectionId, secret, "command-no-repository", "/mohist start", commentId: 1006);
        await PumpAsync();

        var link = await LoadLinkAsync(project.Id, owner);
        Assert.NotNull(link);
        Assert.Equal(IssueStatus.Backlog, (await LoadIssueAsync(project.Id, link!.IssueNumber))!.Status);
        var reply = Assert.Single(_fixture.Comments.Comments,
            comment => comment.ConnectionId == connectionId
                && comment.Body.Contains("已创建，但无法启动", StringComparison.Ordinal));
        Assert.Contains("not found", reply.Body, StringComparison.OrdinalIgnoreCase);

        await projectGrain.AddRepositoryAsync(
            RepoName,
            $"https://github.com/{owner}/{RepoName}.git",
            "main");
        await DeliverCommentAsync(connectionId, secret, "command-no-repository-retry", "/mohist start", commentId: 1006);
        await PumpAsync();

        Assert.Equal(IssueStatus.InProgress, (await LoadIssueAsync(project.Id, link!.IssueNumber))!.Status);
        Assert.Single(_fixture.Comments.Comments, comment =>
            comment.Body.Contains(GitHubCommentKinds.CommandReplyMarker(connectionId, GithubIssueNumber, "1006", GitHubCommentKinds.CommandReplyStarted), StringComparison.Ordinal));
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
        var projectId = $"project-{Guid.NewGuid():N}";
        var project = await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            $"github-command-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = RepoName,
                GitUrl = $"https://github.com/{owner}/{RepoName}.git",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");

        using var response = await Client.PostAsJsonAsync($"/api/projects/{project.Id}/github-connections", new
        {
            owner,
            repo = RepoName,
            feedMode = "backlog",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
