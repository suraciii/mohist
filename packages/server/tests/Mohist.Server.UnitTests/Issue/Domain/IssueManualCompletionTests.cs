using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.UnitTests.Issue.Domain;

public class IssueManualCompletionTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MarkDone_InProgressIssue_RecordsManualCompletion()
    {
        var issue = InProgressIssue();

        var changed = issue.MarkDone(Now);

        Assert.True(changed);
        Assert.Equal(IssueStatus.Done, issue.Status);
        Assert.Equal(Now, issue.CompletedAt);
        var completed = Assert.Single(CompletionEvents(issue));
        Assert.Equal("wr_1", completed.WorkflowRunId);
        Assert.Equal(IssueCompletionKinds.Manual, completed.CompletionKind);
    }

    [Fact]
    public void MarkDone_AlreadyDone_IsIdempotent()
    {
        var issue = InProgressIssue();
        issue.MarkDone(Now);

        var changed = issue.MarkDone(Now.AddMinutes(1));

        Assert.False(changed);
        Assert.Equal(Now, issue.CompletedAt);
        Assert.Single(CompletionEvents(issue));
    }

    [Fact]
    public void MarkDone_BacklogIssue_Rejects()
    {
        var issue = DomainIssue.Create("project-1", 1, "Backlog", isDraft: false, repositoryRef: "main");

        var error = Assert.Throws<InvalidOperationException>(() => issue.MarkDone(Now));

        Assert.Contains("no workflow run", error.Message);
        Assert.Equal(IssueStatus.Backlog, issue.Status);
    }

    [Fact]
    public void MarkDone_CancelledIssue_Rejects()
    {
        var issue = InProgressIssue();
        issue.Close(now: Now);

        var error = Assert.Throws<InvalidOperationException>(() => issue.MarkDone(Now.AddMinutes(1)));

        Assert.Contains("only InProgress", error.Message);
        Assert.Equal(IssueStatus.Cancelled, issue.Status);
    }

    private static DomainIssue InProgressIssue()
    {
        var issue = DomainIssue.Create("project-1", 1, "Delivered", isDraft: false, repositoryRef: "main");
        issue.StartWorkflow("wr_1", now: Now.AddMinutes(-1));
        return issue;
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
