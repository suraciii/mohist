using Mohist.Server.Epic.Services;
using Mohist.Server.Issue.Services;
using Xunit;

namespace Mohist.Server.Tests.Specs.Epic.Domain;

public class EpicProgressBuildSpecs
{
    [Fact]
    public void ActiveAndBlockedIssues_AreDerivedFromHealthNotStatus()
    {
        var linked = new[]
        {
            Issue("issue_1", number: 1, title: "Active A", status: "in_progress", health: "active"),
            Issue("issue_2", number: 2, title: "Blocked B", status: "in_progress", health: "blocked"),
            Issue("issue_3", number: 3, title: "Pending C", status: "backlog", health: "queued"),
        };

        var progress = EpicProgress.Build(linked);

        Assert.Single(progress.ActiveIssues);
        Assert.Single(progress.ActiveIssues, e => e.Id == "issue_1");
        Assert.Single(progress.BlockedIssues);
        Assert.Single(progress.BlockedIssues, e => e.Id == "issue_2");
        Assert.DoesNotContain(progress.ActiveIssues, e => e.Id == "issue_3");
        Assert.DoesNotContain(progress.BlockedIssues, e => e.Id == "issue_3");
    }

    [Fact]
    public void ActiveAndBlockedEntries_CarryIdentityAndHealth()
    {
        var linked = new[]
        {
            Issue("issue_1", number: 11, title: "In flight", status: "in_progress", health: "active"),
            Issue("issue_2", number: 12, title: "Stuck", status: "in_progress", health: "blocked"),
        };

        var progress = EpicProgress.Build(linked);

        var active = Assert.Single(progress.ActiveIssues);
        Assert.Equal("issue_1", active.Id);
        Assert.Equal(11, active.Number);
        Assert.Equal("In flight", active.Title);
        Assert.Equal("active", active.Health);

        var blocked = Assert.Single(progress.BlockedIssues);
        Assert.Equal("issue_2", blocked.Id);
        Assert.Equal(12, blocked.Number);
        Assert.Equal("Stuck", blocked.Title);
        Assert.Equal("blocked", blocked.Health);
    }

    [Fact]
    public void NextIssue_PrefersHighestPriorityStartableIssue()
    {
        var linked = new[]
        {
            Issue("issue_low", number: 1, title: "P4 startable", priority: "p4", canStart: true),
            Issue("issue_high", number: 2, title: "P0 startable", priority: "p0", canStart: true),
            Issue("issue_mid", number: 3, title: "P2 startable", priority: "p2", canStart: true),
        };

        var progress = EpicProgress.Build(linked);

        Assert.NotNull(progress.NextIssue);
        Assert.Equal("issue_high", progress.NextIssue!.Id);
        Assert.Null(progress.NextIssueReason);
    }

    [Fact]
    public void NextIssue_IgnoresNonStartableIssuesEvenWhenInsertedFirst()
    {
        var linked = new[]
        {
            Issue("issue_blocked", number: 1, title: "Blocked by #99", priority: "p0",
                canStart: false, blocker: WaitingFor(99)),
            Issue("issue_startable", number: 2, title: "Startable P4", priority: "p4",
                canStart: true),
        };

        var progress = EpicProgress.Build(linked);

        Assert.NotNull(progress.NextIssue);
        Assert.Equal("issue_startable", progress.NextIssue!.Id);
        Assert.Null(progress.NextIssueReason);
    }

    [Fact]
    public void NextIssue_IsNullAndReasonPopulated_WhenNoIssueStartable()
    {
        var linked = new[]
        {
            Issue("issue_draft", number: 1, title: "Draft issue", priority: "p3",
                canStart: false, blocker: new IssueStartBlockerDto.DraftBlocker()),
            Issue("issue_waiting", number: 2, title: "Waiting issue", priority: "p0",
                canStart: false, blocker: WaitingFor(42)),
        };

        var progress = EpicProgress.Build(linked);

        Assert.Null(progress.NextIssue);
        Assert.NotNull(progress.NextIssueReason);
        Assert.Contains("#42", progress.NextIssueReason!);
    }

    [Fact]
    public void NextIssueReason_PrefersHighestPriorityBlocker()
    {
        var linked = new[]
        {
            Issue("issue_low_blocker", number: 1, title: "Low pri blocked", priority: "p4",
                canStart: false, blocker: WaitingFor(7)),
            Issue("issue_high_blocker", number: 2, title: "High pri blocked", priority: "p0",
                canStart: false, blocker: WaitingFor(8)),
        };

        var progress = EpicProgress.Build(linked);

        Assert.Null(progress.NextIssue);
        Assert.NotNull(progress.NextIssueReason);
        Assert.Contains("#8", progress.NextIssueReason!);
    }

    [Fact]
    public void ReadyToMarkDone_IsTrue_WhenAllLinkedIssuesDelivered()
    {
        var linked = new[]
        {
            Issue("issue_1", number: 1, title: "Done A", status: "done"),
            Issue("issue_2", number: 2, title: "Done B", status: "done"),
        };

        var progress = EpicProgress.Build(linked);

        Assert.True(progress.ReadyToMarkDone);
        Assert.Equal(2, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
        Assert.Null(progress.NextIssueReason);
    }

    [Fact]
    public void ReadyToMarkDone_IsFalse_WhenAnyUndelivered_RegardlessOfNextIssueStartability()
    {
        var linked = new[]
        {
            Issue("issue_1", number: 1, title: "Done A", status: "done"),
            Issue("issue_2", number: 2, title: "Undelivered but blocked", status: "in_progress",
                canStart: false, blocker: WaitingFor(99)),
        };

        var progress = EpicProgress.Build(linked);

        Assert.False(progress.ReadyToMarkDone);
        Assert.Equal(1, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
    }

    [Fact]
    public void EmptyLinkedIssues_YieldsZeroCountsAndNoNext()
    {
        var progress = EpicProgress.Build(Array.Empty<LinkedIssueDto>());

        Assert.Equal(0, progress.DeliveredCount);
        Assert.Equal(0, progress.TotalIssueCount);
        Assert.Empty(progress.ActiveIssues);
        Assert.Empty(progress.BlockedIssues);
        Assert.Null(progress.NextIssue);
        Assert.Null(progress.NextIssueReason);
        Assert.False(progress.ReadyToMarkDone);
    }

    [Fact]
    public void GrainPath_ReadyToMarkDone_DependsOnlyOnDeliveredCounts()
    {
        var grainPathLinked = new[]
        {
            Issue("issue_1", number: 1, title: "Done", status: "done"),
            Issue("issue_2", number: 2, title: "Still in_progress", status: "in_progress", canStart: false),
        };

        var progress = EpicProgress.Build(grainPathLinked);

        Assert.False(progress.ReadyToMarkDone);
        Assert.Equal(1, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
    }

    [Fact]
    public void GrainPath_ReadyToMarkDone_True_WhenAllDelivered_EvenWithNoStartabilityData()
    {
        var grainPathLinked = new[]
        {
            Issue("issue_1", number: 1, title: "Done A", status: "done"),
            Issue("issue_2", number: 2, title: "Done B", status: "done"),
        };

        var progress = EpicProgress.Build(grainPathLinked);

        Assert.True(progress.ReadyToMarkDone);
        Assert.Equal(2, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
    }

    private static LinkedIssueDto Issue(
        string id,
        int number,
        string title,
        string status = "backlog",
        string? priority = "p2",
        string health = "active",
        bool canStart = false,
        IssueStartBlockerDto? blocker = null) =>
        new(id, number, title, status, Stage: "", Health: health, Priority: priority,
            CanStart: canStart, StartBlocker: blocker);

    private static IssueStartBlockerDto.WaitingForBlocker WaitingFor(int prerequisiteNumber) =>
        new() { Issue = new IssuePrerequisiteRefDto { Number = prerequisiteNumber } };
}
