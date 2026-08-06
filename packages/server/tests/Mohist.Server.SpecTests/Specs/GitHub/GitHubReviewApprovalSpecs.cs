using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.Grains;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.GitHub;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.GitHub;

[Collection("GitHubFeed")]
public sealed class GitHubReviewApprovalSpecs
{
    private const string RepoName = "hello-world";

    private readonly GitHubFeedFixture _fixture;

    public GitHubReviewApprovalSpecs(GitHubFeedFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
    }

    private HttpClient Client => _fixture.Client;

    [Fact]
    public async Task ApprovedReview_ByApprover_PassesCheckGate_AttributedToGithubLogin()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-approve-1", ReviewPayload("approved", "alice", issueNumber));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("approved", check.ApprovalStatus!.Result);
        Assert.Equal("github:alice", check.ApprovalStatus.DecidedBy);
        Assert.Equal("integrate", status.Workflow.CurrentStage);
        Assert.Equal("pending", status.Workflow.Status);
    }

    [Fact]
    public async Task ChangesRequestedReview_SendsBack_WithReviewBodyAsReason()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-changes-1", ReviewPayload("changes_requested", "alice", issueNumber, "Fix the naming"));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("running", check.Status);
        Assert.Null(check.ApprovalStatus!.Result);
        Assert.Equal("github:alice", check.ApprovalStatus.DecidedBy);
        var feedback = Assert.Single(check.Feedback!);
        Assert.Equal("Fix the naming", feedback.Body);
    }

    [Fact]
    public async Task CommentedReview_NoAction()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-comment-1", ReviewPayload("commented", "alice", issueNumber, "Nice work"));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("awaiting-approval", check.Status);
        Assert.Null(check.ApprovalStatus!.Result);
        Assert.Null(check.ApprovalStatus.DecidedBy);
        Assert.Equal("check", status.Workflow.CurrentStage);
    }

    [Fact]
    public async Task Review_ByReviewerOutsideApprovers_NoAction()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-outsider-1", ReviewPayload("approved", "mallory", issueNumber));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("awaiting-approval", check.Status);
        Assert.Null(check.ApprovalStatus!.Result);
    }

    [Fact]
    public async Task Review_WithEmptyApproversList_NoAction()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync([]);

        await DeliverAsync(connectionId, secret, "review-empty-list-1", ReviewPayload("approved", "alice", issueNumber));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("awaiting-approval", check.Status);
        Assert.Null(check.ApprovalStatus!.Result);
    }

    [Fact]
    public async Task Review_WhenIssueNotAtCheckGate_NoAction()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-gate-1", ReviewPayload("approved", "alice", issueNumber));
        await PumpAsync();
        await DeliverAsync(connectionId, secret, "review-gate-2", ReviewPayload("approved", "alice", issueNumber));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("approved", check.ApprovalStatus!.Result);
        Assert.Equal("integrate", status.Workflow.CurrentStage);
    }

    [Fact]
    public async Task Review_UnparseableBranch_NoAction()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-branch-1", ReviewPayload("approved", "alice", issueNumber, branch: "feature/foo"));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("awaiting-approval", check.Status);
        Assert.Null(check.ApprovalStatus!.Result);
    }

    [Fact]
    public async Task Review_UnknownIssueNumber_NoAction()
    {
        var (projectId, connectionId, secret, _) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-unknown-1", ReviewPayload("approved", "alice", 99999));
        await PumpAsync();
    }

    [Fact]
    public async Task DuplicateDelivery_ApprovesOnce()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);
        var payload = ReviewPayload("approved", "alice", issueNumber);

        await DeliverAsync(connectionId, secret, "review-dup-a", payload);
        await DeliverAsync(connectionId, secret, "review-dup-b", payload);
        await PumpAsync();
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("approved", check.ApprovalStatus!.Result);
        Assert.Equal("github:alice", check.ApprovalStatus.DecidedBy);
        Assert.Equal("integrate", status.Workflow.CurrentStage);
    }

    private async Task<(string ProjectId, string ConnectionId, string Secret, int IssueNumber)> SetupAtCheckGateAsync(
        string[] approvers)
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var project = await Client.CreateProjectWithDefaultRepositoryAsync<ProjectInfo>(
            "/api/projects", $"github-approval-{Guid.NewGuid():N}", repoName: RepoName, gitUrl: $"https://github.com/{owner}/{RepoName}.git");
        await SeedCheckGateProfileAsync(project.Id);
        var created = await Client.PostDataAsync<JsonElement>($"/api/projects/{project.Id}/github-connections", new
        {
            owner,
            repo = RepoName,
            approvers,
        });
        var connectionId = created.GetProperty("id").GetString()!;
        var secret = created.GetProperty("webhookSecret").GetString()!;

        var issueNumber = await _fixture.Grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(project.Id)).NextAsync();
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issueNumber)));
        await issueGrain.CreateAsync(project.Id, issueNumber, "Implement the feature", null, null, "p2", repositoryRef: RepoName, isDraft: false);
        await issueGrain.StartWorkAsync();
        var status = await LoadWorkflowStatusAsync(project.Id, issueNumber);
        Assert.Equal("awaiting-approval", status!.Workflow!.Status);
        Assert.Equal("check", status.Workflow.CurrentStage);
        return (project.Id, connectionId, secret, issueNumber);
    }

    /// <summary>
    /// Binds the project to a minimal two-stage profile (empty Check gate
    /// followed by an integrate stage) so the issue's run lands on the Check
    /// approval point without any runner.
    /// </summary>
    private async Task SeedCheckGateProfileAsync(string projectId)
    {
        const string profileId = "spec/check-gate";
        var definition = new WorkflowDefinition(
        [
            new StageDefinition("check", [], [], RequiresApproval: true),
            new StageDefinition("integrate", [new TaskDefinition("finish", "Finish", "spec/noop")], []),
        ]);
        var yaml = WorkflowYamlSerializer.ToYaml(definition);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.WorkflowProfileRecords.FindAsync(projectId, profileId);
        if (existing is null)
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
            existing.DefinitionSource = yaml;
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

    private async Task<IssueWorkflowStatus?> LoadWorkflowStatusAsync(string projectId, int issueNumber)
    {
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        return await grain.GetWorkflowStatusAsync();
    }

    private static string ReviewPayload(string state, string login, int issueNumber, string? body = null, string? branch = null)
    {
        var bodyJson = body is null ? "null" : $"\"{body}\"";
        return $$"""
            {
              "action": "submitted",
              "review": { "state": "{{state}}", "body": {{bodyJson}}, "user": { "login": "{{login}}" } },
              "pull_request": { "number": 7, "head": { "ref": "{{branch ?? "mo/issue-" + issueNumber}}" } },
              "repository": { "name": "hello-world", "full_name": "octocat/hello-world", "owner": { "login": "octocat" } }
            }
            """;
    }

    private async Task DeliverAsync(string connectionId, string secret, string deliveryId, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/github-connections/{connectionId}/ingress")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", "pull_request_review");
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        request.Headers.Add("X-Hub-Signature-256", Sign(bytes, secret));
        using var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task PumpAsync() =>
        _fixture.Grains.GetGrain<IEventDispatcherGrain>(EventDispatcherGrain.Global).DispatchNowAsync();

    private static string Sign(byte[] payload, string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)).ToLowerInvariant();
}
