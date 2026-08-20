using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Events.WebSocket;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Subscriptions;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public sealed class EventPushRegistrationSpecs
{
    private readonly MohistIntegrationFixture _fixture;

    public EventPushRegistrationSpecs(MohistIntegrationFixture fixture) => _fixture = fixture;

    [Fact]
    public void EventSocketDomainBridge_IsPushOnly()
    {
        var durable = _fixture.Services.GetRequiredService<IEnumerable<Subscription>>();
        var push = _fixture.Services.GetRequiredService<IEnumerable<EventPushSubscription>>();
        Assert.DoesNotContain(durable, subscription => subscription.Handler is EventSocketDomainBridge);
        Assert.Contains(push, subscription => subscription.Handler is EventSocketDomainBridge);
    }

    [Fact]
    public void RunnerTerminalStatusHandler_IsAbsentFromDurableSubscriptions()
    {
        var durable = _fixture.Services.GetRequiredService<IEnumerable<Subscription>>();
        var push = _fixture.Services.GetRequiredService<IEnumerable<EventPushSubscription>>();

        Assert.DoesNotContain(durable, subscription => subscription.Handler is RunnerWorkflowTerminalStatusHandler);
        Assert.Contains(push, subscription => subscription.Handler is RunnerWorkflowTerminalStatusHandler);
    }
}
