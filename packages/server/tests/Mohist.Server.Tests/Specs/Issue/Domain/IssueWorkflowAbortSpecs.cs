using Mohist.Server.Issue.Domain;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Issue.Domain;

public class IssueWorkflowAbortSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void AbortWorkflow_ActiveRun_TransitionsToCancelled_AndClearsPointer()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("i1", "p1", 1, "t");
        issue.StartWorkflow("wr_1");

        var applied = issue.AbortWorkflow("wr_1");

        Assert.True(applied);
        Assert.Equal(IssueStatus.Cancelled, issue.Status);
        Assert.Null(issue.ActiveWorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void AbortWorkflow_WrongRunId_IsNoOp()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("i1", "p1", 1, "t");
        issue.StartWorkflow("wr_1");

        var applied = issue.AbortWorkflow("wr_different");

        Assert.False(applied);
        Assert.Equal(IssueStatus.InProgress, issue.Status);
        Assert.Equal("wr_1", issue.ActiveWorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void AbortWorkflow_AlreadyCancelled_IsNoOp()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("i1", "p1", 1, "t");
        issue.StartWorkflow("wr_1");
        issue.AbortWorkflow("wr_1");

        var applied = issue.AbortWorkflow("wr_1");

        Assert.False(applied);
        Assert.Equal(IssueStatus.Cancelled, issue.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void AbortWorkflow_NotInProgress_IsNoOp()
    {
        var issue = Mohist.Server.Issue.Domain.Issue.Create("i1", "p1", 1, "t");
        issue.StartWorkflow("wr_1");
        issue.Complete("wr_1");
        // After Complete, status is Done and ActiveWorkflowRunId is null;
        // a subsequent abort for the same run id is a no-op (idempotency
        // guard against double-dispatch from the hook chain).
        var applied = issue.AbortWorkflow("wr_1");

        Assert.False(applied);
        Assert.Equal(IssueStatus.Done, issue.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Issue)]
    [Fact]
    public void Reopen_AfterFailedWorkflow_Succeeds_AndAllowsNewWorkflow()
    {
        // The G3 fix: after a failed workflow, the user can re-open the
        // issue and start a new workflow. Pre-fix, _activeWorkflowRunId
        // would still hold the failed run id and StartWorkflow would throw.
        var issue = Mohist.Server.Issue.Domain.Issue.Create("i1", "p1", 1, "t");
        issue.StartWorkflow("wr_failed");
        issue.AbortWorkflow("wr_failed");

        issue.Reopen();

        Assert.Equal(IssueStatus.Backlog, issue.Status);
        Assert.Null(issue.ActiveWorkflowRunId);

        issue.StartWorkflow("wr_new");
        Assert.Equal(IssueStatus.InProgress, issue.Status);
        Assert.Equal("wr_new", issue.ActiveWorkflowRunId);
    }
}
