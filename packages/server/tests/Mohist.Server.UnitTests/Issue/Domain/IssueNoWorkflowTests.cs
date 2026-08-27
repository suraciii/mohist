using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Domain;

public sealed class IssueNoWorkflowTests
{
    [Fact]
    public void StartWithoutWorkflow_MovesToInProgressWithoutRun()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project", 1, "External work", repositoryRef: "main", isDraft: false, noWorkflow: true);
        issue.ClearPendingEvents();

        issue.StartWithoutWorkflow(undeliveredPrerequisites: null, new DateTime(2026, 1, 1));

        Assert.Equal(IssueStatus.InProgress, issue.Status);
        Assert.Null(issue.WorkflowRunId);
        Assert.Null(issue.WorkspaceName);
        Assert.True(issue.HasWorkflowStarted);
        var started = Assert.Single(issue.PendingEvents) switch
        {
            IssueWorkStarted value => value,
            var other => throw new InvalidOperationException($"Expected IssueWorkStarted, got {other.GetType().Name}"),
        };
        Assert.True(started.NoWorkflow);
        Assert.Null(started.WorkflowRunId);
    }

    [Fact]
    public void NoWorkflow_InProgressCanBeMarkedDone()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project", 1, "External work", repositoryRef: "main", isDraft: false, noWorkflow: true);
        issue.StartWithoutWorkflow(null);

        Assert.True(issue.MarkDone());
        Assert.Equal(IssueStatus.Done, issue.Status);
    }

    [Fact]
    public void WorkflowSelection_IsLockedAfterNoWorkflowStart()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create(
            "project", 1, "External work", repositoryRef: "main", isDraft: false, noWorkflow: true);
        issue.StartWithoutWorkflow(null);

        Assert.Throws<WorkflowProfileLockedException>(() =>
            issue.ReplaceWorkflowProfile("mohist/local", noWorkflow: false));
    }

    [Fact]
    public void ExplicitProfileAndNoWorkflow_AreMutuallyExclusive()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("project", 1, "Work", repositoryRef: "main");

        Assert.Throws<ArgumentException>(() =>
            issue.ReplaceWorkflowProfile("mohist/local", noWorkflow: true));
    }
}
