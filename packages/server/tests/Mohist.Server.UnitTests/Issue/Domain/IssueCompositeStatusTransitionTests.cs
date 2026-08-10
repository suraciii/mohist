using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.UnitTests.Issue.Domain;

public class IssueCompositeStatusTransitionTests
{
    private static readonly DateTime Now = new(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);

    private static DomainIssue CreateParent(int number = 1) =>
        DomainIssue.Create(
            "project-1",
            number,
            $"Parent #{number}",
            isDraft: false,
            repositoryRef: "main",
            now: Now);

    private static IReadOnlyCollection<ChildSnapshot> Children(params IssueStatus[] statuses)
    {
        var list = new List<ChildSnapshot>(statuses.Length);
        for (var i = 0; i < statuses.Length; i++)
        {
            list.Add(new ChildSnapshot(Number: 100 + i, Status: statuses[i]));
        }
        return list;
    }

    private static IssueCompositeStatusChanged SingleStatusChange(DomainIssue parent)
    {
        IssueCompositeStatusChanged? found = null;
        var count = 0;
        foreach (var evt in parent.PendingEvents)
        {
            if (evt is IssueCompositeStatusChanged change)
            {
                found = change;
                count++;
            }
        }
        Assert.NotNull(found);
        Assert.Equal(1, count);
        return found!;
    }

    private static IReadOnlyList<T> EventsOfType<T>(DomainIssue parent) where T : class
    {
        var list = new List<T>();
        foreach (var evt in parent.PendingEvents)
        {
            if (evt is T match) list.Add(match);
        }
        return list;
    }

    [Fact]
    public void MarkCompositeStarted_TransitionsBacklogToInProgress_WithoutWorkflowSideEffects()
    {
        var parent = CreateParent();
        Assert.Equal(IssueStatus.Backlog, parent.Status);

        var changed = parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));

        Assert.True(changed);
        Assert.Equal(IssueStatus.InProgress, parent.Status);
        Assert.Null(parent.WorkflowRunId);
        Assert.False(parent.HasWorkflowStarted);
        Assert.Equal(Now.AddMinutes(1), parent.UpdatedAt);
        Assert.Single(EventsOfType<IssueCompositeStarted>(parent));
    }

    [Fact]
    public void MarkCompositeStarted_IsNoopWhenAlreadyInProgress()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
        parent.ClearPendingEvents();
        var startCount = parent.PendingEvents.Count;

        var changed = parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(2));

        Assert.False(changed);
        Assert.Equal(IssueStatus.InProgress, parent.Status);
        Assert.Equal(startCount, parent.PendingEvents.Count);
    }

    [Fact]
    public void MarkCompositeStarted_ThrowsOnEmptyChildrenSnapshot()
    {
        var parent = CreateParent();

        Assert.Throws<IssueEmptyCompositeSnapshotException>(
            () => parent.MarkCompositeStarted([], Now));

        Assert.Equal(IssueStatus.Backlog, parent.Status);
    }

    [Fact]
    public void MarkCompositeStarted_RejectsNonBacklogSourceState()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
        parent.MarkCompositeDone(Children(IssueStatus.Done), Now.AddMinutes(2));

        Assert.Throws<InvalidOperationException>(
            () => parent.MarkCompositeStarted(Children(IssueStatus.Done), Now.AddMinutes(3)));
    }

    [Fact]
    public void MarkCompositeDone_TransitionsInProgressToDone_WhenAllTerminalWithDone()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
        var completedAt = Now.AddMinutes(5);

        var changed = parent.MarkCompositeDone(
            Children(IssueStatus.Done, IssueStatus.Done, IssueStatus.Cancelled),
            completedAt);

        Assert.True(changed);
        Assert.Equal(IssueStatus.Done, parent.Status);
        Assert.Equal(completedAt, parent.CompletedAt);
        var statusChanged = SingleStatusChange(parent);
        Assert.Equal("inProgress", statusChanged.PreviousStatus);
        Assert.Equal("done", statusChanged.NewStatus);
    }

    [Fact]
    public void MarkCompositeDone_IsNoopWhenAlreadyDone()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
        parent.MarkCompositeDone(Children(IssueStatus.Done), Now.AddMinutes(2));
        parent.ClearPendingEvents();
        var startCount = parent.PendingEvents.Count;

        var changed = parent.MarkCompositeDone(Children(IssueStatus.Done), Now.AddMinutes(3));

        Assert.False(changed);
        Assert.Equal(IssueStatus.Done, parent.Status);
        Assert.Equal(startCount, parent.PendingEvents.Count);
    }

    [Fact]
    public void MarkCompositeDone_ThrowsOnEmptyChildrenSnapshot()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));

        Assert.Throws<IssueEmptyCompositeSnapshotException>(
            () => parent.MarkCompositeDone([], Now.AddMinutes(2)));

        Assert.Equal(IssueStatus.InProgress, parent.Status);
    }

    [Fact]
    public void MarkCompositeDone_RejectsNonInProgressSourceState()
    {
        var parent = CreateParent();

        Assert.Throws<InvalidOperationException>(
            () => parent.MarkCompositeDone(Children(IssueStatus.Done), Now.AddMinutes(1)));
    }

    [Fact]
    public void MarkCompositeDone_RejectsSnapshotThatDoesNotYieldDone()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(
            () => parent.MarkCompositeDone(
                Children(IssueStatus.InProgress, IssueStatus.Done),
                Now.AddMinutes(2)));
    }

    [Fact]
    public void MarkCompositeCancelled_TransitionsInProgressToCancelled_WhenAllChildrenCancelled()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
        parent.MarkCompositeCancelled(Children(IssueStatus.Cancelled, IssueStatus.Cancelled), Now.AddMinutes(2));

        Assert.Equal(IssueStatus.Cancelled, parent.Status);
        Assert.Null(parent.CompletedAt);
        var statusChanged = SingleStatusChange(parent);
        Assert.Equal("inProgress", statusChanged.PreviousStatus);
        Assert.Equal("cancelled", statusChanged.NewStatus);
    }

    [Fact]
    public void MarkCompositeCancelled_TransitionsBacklogToCancelled_WhenAllChildrenCancelled()
    {
        var parent = CreateParent();

        var changed = parent.MarkCompositeCancelled(
            Children(IssueStatus.Cancelled, IssueStatus.Cancelled),
            Now.AddMinutes(1));

        Assert.True(changed);
        Assert.Equal(IssueStatus.Cancelled, parent.Status);
        var statusChanged = SingleStatusChange(parent);
        Assert.Equal("backlog", statusChanged.PreviousStatus);
        Assert.Equal("cancelled", statusChanged.NewStatus);
    }

    [Fact]
    public void MarkCompositeCancelled_IsNoopWhenAlreadyCancelled()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
        parent.MarkCompositeCancelled(Children(IssueStatus.Cancelled, IssueStatus.Cancelled), Now.AddMinutes(2));
        parent.ClearPendingEvents();
        var startCount = parent.PendingEvents.Count;

        var changed = parent.MarkCompositeCancelled(
            Children(IssueStatus.Cancelled, IssueStatus.Cancelled),
            Now.AddMinutes(3));

        Assert.False(changed);
        Assert.Equal(startCount, parent.PendingEvents.Count);
    }

    [Fact]
    public void MarkCompositeCancelled_IsNoopFromBacklogAfterExplicitReopen()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
        parent.MarkCompositeCancelled(Children(IssueStatus.Cancelled), Now.AddMinutes(2));
        parent.ReopenComposite(Now.AddMinutes(3));
        Assert.True(parent.CompositeReopenFence);
        parent.ClearPendingEvents();

        var changed = parent.MarkCompositeCancelled(Children(IssueStatus.Cancelled), Now.AddMinutes(4));

        Assert.False(changed);
        Assert.Equal(IssueStatus.Backlog, parent.Status);
        Assert.Empty(parent.PendingEvents);
    }

    [Fact]
    public void MarkCompositeCancelled_ThrowsOnEmptyChildrenSnapshot()
    {
        var parent = CreateParent();

        Assert.Throws<IssueEmptyCompositeSnapshotException>(
            () => parent.MarkCompositeCancelled([], Now.AddMinutes(1)));
    }

    [Fact]
    public void MarkCompositeCancelled_RejectsSnapshotThatDoesNotYieldCancelled()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(
            () => parent.MarkCompositeCancelled(
                Children(IssueStatus.Done, IssueStatus.Cancelled),
                Now.AddMinutes(2)));
    }

    [Fact]
    public void ReopenComposite_TransitionsCancelledToBacklog_WithoutCoordinatorCheck()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
        parent.MarkCompositeCancelled(Children(IssueStatus.Cancelled, IssueStatus.Cancelled), Now.AddMinutes(2));
        parent.ClearPendingEvents();

        var changed = parent.ReopenComposite(Now.AddMinutes(3));

        Assert.True(changed);
        Assert.Equal(IssueStatus.Backlog, parent.Status);
        var statusChanged = SingleStatusChange(parent);
        Assert.Equal("cancelled", statusChanged.PreviousStatus);
        Assert.Equal("backlog", statusChanged.NewStatus);
    }

    [Fact]
    public void ReopenComposite_IsNoopWhenNotCancelled()
    {
        var parent = CreateParent();
        var startCount = parent.PendingEvents.Count;

        var changed = parent.ReopenComposite(Now.AddMinutes(1));

        Assert.False(changed);
        Assert.Equal(IssueStatus.Backlog, parent.Status);
        Assert.Equal(startCount, parent.PendingEvents.Count);
    }

    [Fact]
    public void ReopenComposite_DoesNotRequireChildrenSnapshot()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
        parent.MarkCompositeCancelled(Children(IssueStatus.Cancelled), Now.AddMinutes(2));

        parent.ReopenComposite(Now.AddMinutes(3));

        Assert.Equal(IssueStatus.Backlog, parent.Status);
    }

    [Theory]
    [InlineData(IssueStatus.Backlog)]
    [InlineData(IssueStatus.InProgress)]
    public void RecomputeCompositeStatus_AnyRunningChildYieldsInProgress(IssueStatus childStatus)
    {
        var parent = CreateParent();

        var target = parent.RecomputeCompositeStatus(Children(childStatus, IssueStatus.Done));

        Assert.Equal(IssueStatus.InProgress, target);
    }

    [Fact]
    public void RecomputeCompositeStatus_AllBacklogChildrenYieldsBacklog()
    {
        var parent = CreateParent();

        var target = parent.RecomputeCompositeStatus(
            Children(IssueStatus.Backlog, IssueStatus.Backlog, IssueStatus.Backlog));

        Assert.Equal(IssueStatus.Backlog, target);
    }

    [Fact]
    public void RecomputeCompositeStatus_AllTerminalWithAtLeastOneDoneYieldsDone()
    {
        var parent = CreateParent();

        var target = parent.RecomputeCompositeStatus(
            Children(IssueStatus.Done, IssueStatus.Done, IssueStatus.Cancelled));

        Assert.Equal(IssueStatus.Done, target);
    }

    [Fact]
    public void RecomputeCompositeStatus_AllCancelledYieldsCancelled()
    {
        var parent = CreateParent();

        var target = parent.RecomputeCompositeStatus(
            Children(IssueStatus.Cancelled, IssueStatus.Cancelled));

        Assert.Equal(IssueStatus.Cancelled, target);
    }

    [Fact]
    public void RecomputeCompositeStatus_MixedDoneAndCancelledYieldsDone()
    {
        var parent = CreateParent();

        var target = parent.RecomputeCompositeStatus(
            Children(IssueStatus.Done, IssueStatus.Cancelled));

        Assert.Equal(IssueStatus.Done, target);
    }

    [Fact]
    public void RecomputeCompositeStatus_ThrowsOnEmptySnapshot()
    {
        var parent = CreateParent();

        Assert.Throws<IssueEmptyCompositeSnapshotException>(
            () => parent.RecomputeCompositeStatus([]));
    }

    [Fact]
    public void CloseComposite_RejectsNonTerminalChildren_WithoutMutation()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
        parent.ClearPendingEvents();

        var exception = Assert.Throws<IssueParentHasNonTerminalChildrenException>(() =>
            parent.Close(
                Children(IssueStatus.InProgress, IssueStatus.Done, IssueStatus.Backlog),
                "user-cancelled",
                Now.AddMinutes(2)));

        Assert.Equal([100, 102], exception.NonTerminalChildNumbers);
        Assert.Equal(IssueStatus.InProgress, parent.Status);
        Assert.Empty(parent.PendingEvents);
    }

    [Fact]
    public void CloseComposite_AcceptsAllTerminalSnapshot_ThenAppliesNormalCloseGuard()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));

        parent.Close(
            Children(IssueStatus.Done, IssueStatus.Cancelled),
            "user-cancelled",
            Now.AddMinutes(2));

        Assert.Equal(IssueStatus.Cancelled, parent.Status);
        Assert.Contains(parent.PendingEvents, evt => evt is IssueCancelled);
    }

    [Fact]
    public void CloseComposite_WithAllTerminalChildren_StillRejectsDoneParent()
    {
        var parent = CreateParent();
        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
        parent.MarkCompositeDone(Children(IssueStatus.Done), Now.AddMinutes(2));

        Assert.Throws<InvalidOperationException>(() =>
            parent.Close(Children(IssueStatus.Done), "user-cancelled", Now.AddMinutes(3)));
    }

    [Fact]
    public void ArchiveForced_ArchivesFromEveryStatus_AndIsIdempotent()
    {
        foreach (var status in Enum.GetValues<IssueStatus>())
        {
            var issue = CreateParent((int)status + 1);
            switch (status)
            {
                case IssueStatus.Backlog:
                    break;
                case IssueStatus.InProgress:
                    issue.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
                    break;
                case IssueStatus.Done:
                    issue.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
                    issue.MarkCompositeDone(Children(IssueStatus.Done), Now.AddMinutes(2));
                    break;
                case IssueStatus.Cancelled:
                    issue.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
                    issue.MarkCompositeCancelled(Children(IssueStatus.Cancelled), Now.AddMinutes(2));
                    break;
            }
            issue.ClearPendingEvents();

            issue.ArchiveForced(Now.AddMinutes(3));
            issue.ArchiveForced(Now.AddMinutes(4));

            Assert.Equal(Now.AddMinutes(3), issue.ArchivedAt);
            Assert.Single(EventsOfType<IssueArchived>(issue));
        }
    }

    [Fact]
    public void Archive_DirectPathStillRequiresDone()
    {
        var issue = CreateParent();

        Assert.Throws<InvalidOperationException>(() => issue.Archive(Now.AddMinutes(1)));
        Assert.Null(issue.ArchivedAt);
    }

    [Fact]
    public void CompositeTransitions_DoNotTouchRepositoryBindingRevision()
    {
        var parent = CreateParent();
        var beforeRevision = parent.RepositoryBindingRevision;

        parent.MarkCompositeStarted(Children(IssueStatus.Backlog), Now.AddMinutes(1));
        Assert.Equal(beforeRevision, parent.RepositoryBindingRevision);

        parent.MarkCompositeDone(Children(IssueStatus.Done), Now.AddMinutes(2));
        Assert.Equal(beforeRevision, parent.RepositoryBindingRevision);
    }
}
