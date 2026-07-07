using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public class ConnectionSubscriptionRegistryTaskLogScopeSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void RegisterConnection_InitialisesTaskLogScopeAsEmpty()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");

        // Empty default is the expected initial state — the
        // publisher silently drops every delta for an
        // unexpanded-task connection.
        Assert.False(registry.ShouldNotifyTaskLog("conn-A", "wf-1", "task-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void SubscribeTaskLog_AddsPairToConnectionScope()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.Subscribe("conn-A", TaskLogDeltaSubscription.TaskLogDeltaSubscriptionType);

        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");

        Assert.True(registry.ShouldNotifyTaskLog("conn-A", "wf-1", "task-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void ShouldNotifyTaskLog_FalseWhenTypeSubscriptionMissing()
    {
        // The two filter dimensions are BOTH required. A
        // connection that subscribed to the task scope but
        // forgot to add the type marker is filtered out — the
        // publisher must check both gates.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");

        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");

        Assert.False(registry.ShouldNotifyTaskLog("conn-A", "wf-1", "task-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void ShouldNotifyTaskLog_FalseOnDifferentTask()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.Subscribe("conn-A", TaskLogDeltaSubscription.TaskLogDeltaSubscriptionType);
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");

        // The scope filter is per-(workflowRunId, taskId) pair —
        // another task in the same run must not match.
        Assert.False(registry.ShouldNotifyTaskLog("conn-A", "wf-1", "task-2"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void ShouldNotifyTaskLog_FalseOnDifferentRun()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.Subscribe("conn-A", TaskLogDeltaSubscription.TaskLogDeltaSubscriptionType);
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");

        Assert.False(registry.ShouldNotifyTaskLog("conn-A", "wf-2", "task-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void UnsubscribeTaskLog_RemovesOnlyMatchingPair()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.Subscribe("conn-A", TaskLogDeltaSubscription.TaskLogDeltaSubscriptionType);
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-2");

        registry.UnsubscribeTaskLog("conn-A", "wf-1", "task-1");

        Assert.False(registry.ShouldNotifyTaskLog("conn-A", "wf-1", "task-1"));
        Assert.True(registry.ShouldNotifyTaskLog("conn-A", "wf-1", "task-2"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void UnsubscribeTaskLog_NotSubscribed_IsNoOp()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");

        // No throw; the connection is fine afterwards.
        registry.UnsubscribeTaskLog("conn-A", "wf-1", "task-1");

        Assert.False(registry.ShouldNotifyTaskLog("conn-A", "wf-1", "task-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void SetTaskLogSubscriptions_ReplacesScopeSet()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.Subscribe("conn-A", TaskLogDeltaSubscription.TaskLogDeltaSubscriptionType);

        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-2");
        registry.SetTaskLogSubscriptions("conn-A", new[] { ("wf-1", "task-2"), ("wf-2", "task-3") });

        Assert.False(registry.ShouldNotifyTaskLog("conn-A", "wf-1", "task-1"));
        Assert.True(registry.ShouldNotifyTaskLog("conn-A", "wf-1", "task-2"));
        Assert.True(registry.ShouldNotifyTaskLog("conn-A", "wf-2", "task-3"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void ShouldNotifyTaskLog_FalseOnUnregisteredConnection()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");

        Assert.False(registry.ShouldNotifyTaskLog("conn-UNKNOWN", "wf-1", "task-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void UnregisterConnection_RemovesTaskLogScope()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.Subscribe("conn-A", TaskLogDeltaSubscription.TaskLogDeltaSubscriptionType);
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");

        registry.UnregisterConnection("conn-A");

        Assert.False(registry.ShouldNotifyTaskLog("conn-A", "wf-1", "task-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void SubscribeTaskLog_NullOrEmptyInputs_AreIgnored()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");

        registry.SubscribeTaskLog("conn-A", "", "task-1");
        registry.SubscribeTaskLog("conn-A", "wf-1", "");
        registry.SubscribeTaskLog("conn-A", null!, "task-1");

        Assert.False(registry.ShouldNotifyTaskLog("conn-A", "wf-1", "task-1"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public void ShouldNotifyTaskLog_NullOrEmptyScopeKeys_ReturnsFalse()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.Subscribe("conn-A", TaskLogDeltaSubscription.TaskLogDeltaSubscriptionType);
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");

        // A null/empty scope pair from the publisher side can
        // never match anything — short-circuit to false.
        Assert.False(registry.ShouldNotifyTaskLog("conn-A", null, "task-1"));
        Assert.False(registry.ShouldNotifyTaskLog("conn-A", "wf-1", null));
        Assert.False(registry.ShouldNotifyTaskLog("conn-A", "", "task-1"));
        Assert.False(registry.ShouldNotifyTaskLog("conn-A", "wf-1", ""));
    }
}
