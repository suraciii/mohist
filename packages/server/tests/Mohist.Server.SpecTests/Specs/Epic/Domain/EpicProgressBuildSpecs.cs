using Mohist.Server.Epic.Services;
using Mohist.Server.Issue.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Domain;

public class EpicProgressBuildSpecs
{
    [Fact]
    public void ActiveAndBlockedIssues_AreDerivedFromHealthNotStatus()
    {
        var linked = new[]
        {
            Issue(number: 1, title: "Active A", status: "in_progress", health: "active"),
            Issue(number: 2, title: "Blocked B", status: "in_progress", health: "blocked"),
            Issue(number: 3, title: "Pending C", status: "backlog", health: "queued"),
        };

        var progress = EpicProgress.Build(linked);

        Assert.Single(progress.ActiveIssues);
        Assert.Single(progress.ActiveIssues, e => e.Number == 1);
        Assert.Single(progress.BlockedIssues);
        Assert.Single(progress.BlockedIssues, e => e.Number == 2);
        Assert.DoesNotContain(progress.ActiveIssues, e => e.Number == 3);
        Assert.DoesNotContain(progress.BlockedIssues, e => e.Number == 3);
    }

    [Fact]
    public void ActiveAndBlockedEntries_CarryIdentityAndHealth()
    {
        var linked = new[]
        {
            Issue(number: 11, title: "In flight", status: "in_progress", health: "active"),
            Issue(number: 12, title: "Stuck", status: "in_progress", health: "blocked"),
        };

        var progress = EpicProgress.Build(linked);

        var active = Assert.Single(progress.ActiveIssues);
        Assert.Equal(11, active.Number);
        Assert.Equal("In flight", active.Title);
        Assert.Equal("active", active.Health);

        var blocked = Assert.Single(progress.BlockedIssues);
        Assert.Equal(12, blocked.Number);
        Assert.Equal("Stuck", blocked.Title);
        Assert.Equal("blocked", blocked.Health);
    }

    [Fact]
    public void NextIssue_PrefersHighestPriorityStartableIssue()
    {
        var linked = new[]
        {
            Issue(number: 1, title: "P4 startable", priority: "p4", canStart: true),
            Issue(number: 2, title: "P0 startable", priority: "p0", canStart: true),
            Issue(number: 3, title: "P2 startable", priority: "p2", canStart: true),
        };

        var progress = EpicProgress.Build(linked);

        Assert.NotNull(progress.NextIssue);
        Assert.Equal(2, progress.NextIssue!.Number);
        Assert.Null(progress.NextIssueReason);
    }

    [Fact]
    public void NextIssue_IgnoresNonStartableIssuesEvenWhenInsertedFirst()
    {
        var linked = new[]
        {
            Issue(number: 1, title: "Blocked by #99", priority: "p0",
                canStart: false, blocker: WaitingFor(99)),
            Issue(number: 2, title: "Startable P4", priority: "p4",
                canStart: true),
        };

        var progress = EpicProgress.Build(linked);

        Assert.NotNull(progress.NextIssue);
        Assert.Equal(2, progress.NextIssue!.Number);
        Assert.Null(progress.NextIssueReason);
    }

    [Fact]
    public void NextIssue_IsNullAndReasonPopulated_WhenNoIssueStartable()
    {
        var linked = new[]
        {
            Issue(number: 1, title: "Draft issue", priority: "p3",
                canStart: false, blocker: new IssueStartBlockerDto.DraftBlocker()),
            Issue(number: 2, title: "Waiting issue", priority: "p0",
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
            Issue(number: 1, title: "Low pri blocked", priority: "p4",
                canStart: false, blocker: WaitingFor(7)),
            Issue(number: 2, title: "High pri blocked", priority: "p0",
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
            Issue(number: 1, title: "Done A", status: "done"),
            Issue(number: 2, title: "Done B", status: "done"),
        };

        var progress = EpicProgress.Build(linked);

        Assert.True(progress.ReadyToMarkDone);
        Assert.Equal(2, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
        Assert.Null(progress.NextIssueReason);
    }

    [Fact]
    public void ReadyToMarkDone_IsTrue_WhenAllLinkedIssuesTerminal_DeliveredCountExcludesCancelled()
    {
        // All-linked-terminal (done + cancelled) is the new readiness rule.
        // deliveredCount counts only the done issue; cancelled is terminal
        // but never counts as delivered.
        var linked = new[]
        {
            Issue(number: 1, title: "Done A", status: "done"),
            Issue(number: 2, title: "Cancelled B", status: "cancelled"),
        };

        var progress = EpicProgress.Build(linked);

        Assert.True(progress.ReadyToMarkDone);
        Assert.Equal(1, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
    }

    [Fact]
    public void ReadyToMarkDone_IsTrue_WhenOnlyCancelledLinkedIssuesRemain()
    {
        var linked = new[]
        {
            Issue(number: 1, title: "Cancelled A", status: "cancelled"),
            Issue(number: 2, title: "Cancelled B", status: "cancelled"),
        };

        var progress = EpicProgress.Build(linked);

        Assert.True(progress.ReadyToMarkDone);
        Assert.Equal(0, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
        Assert.Null(progress.NextIssue);
        Assert.Null(progress.NextIssueReason);
    }

    [Fact]
    public void ReadyToMarkDone_IsFalse_WhenAnyOpenLinkedIssueRemains_RegardlessOfNextIssueStartability()
    {
        var linked = new[]
        {
            Issue(number: 1, title: "Done A", status: "done"),
            Issue(number: 2, title: "Open but blocked", status: "in_progress",
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
    public void GrainPath_ReadyToMarkDone_FollowsOpenLinkedIssueRule()
    {
        var grainPathLinked = new[]
        {
            Issue(number: 1, title: "Done", status: "done"),
            Issue(number: 2, title: "Still in_progress", status: "in_progress", canStart: false),
        };

        var progress = EpicProgress.Build(grainPathLinked);

        Assert.False(progress.ReadyToMarkDone);
        Assert.Equal(1, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
    }

    [Fact]
    public void GrainPath_ReadyToMarkDone_True_WhenAllTerminal_EvenWithNoStartabilityData()
    {
        // No startability data (CanStart=false, StartBlocker=null) doesn't
        // change readiness — the rule is purely on terminal/open status.
        var grainPathLinked = new[]
        {
            Issue(number: 1, title: "Done A", status: "done"),
            Issue(number: 2, title: "Done B", status: "done"),
        };

        var progress = EpicProgress.Build(grainPathLinked);

        Assert.True(progress.ReadyToMarkDone);
        Assert.Equal(2, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
    }

    [Fact]
    public void Build_IgnoresPrerequisiteNumbersAndExternalPrerequisitesFields()
    {
        var withoutPrereqs = new[]
        {
            Issue(number: 1, title: "Done A", status: "done"),
            Issue(number: 2, title: "Done B", status: "done"),
        };
        var withPrereqs = new[]
        {
            Issue(number: 1, title: "Done A", status: "done",
                prerequisiteNumbers: [42, 43],
                externalPrerequisites:
                [
                    new IssuePrerequisiteRefDto { Number = 42, Title = "External 42", Stage = "in_progress", Status = "active" },
                    new IssuePrerequisiteRefDto { Number = 43, Title = "External 43", Stage = "done", Status = "done" },
                ]),
            Issue(number: 2, title: "Done B", status: "done",
                prerequisiteNumbers: [99]),
        };

        var baseline = EpicProgress.Build(withoutPrereqs);
        var withData = EpicProgress.Build(withPrereqs);

        Assert.Equal(baseline.DeliveredCount, withData.DeliveredCount);
        Assert.Equal(baseline.TotalIssueCount, withData.TotalIssueCount);
        Assert.Equal(baseline.ReadyToMarkDone, withData.ReadyToMarkDone);
        Assert.Equal(baseline.NextIssue?.Number, withData.NextIssue?.Number);
        Assert.Equal(baseline.NextIssue?.Number, withData.NextIssue?.Number);
        Assert.Equal(baseline.NextIssue?.Title, withData.NextIssue?.Title);
        Assert.Equal(baseline.NextIssueReason, withData.NextIssueReason);
        Assert.Equal(baseline.ActiveIssues.Count, withData.ActiveIssues.Count);
        Assert.Equal(baseline.BlockedIssues.Count, withData.BlockedIssues.Count);
        for (var i = 0; i < baseline.ActiveIssues.Count; i++)
        {
            Assert.Equal(baseline.ActiveIssues[i].Number, withData.ActiveIssues[i].Number);
            Assert.Equal(baseline.ActiveIssues[i].Number, withData.ActiveIssues[i].Number);
            Assert.Equal(baseline.ActiveIssues[i].Title, withData.ActiveIssues[i].Title);
            Assert.Equal(baseline.ActiveIssues[i].Health, withData.ActiveIssues[i].Health);
        }
        for (var i = 0; i < baseline.BlockedIssues.Count; i++)
        {
            Assert.Equal(baseline.BlockedIssues[i].Number, withData.BlockedIssues[i].Number);
            Assert.Equal(baseline.BlockedIssues[i].Number, withData.BlockedIssues[i].Number);
            Assert.Equal(baseline.BlockedIssues[i].Title, withData.BlockedIssues[i].Title);
            Assert.Equal(baseline.BlockedIssues[i].Health, withData.BlockedIssues[i].Health);
        }
    }

    [Fact]
    public void Build_OutputsAreUnchangedWhenPrerequisiteFieldsArePopulated()
    {
        var linked = new[]
        {
            Issue(number: 1, title: "P4 startable", priority: "p4", health: "active", canStart: true,
                prerequisiteNumbers: [10],
                externalPrerequisites: [new IssuePrerequisiteRefDto { Number = 10, Title = "Upstream", Stage = "in_progress", Status = "active" }]),
            Issue(number: 2, title: "Blocked by #99", priority: "p0", health: "blocked",
                canStart: false, blocker: WaitingFor(99),
                prerequisiteNumbers: [99],
                externalPrerequisites: [new IssuePrerequisiteRefDto { Number = 99, Title = "Missing", Stage = "", Status = "" }]),
            Issue(number: 3, title: "Draft", priority: "p2", health: "active",
                canStart: false, blocker: new IssueStartBlockerDto.DraftBlocker(),
                prerequisiteNumbers: [50, 60]),
        };

        var progress = EpicProgress.Build(linked);

        Assert.Equal(0, progress.DeliveredCount);
        Assert.Equal(3, progress.TotalIssueCount);
        Assert.NotNull(progress.NextIssue);
        Assert.Equal(1, progress.NextIssue.Number);
        Assert.Null(progress.NextIssueReason);
        Assert.False(progress.ReadyToMarkDone);
        Assert.Empty(progress.ActiveIssues);
        Assert.Empty(progress.BlockedIssues);
    }

    [Fact]
    public void NextIssue_IsNullAndReasonShowsInProgress_WhenIssueIsRunning()
    {
        var linked = new[]
        {
            Issue(number: 1, title: "In progress P0", status: "in_progress", priority: "p0", canStart: true),
            Issue(number: 2, title: "Next P1", priority: "p1", canStart: true),
        };

        var progress = EpicProgress.Build(linked);

        Assert.Null(progress.NextIssue);
        Assert.NotNull(progress.NextIssueReason);
        Assert.Contains("Waiting for #1 to complete", progress.NextIssueReason!);
        Assert.Single(progress.ActiveIssues);
        Assert.Equal(1, progress.ActiveIssues[0].Number);
    }

    [Fact]
    public void CancelledIssues_ExcludedFromPipeline()
    {
        var linked = new[]
        {
            Issue(number: 1, title: "Done", status: "done"),
            Issue(number: 2, title: "Cancelled", status: "cancelled"),
            Issue(number: 3, title: "Active", status: "in_progress", health: "active"),
        };

        var progress = EpicProgress.Build(linked);

        Assert.Equal(1, progress.DeliveredCount);
        Assert.Equal(3, progress.TotalIssueCount);
        Assert.Single(progress.ActiveIssues);
        Assert.Equal(3, progress.ActiveIssues[0].Number);
        Assert.Null(progress.NextIssue);
    }

    [Fact]
    public void ActiveAndBlocked_OnlyContainInProgressIssues()
    {
        var linked = new[]
        {
            Issue(number: 1, title: "Backlog active", status: "backlog", health: "active", canStart: true),
            Issue(number: 2, title: "In progress", status: "in_progress", health: "active"),
            Issue(number: 3, title: "Backlog blocked-ish", status: "backlog", health: "blocked"),
        };

        var progress = EpicProgress.Build(linked);

        Assert.Single(progress.ActiveIssues);
        Assert.Equal(2, progress.ActiveIssues[0].Number);
        Assert.Empty(progress.BlockedIssues);
    }

    [Fact]
    public void Build_EmptyPrerequisiteFieldsAreTheDefault()
    {
        var issue = Issue(number: 7, title: "Plain", canStart: true);

        Assert.Empty(issue.PrerequisiteNumbers);
        Assert.Empty(issue.ExternalPrerequisites);
    }

    private static LinkedIssueDto Issue(
        int number,
        string title,
        string status = "backlog",
        string? priority = "p2",
        string health = "active",
        bool canStart = false,
        IssueStartBlockerDto? blocker = null,
        int[]? prerequisiteNumbers = null,
        IReadOnlyList<IssuePrerequisiteRefDto>? externalPrerequisites = null) =>
        new(
            Number: number,
            Title: title,
            Status: status,
            Stage: "",
            Health: health,
            Priority: priority,
            CanStart: canStart,
            StartBlocker: blocker,
            PrerequisiteNumbers: prerequisiteNumbers ?? [],
            ExternalPrerequisites: externalPrerequisites ?? []);

    private static IssueStartBlockerDto.WaitingForBlocker WaitingFor(int prerequisiteNumber) =>
        new() { Issue = new IssuePrerequisiteRefDto { Number = prerequisiteNumber } };
}
