using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Profile;

/// <summary>
/// T-005 contract: the workflow-state projection is reached through
/// <see cref="MohistDefaultWorkflowProjection"/> alone and ignores which
/// Profile is selected. The descriptive Profile type
/// (<see cref="IIssueWorkflowProfile"/>) only exposes the
/// <see cref="WorkflowProfile"/> record; it MUST NOT carry projection
/// members. These specs anchor both guarantees in one place so future
/// regressions are caught.
/// </summary>
public class MohistDefaultWorkflowProjectionSpecs
{
    private static WorkflowStatusView BuildRunningView(string workflowRunId, string? assignedTo = null) =>
        new(
            WorkflowRunId: workflowRunId,
            Status: "running",
            CurrentStage: "build",
            Stages: [],
            PendingWork: null,
            Failure: null,
            AvailableActions: [],
            AssignedTo: assignedTo,
            Metadata: null);

    private static WorkflowStatusView BuildAwaitingApprovalView(string workflowRunId, string stage) =>
        new(
            WorkflowRunId: workflowRunId,
            Status: "awaiting-approval",
            CurrentStage: stage,
            Stages:
            [
                new StageStatusView(
                    Stage: stage,
                    Status: "awaiting-approval",
                    Order: 0,
                    Tasks: [],
                    Checks: [],
                    ApprovalStatus: new ApprovalStatusView(
                        Result: null,
                        RequestedAt: "2026-01-01T00:00:00Z",
                        RespondedAt: null,
                        DecidedBy: null),
                    Failure: null,
                    Feedback: null),
            ],
            PendingWork: null,
            Failure: null,
            AvailableActions: [],
            AssignedTo: "user_1",
            Metadata: null);

    private static WorkflowStatusView BuildFailedView(string workflowRunId, string message) =>
        new(
            WorkflowRunId: workflowRunId,
            Status: "failed",
            CurrentStage: "build",
            Stages: [],
            PendingWork: null,
            Failure: new FailureStatusView(
                Reason: "task-failed",
                Stage: "build",
                TaskId: "verify",
                CheckName: null,
                Message: message,
                Error: null),
            AvailableActions: [],
            AssignedTo: null,
            Metadata: null);

    // ===================== Projection does not depend on profile selection =====================

    [Fact]
    public void ProjectWorkflowState_DoesNotExposeWorkflowProfileParameter()
    {
        var projection = typeof(MohistDefaultWorkflowProjection).GetMethod("ProjectWorkflowState",
            [typeof(int), typeof(string), typeof(IssueStatus), typeof(WorkflowStatusView)]);

        Assert.NotNull(projection);
        Assert.Equal(typeof(MohistDefaultWorkflowState), projection!.ReturnType);
    }

    [Fact]
    public void ProjectWorkflowState_IsIdenticalForDifferentProfileSelections()
    {
        var workflow = BuildRunningView("wr_1");
        var registry = new IssueWorkflowProfileRegistry();
        var profiles = new[] { registry.Get(IssueWorkflowProfiles.LocalId), registry.Get(IssueWorkflowProfiles.GithubPrId) };

        var first = MohistDefaultWorkflowProjection.ProjectWorkflowState(508, "Title", IssueStatus.InProgress, workflow);

        foreach (var profile in profiles)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Id));
            Assert.NotNull(profile.Definition);
        }

        var viaProjection = MohistDefaultWorkflowProjection.ProjectWorkflowState(508, "Title", IssueStatus.InProgress, workflow);
        Assert.Equal(first.IssueStatus, viaProjection.IssueStatus);
        Assert.Equal(first.Health, viaProjection.Health);
        Assert.Equal(first.BlockedReason, viaProjection.BlockedReason);
        Assert.Equal(first.ChangeDir, viaProjection.ChangeDir);
        Assert.Equal(first.Completed, viaProjection.Completed);
    }

    // ===================== Profile descriptive face sourced from WorkflowProfile =====================

    [Fact]
    public void IIssueWorkflowProfile_OnlyExposesProfileProperty()
    {
        var interfaceType = typeof(IIssueWorkflowProfile);
        var properties = interfaceType.GetProperties().Select(p => p.Name).ToArray();

        Assert.Single(properties);
        Assert.Equal("Profile", properties[0]);
    }

    [Fact]
    public void IIssueWorkflowProfile_DoesNotDeclareProjectWorkflowState()
    {
        var interfaceType = typeof(IIssueWorkflowProfile);

        Assert.DoesNotContain(interfaceType.GetMethods(), m => m.Name == "ProjectWorkflowState");
    }

    [Fact]
    public void MohistLocalIssueWorkflowProfile_Profile_MirrorsWorkflowProfileCatalog()
    {
        var profile = new MohistLocalIssueWorkflowProfile().Profile;

        Assert.Same(WorkflowProfileCatalog.Profile, profile);
        Assert.Equal(WorkflowProfileCatalog.LocalId, profile.Id);
        Assert.Equal("Mohist Local", profile.Name);
        Assert.False(string.IsNullOrWhiteSpace(profile.Description));
        Assert.NotNull(profile.Definition);
    }

    [Fact]
    public void MohistGithubPrIssueWorkflowProfile_Profile_MirrorsWorkflowProfileCatalog()
    {
        var profile = new MohistGithubPrIssueWorkflowProfile().Profile;

        Assert.Same(WorkflowProfileCatalog.GithubPrProfileAsset, profile);
        Assert.Equal(WorkflowProfileCatalog.GithubPrId, profile.Id);
        Assert.Equal("Mohist GitHub PR", profile.Name);
        Assert.False(string.IsNullOrWhiteSpace(profile.Description));
        Assert.NotNull(profile.Definition);
    }

    // ===================== Runtime status scenarios =====================

    [Fact]
    public void DoneIssue_ProjectsToDone()
    {
        var projection = MohistDefaultWorkflowProjection.ProjectWorkflowState(508, "Title", IssueStatus.Done, workflow: null);

        Assert.Equal("done", projection.Health);
        Assert.True(projection.Completed);
    }

    [Fact]
    public void AwaitingApproval_ProjectsToAttentionWithReviewRequired()
    {
        var workflow = BuildAwaitingApprovalView("wr_1", "check");

        var projection = MohistDefaultWorkflowProjection.ProjectWorkflowState(508, "Title", IssueStatus.InProgress, workflow);

        Assert.Equal("attention", projection.Health);
        Assert.NotNull(projection.Attention);
        Assert.Equal(WorkflowAttentionReason.ReviewRequired, projection.Attention!.Reason);
    }

    [Fact]
    public void FailedWorkflow_ProjectsToBlockedWithFailureMessage()
    {
        var workflow = BuildFailedView("wr_1", "build failure");

        var projection = MohistDefaultWorkflowProjection.ProjectWorkflowState(508, "Title", IssueStatus.InProgress, workflow);

        Assert.Equal("blocked", projection.Health);
        Assert.Equal("build failure", projection.BlockedReason);
        Assert.NotNull(projection.Attention);
        Assert.Equal(WorkflowAttentionReason.Blocked, projection.Attention!.Reason);
    }

    [Fact]
    public void ChangeDir_DerivedFromIssueNumber()
    {
        var projection = MohistDefaultWorkflowProjection.ProjectWorkflowState(508, "Title", IssueStatus.Backlog, workflow: null);

        Assert.Equal("openspec/changes/issue-508", projection.ChangeDir);
    }

    [Fact]
    public void NoWorkflow_YieldsNoApprovalAndCompletedFalse()
    {
        var projection = MohistDefaultWorkflowProjection.ProjectWorkflowState(508, "Title", IssueStatus.InProgress, workflow: null);

        Assert.Null(projection.StageApproval);
        Assert.False(projection.Completed);
    }
}