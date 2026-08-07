using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Issue.Domain;

/// <summary>
/// Pure-domain specs for <see cref="DomainIssue.MarkDone"/>. The
/// HTTP route <c>POST /api/projects/{ref}/issues/{n}/done</c> calls
/// the grain's <c>MarkDoneAsync</c> which forwards to this domain
/// method once the workflow-status + has-children guards pass. The
/// grain orchestration (parent rejection, workflow-status check) is
/// exercised in <c>IssueManualDoneGrainSpecs</c>.
/// </summary>
public class IssueManualDoneDomainSpecs
{
    [Fact]
    public void MarkDone_OnInProgressIssueWithWorkflow_SetsStatusToDone()
    {
        var issue = NewInProgressIssue();

        var changed = issue.MarkDone();

        Assert.True(changed);
        Assert.Equal(IssueStatus.Done, issue.Status);
        Assert.NotNull(issue.CompletedAt);
    }

    [Fact]
    public void MarkDone_OnAlreadyDoneIssue_NoOp()
    {
        var issue = NewInProgressIssue();
        issue.MarkDone();

        var changed = issue.MarkDone();

        Assert.False(changed);
        Assert.Equal(IssueStatus.Done, issue.Status);
    }

    [Fact]
    public void MarkDone_OnBacklogIssue_Throws()
    {
        var issue = NewIssue();

        Assert.Throws<InvalidOperationException>(() => issue.MarkDone());
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
            now: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        issue.StartWorkflow("wr_seed");

        // Wipe the workflow reference without setting status to Done;
        // simulates an issue that has lost its workflow reference.
        var field = typeof(DomainIssue).GetProperty("WorkflowRunId");
        field?.SetValue(issue, null);

        Assert.Throws<InvalidOperationException>(() => issue.MarkDone());
    }

    [Fact]
    public void MarkDone_OnCancelledIssue_Throws()
    {
        var issue = NewInProgressIssue();
        issue.Close(reason: null, now: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Throws<InvalidOperationException>(() => issue.MarkDone());
    }

    [Fact]
    public void MarkDone_RecordsIssueCompletedEvent()
    {
        var issue = NewInProgressIssue();

        issue.MarkDone();

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
            now: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        issue.StartWorkflow("wr_seed");
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
            now: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}