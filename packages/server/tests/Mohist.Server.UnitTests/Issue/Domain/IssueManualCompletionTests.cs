using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.UnitTests.Issue.Domain;

public class IssueManualCompletionTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MarkDone_OnInProgressIssueWithWorkflow_SetsStatusToDone()
    {
        var issue = NewInProgressIssue();

        var changed = issue.MarkDone(Now);

        Assert.True(changed);
        Assert.Equal(IssueStatus.Done, issue.Status);
        Assert.Equal(Now, issue.CompletedAt);
        var completed = Assert.Single(CompletionEvents(issue));
        Assert.Equal("wr_seed", completed.WorkflowRunId);
        Assert.Equal(IssueCompletionKinds.Manual, completed.CompletionKind);
    }

    [Fact]
    public void MarkDone_OnAlreadyDoneIssue_NoOp()
    {
        var issue = NewInProgressIssue();
        issue.MarkDone(Now);

        var changed = issue.MarkDone(Now.AddMinutes(1));

        Assert.False(changed);
        Assert.Equal(IssueStatus.Done, issue.Status);
        Assert.Equal(Now, issue.CompletedAt);
        Assert.Single(CompletionEvents(issue));
    }

    [Fact]
    public void MarkDone_OnBacklogIssue_Throws()
    {
        var issue = NewIssue();

        var changed = issue.MarkDone(Now);

        Assert.True(changed);
        Assert.Equal(IssueStatus.Done, issue.Status);
        Assert.Equal(Now, issue.CompletedAt);
        var completed = Assert.Single(CompletionEvents(issue));
        Assert.Null(completed.WorkflowRunId);
        Assert.Equal(IssueCompletionKinds.Manual, completed.CompletionKind);
    }

    [Fact]
    public void MarkDone_OnInProgressIssueWithoutWorkflow_Throws()
    {
        var issue = DomainIssue.Create(
            projectId: "proj-manual",
            number: 1,
            title: "Bare",
            repositoryRef: "main",
            isDraft: false,
            now: Now);
        issue.StartWorkflow("wr_seed", Now);

        // Wipe the workflow reference without setting status to Done;
        // simulates an issue that has lost its workflow reference.
        var field = typeof(DomainIssue).GetProperty("WorkflowRunId");
        field?.SetValue(issue, null);

        var error = Assert.Throws<InvalidOperationException>(() => issue.MarkDone(Now));

        Assert.Contains("no workflow run", error.Message);
        Assert.Equal(IssueStatus.InProgress, issue.Status);
    }

    [Fact]
    public void MarkDone_OnCancelledIssue_Throws()
    {
        var issue = NewInProgressIssue();
        issue.Close(reason: null, now: Now.AddDays(1));

        var error = Assert.Throws<InvalidOperationException>(() => issue.MarkDone(Now.AddDays(1).AddMinutes(1)));

        Assert.Contains("only InProgress", error.Message);
        Assert.Equal(IssueStatus.Cancelled, issue.Status);
    }

    [Fact]
    public void MarkDone_RecordsIssueCompletedEvent()
    {
        var issue = NewInProgressIssue();

        issue.MarkDone(Now);

        Assert.Contains(issue.PendingEvents, e => e is IssueCompleted);
    }

    private static DomainIssue NewInProgressIssue()
    {
        var issue = DomainIssue.Create(
            projectId: "proj-manual",
            number: 1,
            title: "Manual done seed",
            repositoryRef: "main",
            isDraft: false,
            now: Now);
        issue.StartWorkflow("wr_seed", Now);
        return issue;
    }

    private static DomainIssue NewIssue()
    {
        return DomainIssue.Create(
            projectId: "proj-manual",
            number: 2,
            title: "Backlog seed",
            repositoryRef: "main",
            isDraft: false,
            now: Now);
    }

    private static IReadOnlyList<IssueCompleted> CompletionEvents(DomainIssue issue)
    {
        var events = new List<IssueCompleted>();
        foreach (var pending in issue.PendingEvents)
        {
            if (pending is IssueCompleted completed) events.Add(completed);
        }
        return events;
    }
}
