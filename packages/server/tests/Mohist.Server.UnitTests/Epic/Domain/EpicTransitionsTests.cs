using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Domain.Events;
using Xunit;
using EpicAggregate = Mohist.Server.Epic.Domain.Epic;

namespace Mohist.Server.UnitTests.Epic.Domain;

public class EpicTransitionsTests
{
    private static readonly DateTime UtcNow =
        DateTime.Parse("2026-06-19T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private static EpicAggregate NewIdleEpic() =>
        EpicAggregate.Create(
            projectId: "project-1",
            number: 1,
            title: "Container epic",
            description: "Body",
            priority: "p2",
            now: UtcNow);

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

    [Fact]
    public void Start_OnPaused_ThrowsStartRequiresIdle()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);
        epic.Pause("hold", UtcNow);

        var ex = Assert.Throws<EpicStartRequiresIdleException>(() => epic.Start(UtcNow));

        Assert.Equal(epic.Number, ex.EpicNumber);
        Assert.Equal("paused", ex.CurrentStatus);
        Assert.Equal(EpicStatus.Paused, epic.Status);
    }

    [Fact]
    public void Start_OnDone_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.MarkDone(new HashSet<int>(), UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() => epic.Start(UtcNow));

        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("running", ex.RequestedStatus);
    }

    [Fact]
    public void Start_OnClosed_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.Close(UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() => epic.Start(UtcNow));

        Assert.Equal("closed", ex.CurrentStatus);
        Assert.Equal("running", ex.RequestedStatus);
    }

    [Fact]
    public void Pause_FromRunning_SetsStatusToPausedAndPersistsReasonAndRecordsStatusChanged()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);

        epic.Pause(reason: "need to clarify scope", now: UtcNow);

        Assert.Equal(EpicStatus.Paused, epic.Status);
        Assert.Equal("need to clarify scope", epic.PauseReason);
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("running", statusChanged.OldStatus);
        Assert.Equal("paused", statusChanged.NewStatus);
        Assert.Null(EpicEventAssertions.OfType<EpicClosed>(epic.PendingEvents).SingleOrDefault());
    }

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

    [Fact]
    public void Pause_FromIdle_ThrowsPauseRequiresRunning()
    {
        var epic = NewIdleEpic();

        var ex = Assert.Throws<EpicPauseRequiresRunningException>(() => epic.Pause(null, UtcNow));

        Assert.Equal(epic.Number, ex.EpicNumber);
        Assert.Equal("idle", ex.CurrentStatus);
        Assert.Equal(EpicStatus.Idle, epic.Status);
    }

    [Fact]
    public void Pause_OnDoneEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.MarkDone(new HashSet<int>(), UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() =>
            epic.Pause(null, UtcNow));

        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("paused", ex.RequestedStatus);
    }

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

    [Fact]
    public void Resume_FromIdle_ThrowsResumeRequiresPaused()
    {
        var epic = NewIdleEpic();

        var ex = Assert.Throws<EpicResumeRequiresPausedException>(() => epic.Resume(UtcNow));

        Assert.Equal(epic.Number, ex.EpicNumber);
        Assert.Equal("idle", ex.CurrentStatus);
        Assert.Equal(EpicStatus.Idle, epic.Status);
    }

    [Fact]
    public void Resume_OnDoneEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.MarkDone(new HashSet<int>(), UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() => epic.Resume(UtcNow));

        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("running", ex.RequestedStatus);
    }

    [Fact]
    public void MarkDone_OnIdle_TransitionsToDoneAndRecordsStatusChanged()
    {
        var epic = NewIdleEpic();

        epic.MarkDone(new HashSet<int>(), UtcNow);

        Assert.Equal(EpicStatus.Done, epic.Status);
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("idle", statusChanged.OldStatus);
        Assert.Equal("done", statusChanged.NewStatus);
    }

    [Fact]
    public void MarkDone_OnRunning_TransitionsToDoneAndRecordsStatusChanged()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);

        epic.MarkDone(new HashSet<int>(), UtcNow);

        Assert.Equal(EpicStatus.Done, epic.Status);
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("running", statusChanged.OldStatus);
        Assert.Equal("done", statusChanged.NewStatus);
    }

    [Fact]
    public void MarkDone_OnPausedEpic_ThrowsPausedCannotMarkDone()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);
        epic.Pause("paused for a reason", UtcNow);

        var ex = Assert.Throws<EpicPausedCannotMarkDoneException>(() =>
            epic.MarkDone(new HashSet<int>(), UtcNow));

        Assert.Equal(epic.Number, ex.EpicNumber);
        Assert.Equal(EpicStatus.Paused, epic.Status);
    }

    [Fact]
    public void MarkDone_WhenOpenLinkedSetHasThree_ThrowsNotReadyToMarkDoneWithCount()
    {
        var epic = NewIdleEpic();

        var open = new HashSet<int> { 1, 2, 3 };

        var ex = Assert.Throws<EpicNotReadyToMarkDoneException>(() =>
            epic.MarkDone(open, UtcNow));

        Assert.Equal(epic.Number, ex.EpicNumber);
        Assert.Equal(3, ex.OpenLinkedCount);
        Assert.Equal(EpicStatus.Idle, epic.Status);
        Assert.Empty(EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents));
    }

    [Fact]
    public void MarkDone_OnDoneEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.MarkDone(new HashSet<int>(), UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() =>
            epic.MarkDone(new HashSet<int>(), UtcNow));

        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("done", ex.RequestedStatus);
    }

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

    [Fact]
    public void Close_FromIdle_TransitionsToClosedAndRecordsEpicClosed()
    {
        var epic = NewIdleEpic();

        epic.Close(UtcNow);

        Assert.Equal(EpicStatus.Closed, epic.Status);
        Assert.NotNull(EpicEventAssertions.OfType<EpicClosed>(epic.PendingEvents).SingleOrDefault());
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("idle", statusChanged.OldStatus);
        Assert.Equal("closed", statusChanged.NewStatus);
    }

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

    [Fact]
    public void Close_OnDoneEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewIdleEpic();
        epic.MarkDone(new HashSet<int>(), UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() =>
            epic.Close(UtcNow));

        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("closed", ex.RequestedStatus);
    }

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

    [Fact]
    public void Pause_WithoutReason_PersistsNullReason()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);

        epic.Pause(null, UtcNow);

        Assert.Equal(EpicStatus.Paused, epic.Status);
        Assert.Null(epic.PauseReason);
    }

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

    [Fact]
    public void Reopen_FromDone_TransitionsToIdleAndRecordsReopenedAndStatusChanged()
    {
        var epic = NewIdleEpic();
        epic.MarkDone(new HashSet<int>(), UtcNow);

        epic.Reopen(UtcNow);

        Assert.Equal(EpicStatus.Idle, epic.Status);
        Assert.False(epic.Status == EpicStatus.Done || epic.Status == EpicStatus.Closed);
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("done", statusChanged.OldStatus);
        Assert.Equal("idle", statusChanged.NewStatus);
        var reopened = EpicEventAssertions.OfType<EpicReopened>(epic.PendingEvents).Single();
        Assert.NotNull(reopened);
    }

    [Fact]
    public void Reopen_FromClosed_TransitionsToIdleAndClearsPauseReason()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);
        epic.Pause("temp", UtcNow);
        epic.Resume(UtcNow);
        epic.Close(UtcNow);

        epic.Reopen(UtcNow);

        Assert.Equal(EpicStatus.Idle, epic.Status);
        Assert.Null(epic.PauseReason);
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Last();
        Assert.Equal("closed", statusChanged.OldStatus);
        Assert.Equal("idle", statusChanged.NewStatus);
        Assert.Single(EpicEventAssertions.OfType<EpicReopened>(epic.PendingEvents));
    }

    [Fact]
    public void Reopen_FromIdle_ThrowsNotTerminalAndLeavesStateUnchanged()
    {
        var epic = NewIdleEpic();
        var eventsBefore = epic.PendingEvents.Count;
        var updatedAtBefore = epic.UpdatedAt;

        var ex = Assert.Throws<EpicNotTerminalException>(() => epic.Reopen(UtcNow));

        Assert.Equal(epic.Number, ex.EpicNumber);
        Assert.Equal("idle", ex.CurrentStatus);
        Assert.Equal(EpicStatus.Idle, epic.Status);
        Assert.Equal(eventsBefore, epic.PendingEvents.Count);
        Assert.Equal(updatedAtBefore, epic.UpdatedAt);
    }

    [Fact]
    public void Reopen_FromRunning_ThrowsNotTerminalAndLeavesStateUnchanged()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);
        var eventsBefore = epic.PendingEvents.Count;

        var ex = Assert.Throws<EpicNotTerminalException>(() => epic.Reopen(UtcNow));

        Assert.Equal("running", ex.CurrentStatus);
        Assert.Equal(EpicStatus.Running, epic.Status);
        Assert.Equal(eventsBefore, epic.PendingEvents.Count);
    }

    [Fact]
    public void Reopen_FromPaused_ThrowsNotTerminalAndLeavesStateUnchanged()
    {
        var epic = NewIdleEpic();
        epic.Start(UtcNow);
        epic.Pause("hold", UtcNow);
        var eventsBefore = epic.PendingEvents.Count;

        var ex = Assert.Throws<EpicNotTerminalException>(() => epic.Reopen(UtcNow));

        Assert.Equal("paused", ex.CurrentStatus);
        Assert.Equal(EpicStatus.Paused, epic.Status);
        Assert.Equal("hold", epic.PauseReason);
        Assert.Equal(eventsBefore, epic.PendingEvents.Count);
    }

    [Fact]
    public void EnsureNotTerminal_StillBlocksOtherTransitionsAfterReopenAdded()
    {
        // Regression: EnsureNotTerminal must continue to block Start,
        // Pause, Resume, MarkDone, and Close from terminal states.
        // Reopen is the only allowed exit.
        var epic = NewIdleEpic();
        epic.Close(UtcNow);

        Assert.Throws<EpicAlreadyTerminalException>(() => epic.Start(UtcNow));
        Assert.Throws<EpicAlreadyTerminalException>(() => epic.Pause(null, UtcNow));
        Assert.Throws<EpicAlreadyTerminalException>(() => epic.Resume(UtcNow));
        Assert.Throws<EpicAlreadyTerminalException>(() => epic.MarkDone(new HashSet<int>(), UtcNow));
        Assert.Throws<EpicAlreadyTerminalException>(() => epic.Close(UtcNow));

        // Reopen is allowed.
        epic.Reopen(UtcNow);
        Assert.Equal(EpicStatus.Idle, epic.Status);
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
