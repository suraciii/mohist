using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.L1Tests.Specs.GitHub;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.GitHub;

[Collection("GitHubCommand")]
public sealed class GitHubReviewApprovalSpecs
{
    private const string RepoName = "hello-world";
    private const int PullRequestNumber = 7;
    private const string GitHubReviewHandlerIdentity = "Mohist.Server.GitHub.Subscriptions.GitHubPullRequestReviewHandler";

    private readonly GitHubCommandFixture _fixture;

    public GitHubReviewApprovalSpecs(GitHubCommandFixture fixture)
    {
        _fixture = fixture;
        fixture.Comments.Comments.Clear();
    }

    private HttpClient Client => _fixture.Client;

    [Fact]
    public async Task ApprovedReview_ByApprover_PassesCheckGate_AttributedToGithubLogin()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-approve-1", ReviewPayload("approved", "alice", PullRequestNumber));
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

        await DeliverAsync(connectionId, secret, "review-changes-1", ReviewPayload("changes_requested", "alice", PullRequestNumber, "Fix the naming"));
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
    public async Task ChangesRequestedReview_WithoutFeedbackTasks_SettlesAsNoOp()
    {
        var (projectId, connectionId, secret, issueNumber) =
            await SetupAtCheckGateAsync(["alice"], includeFeedbackTasks: false);

        await DeliverAsync(connectionId, secret, "review-changes-no-feedback-1", ReviewPayload(
            "changes_requested", "alice", PullRequestNumber, "Fix the naming"));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("awaiting-approval", check.Status);
        Assert.Null(check.ApprovalStatus!.Result);
        Assert.Null(check.ApprovalStatus.DecidedBy);
        Assert.True(check.Feedback is null || check.Feedback.Count == 0);
        await AssertReviewSettledAsync(projectId, connectionId, "review-changes-no-feedback-1");
    }

    [Fact]
    public async Task CommentedReview_NoAction()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-comment-1", ReviewPayload("commented", "alice", PullRequestNumber, "Nice work"));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("awaiting-approval", check.Status);
        Assert.Null(check.ApprovalStatus!.Result);
        Assert.Null(check.ApprovalStatus.DecidedBy);
        Assert.Equal("check", status.Workflow.CurrentStage);
        await AssertReviewSettledAsync(projectId, connectionId, "review-comment-1");
    }

    [Fact]
    public async Task Review_ByReviewerOutsideApprovers_NoAction()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-outsider-1", ReviewPayload("approved", "mallory", PullRequestNumber));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("awaiting-approval", check.Status);
        Assert.Null(check.ApprovalStatus!.Result);
        await AssertReviewSettledAsync(projectId, connectionId, "review-outsider-1");
    }

    [Fact]
    public async Task Review_WithEmptyApproversList_NoAction()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync([]);

        await DeliverAsync(connectionId, secret, "review-empty-list-1", ReviewPayload("approved", "alice", PullRequestNumber));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("awaiting-approval", check.Status);
        Assert.Null(check.ApprovalStatus!.Result);
        await AssertReviewSettledAsync(projectId, connectionId, "review-empty-list-1");
    }

    [Fact]
    public async Task Review_WhenIssueNotAtCheckGate_NoAction()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-gate-1", ReviewPayload("approved", "alice", PullRequestNumber));
        await PumpAsync();
        await DeliverAsync(connectionId, secret, "review-gate-2", ReviewPayload("approved", "alice", PullRequestNumber));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("approved", check.ApprovalStatus!.Result);
        Assert.Equal("integrate", status.Workflow.CurrentStage);
        await AssertReviewSettledAsync(projectId, connectionId, "review-gate-2");
    }

    [Fact]
    public async Task Review_ArbitraryHeadBranch_StillCorrelates()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-branch-1", ReviewPayload("approved", "alice", PullRequestNumber, branch: "feature/foo"));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("approved", check.ApprovalStatus!.Result);
        Assert.Equal("integrate", status.Workflow.CurrentStage);
        await AssertReviewSettledAsync(projectId, connectionId, "review-branch-1");
    }

    [Fact]
    public async Task Review_UnknownIssueNumber_NoAction_AndSettlesWithoutRetryOrDeadLetter()
    {
        var (projectId, connectionId, secret, _) = await SetupAtCheckGateAsync(["alice"]);

        await DeliverAsync(connectionId, secret, "review-unknown-1", ReviewPayload("approved", "alice", 99999));
        await PumpAsync();

        await AssertReviewSettledAsync(projectId, connectionId, "review-unknown-1");
    }

    [Fact]
    public async Task DuplicateDelivery_ApprovesOnce()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);
        var payload = ReviewPayload("approved", "alice", PullRequestNumber);

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

    [Fact]
    public async Task Review_SameLogicalRepositoryDifferentRemote_IsNoOp()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var project = await db.Projects.SingleAsync(row => row.Id == projectId);
            var repositories = JSON.Deserialize<List<RepositoryInfo>>(project.RepositoriesJson)!;
            repositories.Single(repository => repository.Name == RepoName).GitUrl =
                $"https://github.com/other-owner/{RepoName}.git";
            project.RepositoriesJson = JSON.Serialize(repositories);
            await db.SaveChangesAsync();
        }

        await DeliverAsync(connectionId, secret, "review-remote-mismatch-1", ReviewPayload(
            "approved", "alice", PullRequestNumber));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("awaiting-approval", check.Status);
        Assert.Null(check.ApprovalStatus!.Result);
        await AssertReviewSettledAsync(projectId, connectionId, "review-remote-mismatch-1");
    }

    [Fact]
    public async Task Review_InvalidRepositoryRemote_IsNoOp()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync(["alice"]);
        const string malformedRemote = "not-a-valid-git-remote";

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var project = await db.Projects.SingleAsync(row => row.Id == projectId);
            var repositories = JSON.Deserialize<List<RepositoryInfo>>(project.RepositoriesJson)!;
            repositories.Single(repository => repository.Name == RepoName).GitUrl = malformedRemote;
            project.RepositoriesJson = JSON.Serialize(repositories);

            var runRow = await db.WorkflowRuns.SingleAsync(row => row.MetadataProjectId == projectId
                && row.PullRequestNumber == PullRequestNumber);
            var run = JSON.Deserialize<WorkflowRun>(runRow.State)!;
            runRow.State = runRow.State.Replace(
                run.Repository!.GitUrl,
                malformedRemote,
                StringComparison.Ordinal);
            await db.SaveChangesAsync();
        }

        await DeliverAsync(connectionId, secret, "review-invalid-remote-1", ReviewPayload(
            "approved", "alice", PullRequestNumber));
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(s => s.Stage == "check");
        Assert.Equal("awaiting-approval", check.Status);
        Assert.Null(check.ApprovalStatus!.Result);
        await AssertReviewSettledAsync(projectId, connectionId, "review-invalid-remote-1");
    }

    [Fact]
    public async Task DuplicateRuns_UsesOrderedCandidate_AndStopsOnRepositoryMismatch()
    {
        var (projectId, connectionId, secret, firstIssueNumber) = await SetupAtCheckGateAsync(["alice"]);
        var secondIssueNumber = await AddCheckGateRunAsync(projectId);

        string firstRunId;
        string secondRunId;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var runs = await db.WorkflowRuns
                .Where(row => row.MetadataProjectId == projectId && row.PullRequestNumber == PullRequestNumber)
                .OrderBy(row => row.WorkflowRunId)
                .ToListAsync();
            Assert.Equal(2, runs.Count);
            firstRunId = runs[0].WorkflowRunId;
            secondRunId = runs[1].WorkflowRunId;
            var firstRun = JSON.Deserialize<WorkflowRun>(runs[0].State)!;
            runs[0].State = runs[0].State.Replace(
                firstRun.Repository!.GitUrl,
                $"https://github.com/other-owner/{RepoName}.git",
                StringComparison.Ordinal);
            await db.SaveChangesAsync();
        }

        await DeliverAsync(connectionId, secret, "review-duplicate-runs-1", ReviewPayload(
            "approved", "alice", PullRequestNumber));
        await PumpAsync();

        var firstStatus = await LoadWorkflowStatusAsync(projectId, firstIssueNumber);
        var secondStatus = await LoadWorkflowStatusAsync(projectId, secondIssueNumber);
        Assert.Contains(firstStatus!.WorkflowRunId, new[] { firstRunId, secondRunId });
        Assert.Contains(secondStatus!.WorkflowRunId, new[] { firstRunId, secondRunId });
        Assert.Equal("awaiting-approval", firstStatus.Workflow!.Stages.Single(s => s.Stage == "check").Status);
        Assert.Equal("awaiting-approval", secondStatus.Workflow!.Stages.Single(s => s.Stage == "check").Status);
        await AssertReviewSettledAsync(projectId, connectionId, "review-duplicate-runs-1");
    }

    /// <summary>
    /// Every review delivery must settle: the ingress event is marked
    /// dispatched and the approval handler leaves no dead-letter row.
    /// A handler exception would leave the event undelivered for retry
    /// (and eventually dead-letter it), so this is the observable proof
    /// that a no-op path never fails the dispatch.
    /// </summary>
    private async Task AssertReviewSettledAsync(string projectId, string connectionId, string deliveryId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var source = IngressEventPersistence.ConnectionSource(projectId, connectionId);
        var row = await db.IngressEvents.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Source == source && r.EventId == deliveryId);
        Assert.NotNull(row);
        Assert.NotNull(row!.DispatchedAt);

        var deadLetters = scope.ServiceProvider.GetRequiredService<IDeadLetterStore>();
        var rows = await deadLetters.QueryAsync(GitHubReviewHandlerIdentity, 10);
        Assert.Empty(rows);
    }

    private async Task<int> AddCheckGateRunAsync(string projectId)
    {
        var issueNumber = await _fixture.Grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId)).NextAsync();
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await issueGrain.CreateAsync(projectId, issueNumber, "Implement the second feature", null, null, "p2", repositoryRef: RepoName, isDraft: false);
        await issueGrain.StartWorkAsync();
        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        Assert.Equal("awaiting-approval", status!.Workflow!.Status);
        Assert.Equal("check", status.Workflow.CurrentStage);
        await PatchPullRequestVariableAsync(status.WorkflowRunId!);
        return issueNumber;
    }

    private async Task<(string ProjectId, string ConnectionId, string Secret, int IssueNumber)> SetupAtCheckGateAsync(
        string[] approvers,
        bool includeFeedbackTasks = true)
    {
        var owner = $"octocat-{Guid.NewGuid():N}";
        var projectId = $"project-{Guid.NewGuid():N}";
        var project = await _fixture.Grains.GetGrain<IProjectGrain>(projectId).CreateAsync(
            $"github-approval-{Guid.NewGuid():N}",
            new RepositoryInfo
            {
                Name = RepoName,
                GitUrl = $"https://github.com/{owner}/{RepoName}.git",
                BaseBranch = "main",
                IsDefault = true,
            },
            "true");
        await SeedCheckGateProfileAsync(project.Id, includeFeedbackTasks);
        var connection = new GitHubConnection
        {
            Id = $"ghconn_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Owner = owner,
            Repo = RepoName,
            Approvers = approvers,
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

        var issueNumber = await _fixture.Grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(project.Id)).NextAsync();
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(project.Id, issueNumber)));
        await issueGrain.CreateAsync(project.Id, issueNumber, "Implement the feature", null, null, "p2", repositoryRef: RepoName, isDraft: false);
        await issueGrain.StartWorkAsync();
        var status = await LoadWorkflowStatusAsync(project.Id, issueNumber);
        Assert.Equal("awaiting-approval", status!.Workflow!.Status);
        Assert.Equal("check", status.Workflow.CurrentStage);

        var variables = new
        {
            vars = new
            {
                github = new { pr = new { number = PullRequestNumber } },
            },
        };
        await Client.PatchDataAsync<JsonElement>(
            $"/api/workflow-runs/{status.WorkflowRunId}/variables", variables);

        await using (var variableScope = _fixture.Services.CreateAsyncScope())
        {
            var dbFactory = variableScope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await using var verifyDb = await dbFactory.CreateDbContextAsync();
            var runRow = await verifyDb.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == status.WorkflowRunId);
            Assert.Equal(PullRequestNumber, runRow.PullRequestNumber);
            Assert.Contains("pullRequestIdentity", runRow.State, StringComparison.Ordinal);
        }

        return (project.Id, connectionId, secret, issueNumber);
    }

    /// <summary>
    /// Binds the project to a minimal two-stage profile (empty Check gate
    /// followed by an integrate stage) so the issue's run lands on the Check
    /// approval point without any runner.
    /// </summary>
    private async Task SeedCheckGateProfileAsync(string projectId, bool includeFeedbackTasks = true)
    {
        const string profileId = "spec/check-gate";
        var definition = new WorkflowDefinition(
        [
            new StageDefinition("check", [], [], RequiresApproval: true),
            new StageDefinition("integrate", [new TaskDefinition("finish", "Finish", "spec/noop")], []),
        ],
        Approval: includeFeedbackTasks
            ? new ApprovalConfig(new ApprovalFeedbackConfig([
                new TaskDefinition(
                    "apply-feedback",
                    "Apply approval feedback",
                    "mohist/agent",
                    new Dictionary<string, JsonElement?>
                    {
                        ["name"] = JsonSerializer.SerializeToElement("mohist/builder"),
                        ["prompt"] = JsonSerializer.SerializeToElement("${{ prompts.apply-feedback }}"),
                    })
            ]))
            : null);
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

    private Task PatchPullRequestVariableAsync(string workflowRunId) =>
        _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId).PatchVariablesAsync(
            new VariableBundle(
                Vars: JsonSerializer.SerializeToElement(new
                {
                    github = new { pr = new { number = PullRequestNumber } },
                })));

    private static string ReviewPayload(string state, string login, int pullRequestNumber, string? body = null, string? branch = null)
    {
        var bodyJson = body is null ? "null" : $"\"{body}\"";
        var headJson = branch is null ? string.Empty : $", \"head\": {{ \"ref\": \"{branch}\" }}";
        return $$"""
            {
              "action": "submitted",
              "review": { "state": "{{state}}", "body": {{bodyJson}}, "user": { "login": "{{login}}" } },
              "pull_request": { "number": {{pullRequestNumber}}{{headJson}} },
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
        _fixture.Services.GetRequiredService<IEventDispatcher>().DrainAsync();

    private static string Sign(byte[] payload, string secret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)).ToLowerInvariant();
}
