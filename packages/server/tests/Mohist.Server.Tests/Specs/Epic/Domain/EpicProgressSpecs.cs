using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Services;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Epic.Domain;

public class EpicProgressSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void IsTerminal_OnActive_ReturnsFalse()
    {
        Assert.False(EpicProgress.IsTerminal(EpicStatus.Active));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void IsTerminal_OnDone_ReturnsTrue()
    {
        Assert.True(EpicProgress.IsTerminal(EpicStatus.Done));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void IsTerminal_OnClosed_ReturnsTrue()
    {
        Assert.True(EpicProgress.IsTerminal(EpicStatus.Closed));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void IsTerminal_OnPaused_ReturnsFalse()
    {
        Assert.False(EpicProgress.IsTerminal(EpicStatus.Paused));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void IsCompleted_OnDoneStatus_ReturnsTrue()
    {
        var dto = new LinkedIssueDto(Id: "i", Number: 1, Title: "t", Status: "done", Stage: "", Health: "active", Priority: "p2");
        Assert.True(EpicProgress.IsCompleted(dto));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void IsCompleted_OnCompletedStatus_ReturnsTrue()
    {
        var dto = new LinkedIssueDto(Id: "i", Number: 1, Title: "t", Status: "completed", Stage: "", Health: "blocked", Priority: "p2");
        Assert.True(EpicProgress.IsCompleted(dto));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void IsCompleted_OnActiveStatus_ReturnsFalse()
    {
        var dto = new LinkedIssueDto(Id: "i", Number: 1, Title: "t", Status: "active", Stage: "", Health: "active", Priority: "p2");
        Assert.False(EpicProgress.IsCompleted(dto));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void IsCompleted_IgnoresHealth()
    {
        var dto = new LinkedIssueDto(Id: "i", Number: 1, Title: "t", Status: "done", Stage: "", Health: "blocked", Priority: "p2");
        Assert.True(EpicProgress.IsCompleted(dto));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Build_EmptyList_ReportsZeroCountsAndNotReady()
    {
        var progress = EpicProgress.Build(Array.Empty<LinkedIssueDto>());
        Assert.Equal(0, progress.DeliveredCount);
        Assert.Equal(0, progress.TotalIssueCount);
        Assert.False(progress.ReadyToMarkDone);
        Assert.Null(progress.NextIssue);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Build_AllDelivered_ReportsReadyToMarkDone()
    {
        var linked = new[]
        {
            new LinkedIssueDto("i1", 1, "A", "done", "", "active", "p2"),
            new LinkedIssueDto("i2", 2, "B", "done", "", "active", "p2"),
        };
        var progress = EpicProgress.Build(linked);
        Assert.Equal(2, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
        Assert.True(progress.ReadyToMarkDone);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Build_HasUndelivered_NotReadyToMarkDone()
    {
        var linked = new[]
        {
            new LinkedIssueDto("i1", 1, "A", "done", "", "active", "p2"),
            new LinkedIssueDto("i2", 2, "B", "active", "", "active", "p2"),
        };
        var progress = EpicProgress.Build(linked);
        Assert.Equal(1, progress.DeliveredCount);
        Assert.Equal(2, progress.TotalIssueCount);
        Assert.False(progress.ReadyToMarkDone);
    }
}