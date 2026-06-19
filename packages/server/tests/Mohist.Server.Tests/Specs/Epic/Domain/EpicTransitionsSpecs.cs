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

    private static EpicAggregate NewActiveEpic() =>
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
    public void MarkDone_OnDoneEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewActiveEpic();
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
        var epic = NewActiveEpic();
        epic.Close(UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() =>
            epic.MarkDone(new HashSet<int>(), UtcNow));

        Assert.Equal("closed", ex.CurrentStatus);
        Assert.Equal("done", ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Close_OnDoneEpic_ThrowsAlreadyTerminal()
    {
        var epic = NewActiveEpic();
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
        var epic = NewActiveEpic();
        epic.Close(UtcNow);

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() =>
            epic.Close(UtcNow));

        Assert.Equal("closed", ex.CurrentStatus);
        Assert.Equal("closed", ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void MarkDone_WhenUndeliveredSetIsEmpty_TransitionsToDoneAndRecordsStatusChanged()
    {
        var epic = NewActiveEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        epic.LinkIssue("issue_2", 2, UtcNow);

        epic.MarkDone(new HashSet<int>(), UtcNow);

        Assert.Equal(EpicStatus.Done, epic.Status);
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Single();
        Assert.Equal("active", statusChanged.OldStatus);
        Assert.Equal("done", statusChanged.NewStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void MarkDone_WhenUndeliveredSetHasThree_ThrowsNotReadyToMarkDoneWithCount()
    {
        var epic = NewActiveEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);

        var undelivered = new HashSet<int> { 1, 2, 3 };

        var ex = Assert.Throws<EpicNotReadyToMarkDoneException>(() =>
            epic.MarkDone(undelivered, UtcNow));

        Assert.Equal(epic.Id, ex.EpicId);
        Assert.Equal(3, ex.UndeliveredCount);
        Assert.Equal(EpicStatus.Active, epic.Status);
        Assert.Empty(EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void MarkDone_WhenUndeliveredSetHasOne_ThrowsNotReadyToMarkDoneWithCount()
    {
        var epic = NewActiveEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);

        var ex = Assert.Throws<EpicNotReadyToMarkDoneException>(() =>
            epic.MarkDone(new HashSet<int> { 1 }, UtcNow));

        Assert.Equal(1, ex.UndeliveredCount);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void LinkIssue_DuplicateId_IsIdempotent()
    {
        var epic = NewActiveEpic();
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
        var epic = NewActiveEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        var eventsBeforeDuplicate = epic.PendingEvents.Count;

        var ex = Assert.Throws<EpicDuplicateLinkedIssueException>(() =>
            epic.LinkIssue("issue_renamed", 1, UtcNow));

        Assert.Equal(1, ex.IssueNumber);
        Assert.Single(epic.LinkedIssueNumbers);
        Assert.Equal(eventsBeforeDuplicate, epic.PendingEvents.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void LinkIssue_DistinctIds_RecordsOneLinkEventEach()
    {
        var epic = NewActiveEpic();

        epic.LinkIssue("issue_1", 1, UtcNow);
        epic.LinkIssue("issue_2", 2, UtcNow);

        Assert.Equal(2, epic.LinkedIssueNumbers.Count);
        Assert.Equal(2, EpicEventAssertions.OfType<EpicIssueLinked>(epic.PendingEvents).Count());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Close_RecordsEpicClosedAndStatusChanged()
    {
        var epic = NewActiveEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);

        epic.Close(UtcNow);

        Assert.Equal(EpicStatus.Closed, epic.Status);
        Assert.NotNull(EpicEventAssertions.OfType<EpicClosed>(epic.PendingEvents).SingleOrDefault());
        var statusChanged = EpicEventAssertions.OfType<EpicStatusChanged>(epic.PendingEvents).Single();
        Assert.Equal("active", statusChanged.OldStatus);
        Assert.Equal("closed", statusChanged.NewStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void UnlinkIssue_RemovesFromSetAndRecordsUnlinkEvent()
    {
        var epic = NewActiveEpic();
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
        var epic = NewActiveEpic();
        epic.LinkIssue("issue_1", 1, UtcNow);
        var eventsBefore = epic.PendingEvents.Count;

        epic.UnlinkIssue("issue_unknown", UtcNow);

        Assert.Single(epic.LinkedIssueNumbers);
        Assert.Equal(eventsBefore, epic.PendingEvents.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Create_StoresTypedValuesAndRecordsCreated()
    {
        var epic = EpicAggregate.Create(
            id: "epic_new",
            projectId: "project-x",
            number: 42,
            title: "New epic",
            description: "alpha",
            priority: "p1",
            now: UtcNow);

        Assert.Equal("epic_new", epic.Id);
        Assert.Equal("project-x", epic.ProjectId);
        Assert.Equal(42, epic.Number);
        Assert.Equal("New epic", epic.Title);
        Assert.Equal("alpha", epic.Description);
        Assert.Equal("p1", epic.Priority);
        Assert.Equal(EpicStatus.Active, epic.Status);
        var created = EpicEventAssertions.OfType<EpicCreated>(epic.PendingEvents).Single();
        Assert.Equal("New epic", created.Title);
        Assert.Equal("p1", created.Priority);
        Assert.Equal("alpha", created.Description);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Update_PriorityChange_RecordsPriorityChangedAndUpdatedEvents()
    {
        var epic = NewActiveEpic();
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
        var epic = NewActiveEpic();
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
        var epic = NewActiveEpic();
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
