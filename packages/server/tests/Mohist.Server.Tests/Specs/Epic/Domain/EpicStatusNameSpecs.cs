using Mohist.Server.Epic.Domain;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Epic.Domain;

public class EpicStatusNameSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Theory]
    [InlineData(EpicStatus.Idle, "idle")]
    [InlineData(EpicStatus.Running, "running")]
    [InlineData(EpicStatus.Paused, "paused")]
    [InlineData(EpicStatus.Done, "done")]
    [InlineData(EpicStatus.Closed, "closed")]
    public void ToName_ForEachStatus_ReturnsCanonicalString(EpicStatus status, string expected)
    {
        Assert.Equal(expected, EpicStatusName.ToName(status));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Theory]
    [InlineData("idle", EpicStatus.Idle)]
    [InlineData("running", EpicStatus.Running)]
    [InlineData("paused", EpicStatus.Paused)]
    [InlineData("done", EpicStatus.Done)]
    [InlineData("closed", EpicStatus.Closed)]
    [InlineData("IDLE", EpicStatus.Idle)]
    [InlineData("Running", EpicStatus.Running)]
    [InlineData("PAUSED", EpicStatus.Paused)]
    public void Parse_ForKnownNames_ReturnsMatchingEnum(string value, EpicStatus expected)
    {
        Assert.Equal(expected, EpicStatusName.Parse(value));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Parse_LegacyActive_DefensivelyMapsToIdle()
    {
        Assert.Equal(EpicStatus.Idle, EpicStatusName.Parse("active"));
        Assert.Equal(EpicStatus.Idle, EpicStatusName.Parse("ACTIVE"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Parse_NullOrEmpty_DefaultsToIdle()
    {
        Assert.Equal(EpicStatus.Idle, EpicStatusName.Parse(null));
        Assert.Equal(EpicStatus.Idle, EpicStatusName.Parse(""));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void Parse_UnknownValue_DefaultsToIdle()
    {
        Assert.Equal(EpicStatus.Idle, EpicStatusName.Parse("not-a-status"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Theory]
    [InlineData(EpicStatus.Idle)]
    [InlineData(EpicStatus.Running)]
    [InlineData(EpicStatus.Paused)]
    [InlineData(EpicStatus.Done)]
    [InlineData(EpicStatus.Closed)]
    public void RoundTrip_ToNameThenParse_ReturnsSameStatus(EpicStatus status)
    {
        var name = EpicStatusName.ToName(status);
        var parsed = EpicStatusName.Parse(name);
        Assert.Equal(status, parsed);
    }
}
