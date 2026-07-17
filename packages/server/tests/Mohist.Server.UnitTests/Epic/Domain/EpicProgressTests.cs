using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Epic.Domain;

public class EpicProgressTests
{
    [Fact]
    public void IsTerminal_OnIdle_ReturnsFalse()
    {
        Assert.False(EpicProgress.IsTerminal(EpicStatus.Idle));
    }

    [Fact]
    public void IsTerminal_OnRunning_ReturnsFalse()
    {
        Assert.False(EpicProgress.IsTerminal(EpicStatus.Running));
    }

    [Fact]
    public void IsTerminal_OnDone_ReturnsTrue()
    {
        Assert.True(EpicProgress.IsTerminal(EpicStatus.Done));
    }

    [Fact]
    public void IsTerminal_OnClosed_ReturnsTrue()
    {
        Assert.True(EpicProgress.IsTerminal(EpicStatus.Closed));
    }

    [Fact]
    public void IsTerminal_OnPaused_ReturnsFalse()
    {
        Assert.False(EpicProgress.IsTerminal(EpicStatus.Paused));
    }

    [Fact]
    public void IsCompleted_OnDoneStatus_ReturnsTrue()
    {
        var dto = new LinkedIssueDto(Number: 1, Title: "t", Status: "done", Stage: "", Health: "active", Priority: "p2");
        Assert.True(EpicProgress.IsCompleted(dto));
    }

    [Fact]
    public void IsCompleted_OnCompletedStatus_ReturnsTrue()
    {
        var dto = new LinkedIssueDto(Number: 1, Title: "t", Status: "completed", Stage: "", Health: "blocked", Priority: "p2");
        Assert.True(EpicProgress.IsCompleted(dto));
    }

    [Fact]
    public void IsCompleted_OnActiveStatus_ReturnsFalse()
    {
        var dto = new LinkedIssueDto(Number: 1, Title: "t", Status: "active", Stage: "", Health: "active", Priority: "p2");
        Assert.False(EpicProgress.IsCompleted(dto));
    }

    [Fact]
    public void IsTerminal_OnLinkedIssueDone_ReturnsTrue()
    {
        var dto = new LinkedIssueDto(Number: 1, Title: "t", Status: "done", Stage: "", Health: "active", Priority: "p2");
        Assert.True(EpicProgress.IsTerminal(dto));
    }

    [Fact]
    public void IsTerminal_OnLinkedIssueCancelled_ReturnsTrue()
    {
        var dto = new LinkedIssueDto(Number: 1, Title: "t", Status: "cancelled", Stage: "", Health: "cancelled", Priority: "p2");
        Assert.True(EpicProgress.IsTerminal(dto));
    }

    [Fact]
    public void IsTerminal_OnLinkedIssueInProgress_ReturnsFalse()
    {
        var dto = new LinkedIssueDto(Number: 1, Title: "t", Status: "in_progress", Stage: "", Health: "active", Priority: "p2");
        Assert.False(EpicProgress.IsTerminal(dto));
    }

    [Fact]
    public void IsTerminal_OnLinkedIssueBacklog_ReturnsFalse()
    {
        var dto = new LinkedIssueDto(Number: 1, Title: "t", Status: "backlog", Stage: "", Health: "queued", Priority: "p2");
        Assert.False(EpicProgress.IsTerminal(dto));
    }

    [Fact]
    public void IsOpen_IsInverseOfIsTerminal()
    {
        var samples = new[]
        {
            new LinkedIssueDto(1, "Done", "done", "", "active", "p2"),
            new LinkedIssueDto(2, "Cancelled", "cancelled", "", "cancelled", "p2"),
            new LinkedIssueDto(3, "Backlog", "backlog", "", "queued", "p2"),
            new LinkedIssueDto(4, "InProgress", "in_progress", "", "active", "p2"),
        };
        foreach (var dto in samples)
            Assert.Equal(!EpicProgress.IsTerminal(dto), EpicProgress.IsOpen(dto));
    }

    [Fact]
    public void IsReadyToComplete_OnAllTerminal_True()
    {
        var linked = new[]
        {
            new LinkedIssueDto(1, "Done", "done", "", "active", "p2"),
            new LinkedIssueDto(2, "Cancelled", "cancelled", "", "cancelled", "p2"),
        };
        Assert.True(EpicProgress.IsReadyToComplete(linked));
    }

    [Fact]
    public void IsReadyToComplete_OnEmpty_False()
    {
        Assert.False(EpicProgress.IsReadyToComplete(Array.Empty<LinkedIssueDto>()));
    }

    [Fact]
    public void IsReadyToComplete_OnAnyOpen_False()
    {
        var linked = new[]
        {
            new LinkedIssueDto(1, "Done", "done", "", "active", "p2"),
            new LinkedIssueDto(2, "Backlog", "backlog", "", "queued", "p2"),
        };
        Assert.False(EpicProgress.IsReadyToComplete(linked));
    }

    [Fact]
    public void IsCompleted_IgnoresHealth()
    {
        var dto = new LinkedIssueDto(Number: 1, Title: "t", Status: "done", Stage: "", Health: "blocked", Priority: "p2");
        Assert.True(EpicProgress.IsCompleted(dto));
    }

    [Fact]
    public void Build_EmptyList_ReportsZeroCountsAndNotReady()
    {
        var progress = EpicProgress.Build(Array.Empty<LinkedIssueDto>());
        Assert.Equal(0, progress.DeliveredCount);
        Assert.Equal(0, progress.TotalIssueCount);
        Assert.False(progress.ReadyToMarkDone);
        Assert.Null(progress.NextIssue);
    }

    [Fact]
    public void Build_AllDelivered_ReportsReadyToMarkDone()
    {
        var linked = new[]
        {
            new LinkedIssueDto(1, "A", "done", "", "active", "p2"),
            new LinkedIssueDto(2, "B", "done", "", "active", "p2"),
        };
        var progress = EpicProgress.Build(linked);
        Assert.Equal(2, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
        Assert.True(progress.ReadyToMarkDone);
    }

    [Fact]
    public void Build_HasOpenLinkedIssue_NotReadyToMarkDone()
    {
        var linked = new[]
        {
            new LinkedIssueDto(1, "A", "done", "", "active", "p2"),
            new LinkedIssueDto(2, "B", "active", "", "active", "p2"),
        };
        var progress = EpicProgress.Build(linked);
        Assert.Equal(1, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
        Assert.False(progress.ReadyToMarkDone);
    }
}
