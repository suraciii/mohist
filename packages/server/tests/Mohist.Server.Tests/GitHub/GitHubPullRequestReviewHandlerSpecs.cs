using Mohist.Server.GitHub;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.GitHub;

[Trait("level", "L0")]
public sealed class GitHubPullRequestReviewHandlerSpecs
{
    [Fact]
    public async Task ApprovedReview_ApprovesTheMatchedCheckGateWithGitHubIdentity()
    {
        await using var factory = await GitHubPullRequestReviewHandlerTestFactory.CreateAsync(["alice"]);
        await factory.AddRunAsync();

        await factory.HandleAsync(factory.Review("approved", "alice"));

        var approval = Assert.Single(factory.Workflow.Approvals);
        Assert.Equal("github:alice", approval.DecidedBy);
    }

    [Fact]
    public async Task ChangesRequestedReview_SendsTheReviewBodyBackWithGitHubIdentity()
    {
        await using var factory = await GitHubPullRequestReviewHandlerTestFactory.CreateAsync(["alice"]);
        await factory.AddRunAsync();

        await factory.HandleAsync(factory.Review("changes_requested", "alice", body: "Fix the naming"));

        var request = Assert.Single(factory.Workflow.ChangeRequests);
        Assert.Equal("Fix the naming", request.Body);
        Assert.Equal("github:alice", request.DecidedBy);
    }

    [Fact]
    public async Task ChangesRequestedReview_WhenTheWorkflowRejectsFeedback_SettlesAsNoOp()
    {
        await using var factory = await GitHubPullRequestReviewHandlerTestFactory.CreateAsync(["alice"]);
        await factory.AddRunAsync();
        factory.Workflow.RequestChangesFailure = new InvalidOperationException("No feedback tasks are configured");

        await factory.HandleAsync(factory.Review("changes_requested", "alice", body: "Fix the naming"));

        var request = Assert.Single(factory.Workflow.ChangeRequests);
        Assert.Equal("Fix the naming", request.Body);
        Assert.Empty(factory.Workflow.Approvals);
    }

    [Fact]
    public async Task CommentedReview_DoesNotResolveTheCheckGate()
    {
        await using var factory = await GitHubPullRequestReviewHandlerTestFactory.CreateAsync(["alice"]);
        await factory.AddRunAsync();

        await factory.HandleAsync(factory.Review("commented", "alice", body: "Nice work"));

        Assert.Empty(factory.Workflow.Decisions);
    }

    [Fact]
    public async Task ReviewByAnUnlistedReviewer_DoesNotResolveTheCheckGate()
    {
        await using var factory = await GitHubPullRequestReviewHandlerTestFactory.CreateAsync(["alice"]);
        await factory.AddRunAsync();

        await factory.HandleAsync(factory.Review("approved", "mallory"));

        Assert.Empty(factory.Workflow.Decisions);
    }

    [Fact]
    public async Task ReviewWhenNoApproversAreConfigured_DoesNotResolveTheCheckGate()
    {
        await using var factory = await GitHubPullRequestReviewHandlerTestFactory.CreateAsync([]);
        await factory.AddRunAsync();

        await factory.HandleAsync(factory.Review("approved", "alice"));

        Assert.Empty(factory.Workflow.Decisions);
    }

    [Fact]
    public async Task ReviewWhenTheRunIsPastTheCheckGate_DoesNotResolveTheWorkflow()
    {
        await using var factory = await GitHubPullRequestReviewHandlerTestFactory.CreateAsync(["alice"]);
        await factory.AddRunAsync(
            status: WorkflowRunStatus.Pending,
            stageId: "integrate",
            stageStatus: StageRunStatus.Pending,
            requiresApproval: false);

        await factory.HandleAsync(factory.Review("approved", "alice"));

        Assert.Empty(factory.Workflow.Decisions);
    }

    [Fact]
    public async Task ReviewWithAnArbitraryHeadBranch_CorrelatesByPullRequestNumber()
    {
        await using var factory = await GitHubPullRequestReviewHandlerTestFactory.CreateAsync(["alice"]);
        await factory.AddRunAsync();

        await factory.HandleAsync(factory.Review("approved", "alice", branch: "feature/foo"));

        Assert.Single(factory.Workflow.Approvals);
    }

    [Fact]
    public async Task ReviewForAnUnknownPullRequest_DoesNotResolveTheWorkflow()
    {
        await using var factory = await GitHubPullRequestReviewHandlerTestFactory.CreateAsync(["alice"]);

        await factory.HandleAsync(factory.Review("approved", "alice", pullRequestNumber: 99999));

        Assert.Empty(factory.Workflow.Decisions);
        Assert.Equal(0, factory.Grains.WorkflowRequests);
    }

    [Fact]
    public async Task ReviewForADifferentRepositoryRemote_DoesNotResolveTheWorkflow()
    {
        await using var factory = await GitHubPullRequestReviewHandlerTestFactory.CreateAsync(
            ["alice"], projectRemote: "https://github.com/other-owner/hello-world.git");
        await factory.AddRunAsync();

        await factory.HandleAsync(factory.Review("approved", "alice"));

        Assert.Empty(factory.Workflow.Decisions);
        Assert.Equal(0, factory.Grains.WorkflowRequests);
    }

    [Fact]
    public async Task ReviewForAnInvalidRepositoryRemote_DoesNotResolveTheWorkflow()
    {
        const string malformedRemote = "not-a-valid-git-remote";
        await using var factory = await GitHubPullRequestReviewHandlerTestFactory.CreateAsync(
            ["alice"], projectRemote: malformedRemote);
        await factory.AddRunAsync(repositoryRemote: malformedRemote);

        await factory.HandleAsync(factory.Review("approved", "alice"));

        Assert.Empty(factory.Workflow.Decisions);
        Assert.Equal(0, factory.Grains.WorkflowRequests);
    }

    [Fact]
    public async Task DuplicateRuns_StopAtTheFirstOrderedRepositoryMismatch()
    {
        await using var factory = await GitHubPullRequestReviewHandlerTestFactory.CreateAsync(["alice"]);
        await factory.AddRunAsync(
            runId: "run-1",
            repositoryRemote: "https://github.com/other-owner/hello-world.git");
        await factory.AddRunAsync(runId: "run-2");

        await factory.HandleAsync(factory.Review("approved", "alice"));

        Assert.Empty(factory.Workflow.Decisions);
        Assert.Equal(0, factory.Grains.WorkflowRequests);
    }
}
