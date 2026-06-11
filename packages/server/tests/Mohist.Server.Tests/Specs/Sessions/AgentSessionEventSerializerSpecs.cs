using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Sessions;

public class AgentSessionEventSerializerSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void BusType_Started_ReturnsReverseDnsConstant()
    {
        var busType = AgentSessionEventSerializer.BusType(new AgentSessionStarted("acp-1"));

        Assert.Equal(EventCatalog.ReverseDns.AgentSessionStarted, busType);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void BusType_Activated_ReturnsReverseDnsConstant()
    {
        var busType = AgentSessionEventSerializer.BusType(new AgentSessionActivated("running"));

        Assert.Equal(EventCatalog.ReverseDns.AgentSessionActivated, busType);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void BusType_Completed_ReturnsReverseDnsConstant()
    {
        var busType = AgentSessionEventSerializer.BusType(new AgentSessionCompleted(0));

        Assert.Equal(EventCatalog.ReverseDns.AgentSessionCompleted, busType);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void BusType_Failed_ReturnsReverseDnsConstant()
    {
        var busType = AgentSessionEventSerializer.BusType(new AgentSessionFailed("error", 1));

        Assert.Equal(EventCatalog.ReverseDns.AgentSessionFailed, busType);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void BusType_Cancelled_ReturnsReverseDnsConstant()
    {
        var busType = AgentSessionEventSerializer.BusType(new AgentSessionCancelled("stopped", 2));

        Assert.Equal(EventCatalog.ReverseDns.AgentSessionCancelled, busType);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void BusType_StatusChanged_ReturnsReverseDnsConstant()
    {
        var busType = AgentSessionEventSerializer.BusType(new AgentSessionStatusChanged("stalled", null));

        Assert.Equal(EventCatalog.ReverseDns.AgentSessionStatusChanged, busType);
    }
}
