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
    public void BusType_RuntimeBound_ReturnsReverseDnsConstant()
    {
        var busType = AgentSessionEventSerializer.BusType(new AgentSessionRuntimeBound("acp-1"));

        Assert.Equal(EventCatalog.ReverseDns.AgentSessionRuntimeBound, busType);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public void EventCatalog_IncludesTranscriptRuntimeEventTypes()
    {
        Assert.Contains("message.delta", EventCatalog.All);
        Assert.Contains("reasoning.delta", EventCatalog.All);
        Assert.Contains("tool_call.started", EventCatalog.All);
        Assert.Contains("session.closed", EventCatalog.All);
        Assert.Contains("usage.updated", EventCatalog.All);
        Assert.Contains("model.resolved", EventCatalog.All);
    }
}
