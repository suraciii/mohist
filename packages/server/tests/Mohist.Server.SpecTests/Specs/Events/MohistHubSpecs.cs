using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Events.Hub;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.User.Grains;
using Orleans;
using Xunit;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.SpecTests.Specs.Events;

public class MohistHubSpecs
{
    [Fact]
    public async Task OnConnectedAsync_FreshConnection_LeavesRegistrySubscriptionSetEmpty()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHub(grains, registry);

        await hub.OnConnectedAsync();

        // Empty default is the expected initial state for a
        // freshly opened tab — ShouldNotify returns false for
        // every event type until SetSubscriptionsAsync is
        // invoked by the client.
        Assert.Contains("conn-fresh", registry.ConnectionIds);
        Assert.False(registry.ShouldNotify("conn-fresh", "com.mohist.workflow.stage.started"));
        Assert.False(registry.ShouldNotify("conn-fresh", "coder_text_chunk"));
    }

    [Fact]
    public async Task OnConnectedAsync_FreshConnection_DoesNotThrow()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHub(grains, registry);

        // Connection lifecycle: a freshly opened tab that has not
        // called SetSubscriptionsAsync yet must not error or block
        // OnConnectedAsync. The dispatcher will simply filter out
        // every emit until the client subscribes.
        await hub.OnConnectedAsync();

        Assert.Contains("conn-fresh", registry.ConnectionIds);
    }

    [Fact]
    public async Task OnConnectedAsync_GrainLookupFails_LeavesRegistryEmptyAndDoesNotThrow()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory
        {
            GetSubscriptionsThrows = true,
        };
        var hub = NewHub(grains, registry);

        // A grain failure during replay must NOT block
        // OnConnectedAsync; the connection is open and the next
        // SetSubscriptionsAsync from the client will repopulate
        // both the grain and the registry.
        await hub.OnConnectedAsync();

        Assert.Contains("conn-fresh", registry.ConnectionIds);
        Assert.False(registry.ShouldNotify("conn-fresh", "com.mohist.workflow.stage.started"));
    }

    [Fact]
    public async Task OnConnectedAsync_ReconnectWithStoredSet_ReappliesItToTheRegistry()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        grains.StoredSubscriptions["conn-reconnect"] = new HashSet<string>(StringComparer.Ordinal)
        {
            "com.mohist.workflow.stage.started",
            "coder_text_chunk",
        };
        var hub = NewHub(grains, registry, connectionId: "conn-reconnect");

        // Simulate a reconnect: a connection opens, and the
        // grain remembers the prior tab's subscription set. The
        // hub reapplies that set to the registry so the dispatcher
        // can deliver emits that the user previously opted into
        // even before the client has a chance to call
        // SetSubscriptionsAsync from onreconnected.
        await hub.OnConnectedAsync();

        Assert.True(registry.ShouldNotify("conn-reconnect", "com.mohist.workflow.stage.started"));
        Assert.True(registry.ShouldNotify("conn-reconnect", "coder_text_chunk"));
        Assert.False(registry.ShouldNotify("conn-reconnect", "stage_changed"));
    }

    [Fact]
    public async Task SetSubscriptionsAsync_FirstCall_StoresListInGrainAndRegistry()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHub(grains, registry, connectionId: "conn-A");

        await hub.OnConnectedAsync();
        Assert.False(registry.ShouldNotify("conn-A", "com.mohist.workflow.stage.started"));

        await hub.SetSubscriptionsAsync(new[]
        {
            "com.mohist.workflow.stage.started",
            "com.mohist.issue.created",
            "coder_text_chunk",
        });

        // Both the registry and the grain see the same list.
        Assert.True(registry.ShouldNotify("conn-A", "com.mohist.workflow.stage.started"));
        Assert.True(registry.ShouldNotify("conn-A", "com.mohist.issue.created"));
        Assert.True(registry.ShouldNotify("conn-A", "coder_text_chunk"));

        var stored = await grains.Get("conn-A").GetSubscriptionsAsync();
        Assert.Contains("com.mohist.workflow.stage.started", stored);
        Assert.Contains("com.mohist.issue.created", stored);
        Assert.Contains("coder_text_chunk", stored);
        Assert.Equal(3, stored.Count);
    }

    [Fact]
    public async Task SetSubscriptionsAsync_SecondCallWithSameList_IsIdempotent()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHub(grains, registry, connectionId: "conn-A");

        await hub.OnConnectedAsync();
        var list = new[]
        {
            "com.mohist.workflow.stage.started",
            "com.mohist.issue.created",
        };

        await hub.SetSubscriptionsAsync(list);
        await hub.SetSubscriptionsAsync(list);
        await hub.SetSubscriptionsAsync(list);

        // The registry holds each event type exactly once — a
        // second or third call with the same list does not
        // duplicate or shift entries.
        var stored = await grains.Get("conn-A").GetSubscriptionsAsync();
        Assert.Equal(2, stored.Count);
        Assert.True(registry.ShouldNotify("conn-A", "com.mohist.workflow.stage.started"));
        Assert.True(registry.ShouldNotify("conn-A", "com.mohist.issue.created"));
    }

    [Fact]
    public async Task SetSubscriptionsAsync_ReplacingList_PurgesPriorEntries()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHub(grains, registry, connectionId: "conn-A");

        await hub.OnConnectedAsync();
        await hub.SetSubscriptionsAsync(new[] { "com.mohist.workflow.stage.started", "old.type" });
        await hub.SetSubscriptionsAsync(new[] { "com.mohist.issue.created" });

        // Replacing the list drops the previous entries — the
        // registry no longer reports the dropped types.
        Assert.True(registry.ShouldNotify("conn-A", "com.mohist.issue.created"));
        Assert.False(registry.ShouldNotify("conn-A", "com.mohist.workflow.stage.started"));
        Assert.False(registry.ShouldNotify("conn-A", "old.type"));
    }

    [Fact]
    public async Task Dispatcher_ForConnectionWithEmptySubscriptionSet_FiltersEveryEmit()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHub(grains, registry, connectionId: "conn-empty");

        await hub.OnConnectedAsync();

        // Empty set is the expected default for a freshly opened
        // tab. The dispatcher must filter out every emit until
        // the client subscribes — this preserves the contract that
        // an open tab does not see anything it has not opted into.
        // The dispatcher behavior is exercised through its product path in
        // EventBridgeSpecs; here we lock in the registry shape
        // the dispatcher depends on.
        foreach (var eventType in new[]
        {
            "com.mohist.workflow.stage.started",
            "com.mohist.issue.created",
            "coder_text_chunk",
            "ralph_task_update",
        })
        {
            Assert.False(registry.ShouldNotify("conn-empty", eventType),
                $"expected empty set to filter {eventType}");
        }
    }

    [Fact]
    public async Task Registry_AfterSetSubscriptions_ReportsMatchingEventTypesOnly()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHub(grains, registry, connectionId: "conn-A");

        await hub.OnConnectedAsync();
        await hub.SetSubscriptionsAsync(new[]
        {
            "com.mohist.workflow.stage.started",
            "com.mohist.issue.created",
            "coder_text_chunk",
        });

        // The first SetSubscriptionsAsync after OnConnectedAsync
        // populates the registry; the very next bus emit reaches
        // the connection. This locks in the "first call wins"
        // semantic the spec requires: matches are reported,
        // non-matches are not.
        Assert.True(registry.ShouldNotify("conn-A", "com.mohist.workflow.stage.started"));
        Assert.True(registry.ShouldNotify("conn-A", "com.mohist.issue.created"));
        Assert.True(registry.ShouldNotify("conn-A", "coder_text_chunk"));
        Assert.False(registry.ShouldNotify("conn-A", "stage_changed"));
        Assert.False(registry.ShouldNotify("conn-A", "ralph_task_update"));
    }

    private static MohistHub NewHub(
        IGrainFactory grains,
        ConnectionSubscriptionRegistry registry,
        string connectionId = "conn-fresh")
    {
        var hub = new MohistHub(grains, registry)
        {
            Context = new TestHubCallerContext(connectionId),
            Groups = new NoopGroupManager(),
        };
        return hub;
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly string _connectionId;
        public TestHubCallerContext(string connectionId) { _connectionId = connectionId; }
        public override string ConnectionId => _connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted { get; } = CancellationToken.None;
        public override void Abort() { }
    }

    /// <summary>
    /// Test-only <see cref="IGrainFactory"/> that returns
    /// <see cref="ScriptedConnectionSubscriptionGrain"/> instances keyed
    /// by <c>connectionId</c> (the only grain interface MohistHub
    /// resolves). The factory also keeps an in-memory "stored
    /// subscription set" so tests can simulate the durable grain's
    /// replay-on-reconnect state without spinning up an Orleans
    /// cluster.
    /// </summary>
    private sealed class ScriptedConnectionSubscriptionGrainFactory : IGrainFactory
    {
        public Dictionary<string, HashSet<string>> StoredSubscriptions { get; } = new(StringComparer.Ordinal);
        public bool GetSubscriptionsThrows { get; set; }

        public IConnectionSubscriptionGrain Get(string connectionId) =>
            new ScriptedConnectionSubscriptionGrain(StoredSubscriptions, connectionId, () => GetSubscriptionsThrows);

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid grainPrimaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long grainPrimaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string grainPrimaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IConnectionSubscriptionGrain))
            {
                return (TGrainInterface)(object)Get(grainPrimaryKey);
            }
            throw new NotSupportedException();
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
            => throw new NotSupportedException();

        Orleans.Runtime.IAddressable IGrainFactory.GetGrain(GrainId grainId)
            => throw new NotSupportedException();

        Orleans.Runtime.IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => throw new NotSupportedException();

        Orleans.Runtime.IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix)
            => throw new NotSupportedException();

        Orleans.Runtime.IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey)
            => throw new NotSupportedException();

        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();
    }

    private sealed class ScriptedConnectionSubscriptionGrain : IConnectionSubscriptionGrain
    {
        private readonly Dictionary<string, HashSet<string>> _stored;
        private readonly string _connectionId;
        private readonly Func<bool> _getSubscriptionsThrows;
        public ScriptedConnectionSubscriptionGrain(
            Dictionary<string, HashSet<string>> stored,
            string connectionId,
            Func<bool> getSubscriptionsThrows)
        {
            _stored = stored;
            _connectionId = connectionId;
            _getSubscriptionsThrows = getSubscriptionsThrows;
        }

        public Task SetSubscriptionsAsync(IReadOnlyCollection<string> eventTypes)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (eventTypes is not null)
            {
                foreach (var t in eventTypes)
                {
                    if (!string.IsNullOrEmpty(t)) set.Add(t);
                }
            }
            _stored[_connectionId] = set;
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(string eventType)
        {
            if (string.IsNullOrEmpty(eventType)) return Task.CompletedTask;
            if (!_stored.TryGetValue(_connectionId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                _stored[_connectionId] = set;
            }
            set.Add(eventType);
            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(string eventType)
        {
            if (string.IsNullOrEmpty(eventType)) return Task.CompletedTask;
            if (_stored.TryGetValue(_connectionId, out var set))
            {
                set.Remove(eventType);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlySet<string>> GetSubscriptionsAsync()
        {
            if (_getSubscriptionsThrows()) throw new InvalidOperationException("simulated grain failure");
            if (!_stored.TryGetValue(_connectionId, out var set))
            {
                return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
            }
            return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(set, StringComparer.Ordinal));
        }
    }
}
