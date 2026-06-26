using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Tests.Support;
using Xunit;
using EpicAggregate = Mohist.Server.Epic.Domain.Epic;

namespace Mohist.Server.Tests.Specs.Epic.Domain;

public class EpicTransitionsSpecs
{
    private static readonly DateTime UtcNow =
        DateTime.Parse("2026-06-19T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private static EpicAggregate NewIdleEpic() =>
        EpicAggregate.Create(
            id: "epic_1",
            projectId: "project-1",
            number: 1,
            title: "Container epic",
            description: "Body",
            priority: "p2",
            now: UtcNow);

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Create_YieldsIdleStatusAndRecordsCreated()
    {
        var epic = NewIdleEpic();

        Assert.Equal(EpicStatus.Idle, epic.Status);
        var created = EpicEventAssertions.OfType<EpicCreated>(epic.PendingEvents).Single();
        Assert.Equal("Container epic", created.Title);
        Assert.Equal("p2", created.Priority);
        Assert.Equal("Body", created.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Start_FromIdle_SetsStatusToRunningAndRecordsStatusChanged()
    {
        var epic = NewIdleEpic();

        epic.Start(UtcNow);

        Assert.Equal(EpicStatus.Running, epic.Status);
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("idle", statusChanged.OldStatus);
        Assert.Equal("running", statusChanged.NewStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Start_OnAlreadyRunning_IsIdempotentNoOp()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);
        var eventsBefore = epic.PendingEvents.Count;
        var updatedAtBefore = epic.UpdatedAt;

        epic.Start(UtcNow);

        Assert.Equal(EpicStatus.Running, epic.Status);
        Assert.Equal(eventsBefore, epic.PendingEvents.Count);
        Assert.Equal(updatedAtBefore, epic.UpdatedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Start_OnPaused_ThrowsStartRequiresIdle()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);
        epic.Pause("hold", UtcNow);

        var ex = Assert.Throws<EpicStartRequiresIdleException>(() => epic.Start(UtcNow));

        Assert.Equal(epic.Id, ex.EpicId);
        Assert.Equal("paused", ex.CurrentStatus);
        Assert.Equal(EpicStatus.Paused, epic.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Start_OnDone_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        epic.MarkDone(new HashSet<int>(), UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() => epic.Start(UtcNow));

        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("running", ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Start_OnClosed_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.Close(UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() => epic.Start(UtcNow));

        Assert.Equal("closed", ex.CurrentStatus);
        Assert.Equal("running", ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Pause_FromRunning_SetsStatusToPausedAndPersistsReasonAndRecordsStatusChanged()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        epic.Start(UtcNow);

        epic.Pause(reason: "need to clarify scope", now: UtcNow);

        Assert.Equal(EpicStatus.Paused, epic.Status);
        Assert.Equal("need to clarify scope", epic.PauseReason);
        Assert.Single(epic.LinkedIssueNumbers);
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("running", statusChanged.OldStatus);
        Assert.Equal("paused", statusChanged.NewStatus);
        Assert.Null(EpicEventAssertions.OfType<EpicClosed>(epic.PendingEvents).SingleOrDefault());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Pause_OnAlreadyPaused_IsIdempotentNoOp()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);
        epic.Pause(null, UtcNow);
        var eventsBefore = epic.PendingEvents.Count;
        var updatedAtBefore = epic.UpdatedAt;

        epic.Pause("different reason", UtcNow);

        Assert.Equal(EpicStatus.Paused, epic.Status);
        Assert.Null(epic.PauseReason);
        Assert.Equal(eventsBefore, epic.PendingEvents.Count);
        Assert.Equal(updatedAtBefore, epic.UpdatedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Pause_FromIdle_ThrowsPauseRequiresRunning()
    {
        var epic = NewIdleEpic();

        var ex = Assert.Throws<EpicPauseRequiresRunningException>(() => epic.Pause(null, UtcNow));

        Assert.Equal(epic.Id, ex.EpicId);
        Assert.Equal("idle", ex.CurrentStatus);
        Assert.Equal(EpicStatus.Idle, epic.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Pause_OnDoneEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        epic.MarkDone(new HashSet<int>(), UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() =>
            epic.Pause(null, UtcNow));

        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("paused", ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Pause_OnClosedEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.Close(UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() =>
            epic.Pause(null, UtcNow));

        Assert.Equal("closed", ex.CurrentStatus);
        Assert.Equal("paused", ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Resume_FromPaused_SetsStatusToRunningAndClearsReasonAndRecordsStatusChanged()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);
        epic.Pause("temporary hold", UtcNow);

        epic.Resume(UtcNow);

        Assert.Equal(EpicStatus.Running, epic.Status);
        Assert.Null(epic.PauseReason);
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("paused", statusChanged.OldStatus);
        Assert.Equal("running", statusChanged.NewStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Resume_OnRunningEpic_IsNoOp()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);
        var eventsBefore = epic.PendingEvents.Count;

        epic.Resume(UtcNow);

        Assert.Equal(EpicStatus.Running, epic.Status);
        Assert.Equal(eventsBefore, epic.PendingEvents.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Resume_FromIdle_ThrowsResumeRequiresPaused()
    {
        var epic = NewIdleEpic();

        var ex = Assert.Throws<EpicResumeRequiresPausedException>(() => epic.Resume(UtcNow));

        Assert.Equal(epic.Id, ex.EpicId);
        Assert.Equal("idle", ex.CurrentStatus);
        Assert.Equal(EpicStatus.Idle, epic.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Resume_OnDoneEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        epic.MarkDone(new HashSet<int>(), UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() => epic.Resume(UtcNow));

        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("running", ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void MarkDone_OnIdle_TransitionsToDoneAndRecordsStatusChanged()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        epic.LinkIssue("issue_2", 2, UtcNow);

        epic.MarkDone(new HashSet<int>(), UtcNow);

        Assert.Equal(EpicStatus.Done, epic.Status);
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("idle", statusChanged.OldStatus);
        Assert.Equal("done", statusChanged.NewStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void MarkDone_OnRunning_TransitionsToDoneAndRecordsStatusChanged()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        epic.Start(UtcNow);

        epic.MarkDone(new HashSet<int>(), UtcNow);

        Assert.Equal(EpicStatus.Done, epic.Status);
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("running", statusChanged.OldStatus);
        Assert.Equal("done", statusChanged.NewStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void MarkDone_OnPausedEpic_ThrowsPausedCannotMarkDone()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);
        epic.Pause("paused for a reason", UtcNow);

        var ex = Assert.Throws<EpicPausedCannotMarkDoneException>(() =>
            epic.MarkDone(new HashSet<int>(), UtcNow));

        Assert.Equal(epic.Id, ex.EpicId);
        Assert.Equal(EpicStatus.Paused, epic.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void MarkDone_WhenUndeliveredSetHasThree_ThrowsNotReadyToMarkDoneWithCount()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);

        var undelivered = new HashSet<int> { 1, 2, 3 };

        var ex = Assert.Throws<EpicNotReadyToMarkDoneException>(() =>
            epic.MarkDone(undelivered, UtcNow));

        Assert.Equal(epic.Id, ex.EpicId);
        Assert.Equal(3, ex.UndeliveredCount);
        Assert.Equal(EpicStatus.Idle, epic.Status);
        Assert.Empty(EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void MarkDone_OnDoneEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        epic.MarkDone(new HashSet<int>(), UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() =>
            epic.MarkDone(new HashSet<int>(), UtcNow));

        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("done", ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void MarkDone_OnClosedEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.Close(UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() =>
            epic.MarkDone(new HashSet<int>(), UtcNow));

        Assert.Equal("closed", ex.CurrentStatus);
        Assert.Equal("done", ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Close_FromIdle_TransitionsToClosedAndRecordsEpicClosed()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);

        epic.Close(UtcNow);

        Assert.Equal(EpicStatus.Closed, epic.Status);
        Assert.NotNull(EpicEventAssertions.OfType<EpicClosed>(epic.PendingEvents).SingleOrDefault());
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("idle", statusChanged.OldStatus);
        Assert.Equal("closed", statusChanged.NewStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Close_FromRunning_TransitionsToClosed()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);

        epic.Close(UtcNow);

        Assert.Equal(EpicStatus.Closed, epic.Status);
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("running", statusChanged.OldStatus);
        Assert.Equal("closed", statusChanged.NewStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Close_FromPaused_AllowedWithoutResume()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);
        epic.Pause("temporary hold", UtcNow);

        epic.Close(UtcNow);

        Assert.Equal(EpicStatus.Closed, epic.Status);
        Assert.NotNull(EpicEventAssertions.OfType<EpicClosed>(epic.PendingEvents).SingleOrDefault());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Close_OnDoneEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        epic.MarkDone(new HashSet<int>(), UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() =>
            epic.Close(UtcNow));

        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("closed", ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Close_OnClosedEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.Close(UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() =>
            epic.Close(UtcNow));

        Assert.Equal("closed", ex.CurrentStatus);
        Assert.Equal("closed", ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Pause_WithoutReason_PersistsNullReason()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);

        epic.Pause(null, UtcNow);

        Assert.Equal(EpicStatus.Paused, epic.Status);
        Assert.Null(epic.PauseReason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void LinkIssue_DuplicateId_IsIdempotent()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        var eventsBeforeDuplicate = epic.PendingEvents.Count;

        epic.LinkIssue("issue_1", 1, UtcNow);

        Assert.Single(epic.LinkedIssueNumbers);
        Assert.Equal(eventsBeforeDuplicate, epic.PendingEvents.Count);
        Assert.Equal(1, epic.LinkedIssueNumbers["issue_1"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void LinkIssue_DuplicateNumberWithDifferentId_ThrowsDuplicateLinkedIssue()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        var eventsBeforeDuplicate = epic.PendingEvents.Count;

        var ex = Assert.Throws<EpicDuplicateLinkedIssueException>(() => epic.LinkIssue("issue_renamed", 1, UtcNow));

        Assert.Equal(1, ex.IssueNumber);
        Assert.Single(epic.LinkedIssueNumbers);
        Assert.Equal(eventsBeforeDuplicate, epic.PendingEvents.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void LinkIssue_DistinctIds_RecordsOneLinkEventEach()
    {
        var epic = NewIdleEpic();

        epic.LinkIssue("issue_1", 1, UtcNow);
        epic.LinkIssue("issue_2", 2, UtcNow);

        Assert.Equal(2, epic.LinkedIssueNumbers.Count);
        Assert.Equal(2, EpicEventAssertions.OfType<EpicIssueLinked>(epic.PendingEvents).Count());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void UnlinkIssue_RemovesFromSetAndRecordsUnlinkEvent()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        epic.LinkIssue("issue_2", 2, UtcNow);

        epic.UnlinkIssue("issue_1", UtcNow);

        Assert.Single(epic.LinkedIssueNumbers);
        Assert.True(epic.LinkedIssueNumbers.ContainsKey("issue_2"));
        Assert.NotNull(EpicEventAssertions.OfType<EpicIssueUnlinked>(epic.PendingEvents).SingleOrDefault());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void UnlinkIssue_UnknownId_NoOp()
    {
        var epic = NewIdleEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        var eventsBefore = epic.PendingEvents.Count;

        epic.UnlinkIssue("issue_unknown", UtcNow);

        Assert.Single(epic.LinkedIssueNumbers);
        Assert.Equal(eventsBefore, epic.PendingEvents.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Update_PriorityChange_RecordsPriorityChangedAndUpdatedEvents()
    {
        var epic = NewIdleEpic();
        var eventsBefore = epic.PendingEvents.Count;

        epic.Update(title: null, description: null, priority: "p0", now: UtcNow);

        Assert.Equal("p0", epic.Priority);
        var evt = EpicEventAssertions.OfType<EpicPriorityChanged>(epic.PendingEvents).Single();
        Assert.Equal("p2", evt.OldPriority);
        Assert.Equal("p0", evt.NewPriority);
        var updated = EpicEventAssertions.OfType<EpicUpdated>(epic.PendingEvents).Single();
        Assert.Null(updated.Title);
        Assert.Null(updated.Description);
        Assert.Equal("p0", updated.Priority);
        Assert.Equal(eventsBefore + 2, epic.PendingEvents.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Update_TitleAndDescription_RecordsUpdatedEvent()
    {
        var epic = NewIdleEpic();
        var eventsBefore = epic.PendingEvents.Count;

        epic.Update(title: "Renamed epic", description: "Updated body", priority: null, now: UtcNow);

        Assert.Equal("Renamed epic", epic.Title);
        Assert.Equal("Updated body", epic.Description);
        var updated = EpicEventAssertions.OfType<EpicUpdated>(epic.PendingEvents).Single();
        Assert.Equal("Renamed epic", updated.Title);
        Assert.Equal("Updated body", updated.Description);
        Assert.Null(updated.Priority);
        Assert.Equal(eventsBefore + 1, epic.PendingEvents.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Update_NullFields_NoOp()
    {
        var epic = NewIdleEpic();
        var eventsBefore = epic.PendingEvents.Count;
        var updatedAtBefore = epic.UpdatedAt;

        epic.Update(title: null, description: null, priority: null, now: UtcNow);

        Assert.Equal(eventsBefore, epic.PendingEvents.Count);
        Assert.Equal(updatedAtBefore, epic.UpdatedAt);
    }
}

internal static class EpicEventAssertions
{
    public static List<T> OfType<T>(IEnumerable<EpicEvent> events) where T : class
    {
        var result = new List<T>();
        foreach (var evt in events)
        {
            switch (evt)
            {
                case T match:
                    result.Add(match);
                    break;
            }
        }
        return result;
    }
}
