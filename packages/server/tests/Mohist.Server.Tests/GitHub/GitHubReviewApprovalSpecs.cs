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
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.Tests.GitHub;

[Collection("GitHubCommand")]
[Trait("level", "L1")]
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
    public async Task DuplicateSignedDeliveries_AdvanceCheckOnceWithGitHubIdentity()
    {
        var (projectId, connectionId, secret, issueNumber) = await SetupAtCheckGateAsync();
        var payload = ReviewPayload("approved", "alice", PullRequestNumber);

        await DeliverAsync(connectionId, secret, "review-dup-a", payload);
        await DeliverAsync(connectionId, secret, "review-dup-b", payload);
        await PumpAsync();
        await PumpAsync();

        var status = await LoadWorkflowStatusAsync(projectId, issueNumber);
        var check = status!.Workflow!.Stages.Single(stage => stage.Stage == "check");
        Assert.Equal("approved", check.ApprovalStatus!.Result);
        Assert.Equal("github:alice", check.ApprovalStatus.DecidedBy);
        Assert.Equal("integrate", status.Workflow.CurrentStage);
        await AssertReviewSettledAsync(projectId, connectionId, "review-dup-a");
        await AssertReviewSettledAsync(projectId, connectionId, "review-dup-b");
    }

    private async Task AssertReviewSettledAsync(string projectId, string connectionId, string deliveryId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var source = IngressEventPersistence.ConnectionSource(projectId, connectionId);
        var row = await db.IngressEvents.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Source == source && candidate.EventId == deliveryId);
        Assert.NotNull(row);
        Assert.NotNull(row!.DispatchedAt);

        var deadLetters = scope.ServiceProvider.GetRequiredService<IDeadLetterStore>();
        Assert.Empty(await deadLetters.QueryAsync(GitHubReviewHandlerIdentity, 10));
    }

    private async Task<(string ProjectId, string ConnectionId, string Secret, int IssueNumber)> SetupAtCheckGateAsync()
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
        await SeedCheckGateProfileAsync(project.Id);
        var connection = new GitHubConnection
        {
            Id = $"ghconn_{Guid.NewGuid():N}",
            ProjectId = project.Id,
            Owner = owner,
            Repo = RepoName,
            Approvers = ["alice"],
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

        var issueNumber = await _fixture.Grains
            .GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(project.Id))
            .NextAsync();
        var issueGrain = _fixture.Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(project.Id, issueNumber)));
        await issueGrain.CreateAsync(
            project.Id,
            issueNumber,
            "Implement the feature",
            null,
            null,
            "p2",
            repositoryRef: RepoName,
            isDraft: false);
        await issueGrain.StartWorkAsync();
        var status = await LoadWorkflowStatusAsync(project.Id, issueNumber);
        Assert.Equal("awaiting-approval", status!.Workflow!.Status);
        Assert.Equal("check", status.Workflow.CurrentStage);

        await PatchPullRequestVariableAsync(status.WorkflowRunId!);

        await using (var variableScope = _fixture.Services.CreateAsyncScope())
        {
            var dbFactory = variableScope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var runRow = await db.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == status.WorkflowRunId);
            Assert.Equal(PullRequestNumber, runRow.PullRequestNumber);
            Assert.Contains("pullRequestIdentity", runRow.State, StringComparison.Ordinal);
        }

        return (project.Id, connection.Id, secret, issueNumber);
    }

    private async Task SeedCheckGateProfileAsync(string projectId)
    {
        const string profileId = "spec/check-gate";
        var definition = new WorkflowDefinition(
        [
            new StageDefinition("check", [], [], RequiresApproval: true),
            new StageDefinition("integrate", [new TaskDefinition("finish", "Finish", "spec/noop")], []),
        ],
        Approval: new ApprovalConfig(new ApprovalFeedbackConfig([
            new TaskDefinition(
                "apply-feedback",
                "Apply approval feedback",
                "mohist/agent",
                new Dictionary<string, JsonElement?>
                {
                    ["name"] = JsonSerializer.SerializeToElement("mohist/builder"),
                    ["prompt"] = JsonSerializer.SerializeToElement("${{ prompts.apply-feedback }}"),
                })
        ])));
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
        var grain = _fixture.Grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        return await grain.GetWorkflowStatusAsync();
    }

    private Task PatchPullRequestVariableAsync(string workflowRunId) =>
        _fixture.Grains.GetGrain<IWorkflowGrain>(workflowRunId).PatchVariablesAsync(
            new VariableBundle(
                Vars: JsonSerializer.SerializeToElement(new
                {
                    github = new { pr = new { number = PullRequestNumber } },
                })));

    private static string ReviewPayload(string state, string login, int pullRequestNumber) =>
        $$"""
            {
              "action": "submitted",
              "review": { "state": "{{state}}", "body": null, "user": { "login": "{{login}}" } },
              "pull_request": { "number": {{pullRequestNumber}} },
              "repository": { "name": "hello-world", "full_name": "octocat/hello-world", "owner": { "login": "octocat" } }
            }
            """;

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
