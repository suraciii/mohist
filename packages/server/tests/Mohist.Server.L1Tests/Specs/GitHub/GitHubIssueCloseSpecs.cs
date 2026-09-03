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
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.L1Tests.Specs.GitHub;

[Collection("GitHubCommand")]
public sealed class GitHubIssueCloseSpecs
{
    private const string RepoName = "hello-world";
    private const int GithubIssueNumber = 42;

    private readonly GitHubCommandFixture _fixture;

    public GitHubIssueCloseSpecs(GitHubCommandFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
        fixture.Comments.StateLabels.Clear();
        fixture.Comments.Closes.Clear();
        fixture.Comments.ConfirmationFailure = null;
        fixture.Comments.PostThenThrow = false;
        fixture.Comments.CloseFailure = null;
        fixture.Comments.CloseThenThrow = false;
    }

    private HttpClient Client => _fixture.Client;

    private async Task<(string ProjectId, string ConnectionId, string Secret)> ConnectNewAsync()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var projectId = $"github-close-{Guid.NewGuid():N}";
        var project = await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            projectId,
            new RepositoryInfo
            {
                Name = RepoName,
                GitUrl = $"https://github.com/{owner}/{RepoName}.git",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        var body = new Dictionary<string, object?>
        {
            ["owner"] = owner,
            ["repo"] = RepoName,

        };
        var created = await Client.PostDataAsync<JsonElement>($"/api/projects/{project.Id}/github-connections", body);
        return (project.Id, created.GetProperty("id").GetString()!, created.GetProperty("webhookSecret").GetString()!);
    }

    private async Task DeliverClosedAsync(string connectionId, string secret, string deliveryId, string? stateReason = "not_planned")
    {
        var stateReasonProperty = stateReason is null ? string.Empty : $"\"state_reason\": \"{stateReason}\",";
        var payload = $$"""
            {
              "action": "closed",
              "number": {{GithubIssueNumber}},
              "issue": {
                "number": {{GithubIssueNumber}},
                "title": "Close me",
                "state": "closed",
                {{stateReasonProperty}}
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

    private async Task DeliverReopenedAsync(string connectionId, string secret, string deliveryId)
    {
        var payload = $$"""
            {
              "action": "reopened",
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
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        request.Headers.Add("X-Hub-Signature-256",
            "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes)).ToLowerInvariant());
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<int> SeedLinkedIssueAsync(string projectId)
    {
        var issueNumber = await _fixture.Grains
            .GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId))
            .NextAsync();
        await _fixture.Grains
            .GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)))
            .CreateAsync(projectId, issueNumber, "Close me", null, null, "p2", RepoName, isDraft: false);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<GitHubIssueLinkStore>();
        await links.CreateAsync(projectId, RepoName, GithubIssueNumber, issueNumber);
        return issueNumber;
    }

    private Task PumpAsync() =>
        _fixture.Services.GetRequiredService<IEventDispatcher>().DrainAsync();

    private async Task<(string ProjectId, string ConnectionId, string Secret, int IssueNumber)> CreateIssueAtIntegrateAsync()
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var projectId = $"github-integrate-{Guid.NewGuid():N}";
        var project = await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            projectId,
            new RepositoryInfo
            {
                Name = RepoName,
                GitUrl = $"https://github.com/{owner}/{RepoName}.git",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        await SeedIntegrateProfileAsync(project.Id);
        var created = await Client.PostDataAsync<JsonElement>($"/api/projects/{project.Id}/github-connections", new
        {
            owner,
            repo = RepoName,

        });
        var connectionId = created.GetProperty("id").GetString()!;
        var secret = created.GetProperty("webhookSecret").GetString()!;
        var issueNumber = await _fixture.Grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(project.Id)).NextAsync();
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issueNumber)));
        await issueGrain.CreateAsync(project.Id, issueNumber, "Close me", null, null, "p2", RepoName, isDraft: false);
        await issueGrain.StartWorkAsync();
        var workflowStatus = await issueGrain.GetWorkflowStatusAsync();
        Assert.Equal("check", workflowStatus!.Workflow!.CurrentStage);
        await _fixture.Grains.GetGrain<Mohist.Server.Workflow.Grains.IWorkflowGrain>(workflowStatus.WorkflowRunId!)
            .ApproveAsync("github:alice");
        workflowStatus = await issueGrain.GetWorkflowStatusAsync();
        Assert.Equal("integrate", workflowStatus!.Workflow!.CurrentStage);
        await SeedLinkAsync(project.Id, issueNumber);
        return (project.Id, connectionId, secret, issueNumber);
    }

    private async Task SeedIntegrateProfileAsync(string projectId)
    {
        const string profileId = "spec/integrate-close-guard";
        var definition = new WorkflowDefinition(
        [
            new StageDefinition("check", [], [], RequiresApproval: true),
            new StageDefinition("integrate", [new TaskDefinition("finish", "Finish", "spec/noop")], []),
        ]);
        var yaml = WorkflowYamlSerializer.ToYaml(definition);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var profile = await db.WorkflowProfileRecords.FindAsync(projectId, profileId);
        if (profile is null)
        {
            db.WorkflowProfileRecords.Add(new WorkflowProfileRecordRow
            {
                ProjectId = projectId,
                ProfileId = profileId,
                Name = profileId,
                DefinitionSource = yaml,
                SourceProvenance = nameof(WorkflowProfileSourceProvenance.Verbatim),
            });
        }
        else
        {
            profile.DefinitionSource = yaml;
        }

        var projectProfile = await db.ProjectWorkflowProfiles.FindAsync(projectId);
        if (projectProfile is null)
        {
            db.ProjectWorkflowProfiles.Add(new ProjectWorkflowProfile
            {
                ProjectId = projectId,
                DefaultWorkflowProfileId = profileId,
                DefaultWorkflowProfileIdKey = profileId,
            });
        }
        else
        {
            projectProfile.DefaultWorkflowProfileId = profileId;
            projectProfile.DefaultWorkflowProfileIdKey = profileId;
        }
        await db.SaveChangesAsync();
    }

    private async Task<int> CreateNoWorkflowIssueAsync(string projectId)
    {
        var issueNumber = await _fixture.Grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId)).NextAsync();
        var issue = DomainIssue.Create(
            projectId,
            issueNumber,
            "Close me",
            "No workflow",
            repositoryRef: RepoName,
            isDraft: false,
            noWorkflow: true);
        await SeedLinkAsync(projectId, issueNumber);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIssueStore>();
        await store.SaveAsync(GrainKey.Issue(new IssueKey(projectId, issueNumber)), issue, issue.PendingEvents);
        return issueNumber;
    }

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

    private async Task<GitHubCommandReply?> LoadReplyAsync(string connectionId, string commentId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.GitHubCommandReplies.AsNoTracking()
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

    [Fact]
    public async Task ClosedEvent_CancelsLinkedBacklogIssue()
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync();
        var issueNumber = await SeedLinkedIssueAsync(projectId);

        await DeliverClosedAsync(connectionId, secret, "close-delivery-1");
        await PumpAsync();

        var issue = await LoadIssueAsync(projectId, issueNumber);
        Assert.Equal(IssueStatus.Cancelled, issue!.Status);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("not_planned")]
    public async Task ClosedEvent_OnRunningWorkflowBeforeIntegrate_StopsAndCancelsIssue(string stateReason)
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync();
        var issueNumber = await SeedLinkedIssueAsync(projectId);
        await _fixture.Grains
            .GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)))
            .StartWorkAsync();

        await DeliverClosedAsync(connectionId, secret, $"close-delivery-running-{stateReason}", stateReason);
        await PumpAsync();

        var issue = await LoadIssueAsync(projectId, issueNumber);
        Assert.Equal(IssueStatus.Cancelled, issue!.Status);
        Assert.Single(_fixture.Comments.Closes, close => close.StateReason == "not_planned");
    }

    [Fact]
    public async Task ClosedEvent_AtIntegrate_IsDeliveryEchoAndLeavesIssueRunning()
    {
        var (projectId, connectionId, secret, issueNumber) = await CreateIssueAtIntegrateAsync();

        await DeliverClosedAsync(connectionId, secret, "close-delivery-integrate");
        await PumpAsync();

        Assert.Equal(IssueStatus.InProgress, (await LoadIssueAsync(projectId, issueNumber))!.Status);
        Assert.Empty(_fixture.Comments.Closes);
    }

    [Fact]
    public async Task ClosedEvent_OnNoWorkflowCompleted_MarksIssueDone()
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync();
        var issueNumber = await CreateNoWorkflowIssueAsync(projectId);

        await DeliverClosedAsync(connectionId, secret, "close-delivery-no-workflow-done", "completed");
        await PumpAsync();

        Assert.Equal(IssueStatus.Done, (await LoadIssueAsync(projectId, issueNumber))!.Status);
    }

    [Fact]
    public async Task ClosedEvent_OnNoWorkflowNotPlanned_CancelsIssue()
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync();
        var issueNumber = await CreateNoWorkflowIssueAsync(projectId);

        await DeliverClosedAsync(connectionId, secret, "close-delivery-no-workflow-cancel", "not_planned");
        await PumpAsync();

        Assert.Equal(IssueStatus.Cancelled, (await LoadIssueAsync(projectId, issueNumber))!.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("completed ")]
    public async Task ClosedEvent_OnNoWorkflowWithUnknownStateReason_IsNoOp(string? stateReason)
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync();
        var issueNumber = await CreateNoWorkflowIssueAsync(projectId);

        await DeliverClosedAsync(connectionId, secret, $"close-delivery-no-workflow-invalid-{Guid.NewGuid():N}", stateReason);
        await PumpAsync();

        Assert.Equal(IssueStatus.Backlog, (await LoadIssueAsync(projectId, issueNumber))!.Status);
    }

    [Fact]
    public async Task ReopenedEvent_OnCancelledNoWorkflowIssue_ReturnsToBacklog()
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync();
        var issueNumber = await CreateNoWorkflowIssueAsync(projectId);
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await issueGrain.CancelAsync();
        await SeedLinkAsync(projectId, issueNumber);

        await DeliverReopenedAsync(connectionId, secret, "reopen-delivery-cancelled");
        await PumpAsync();

        Assert.Equal(IssueStatus.Backlog, (await LoadIssueAsync(projectId, issueNumber))!.Status);
    }

    [Fact]
    public async Task ReopenedEvent_OnDoneIssue_LeavesDoneAndPostsOneFollowUpSuggestion()
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync();
        var issueNumber = await CreateNoWorkflowIssueAsync(projectId);
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await issueGrain.MarkDoneAsync();
        await SeedLinkAsync(projectId, issueNumber);

        await DeliverReopenedAsync(connectionId, secret, "reopen-delivery-done-1");
        await PumpAsync();
        await DeliverReopenedAsync(connectionId, secret, "reopen-delivery-done-2");
        await PumpAsync();

        Assert.Equal(IssueStatus.Done, (await LoadIssueAsync(projectId, issueNumber))!.Status);
        var comment = Assert.Single(_fixture.Comments.Comments,
            c => c.ConnectionId == connectionId && c.Body.Contains("follow-up", StringComparison.Ordinal));
        Assert.Contains(
            GitHubCommentKinds.CommandReplyMarker(
                connectionId,
                GithubIssueNumber,
                "issue-reopened",
                GitHubCommentKinds.ReopenedDoneFollowUp),
            comment.Body,
            StringComparison.Ordinal);
        var reply = await LoadReplyAsync(connectionId, "issue-reopened");
        Assert.NotNull(reply);
        Assert.NotNull(reply!.PostedAt);
    }

    [Fact]
    public async Task ReopenedEvent_OnDoneFollowUpFailure_IsRetriedByHostedConsumer()
    {
        var (projectId, connectionId, secret) = await ConnectNewAsync();
        var issueNumber = await CreateNoWorkflowIssueAsync(projectId);
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await issueGrain.MarkDoneAsync();
        await SeedLinkAsync(projectId, issueNumber);
        _fixture.Comments.ConfirmationFailure = new TimeoutException("simulated follow-up failure");

        await DeliverReopenedAsync(connectionId, secret, "reopen-delivery-follow-up-failure");
        await PumpAsync();

        var failed = await LoadReplyAsync(connectionId, "issue-reopened");
        Assert.NotNull(failed);
        Assert.Equal(1, failed!.AttemptCount);
        Assert.NotNull(failed.NextAttemptAt);
        Assert.Empty(_fixture.Comments.Comments);

        _fixture.Comments.ConfirmationFailure = null;
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var worker = _fixture.Services.GetRequiredService<GitHubCommandReplyDeliveryWorker>();
        Assert.Equal(1, await worker.ProcessPendingAsync());

        var delivered = await LoadReplyAsync(connectionId, "issue-reopened");
        Assert.NotNull(delivered);
        Assert.NotNull(delivered!.PostedAt);
        Assert.Single(_fixture.Comments.Comments,
            c => c.Body.Contains(GitHubCommentKinds.ReopenedDoneFollowUp, StringComparison.Ordinal));
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
