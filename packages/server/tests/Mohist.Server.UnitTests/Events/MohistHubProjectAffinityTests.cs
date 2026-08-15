using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.TestSupport;
using Mohist.Server.User.Grains;
using Orleans;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

/// <summary>
/// Tests for the project-affinity capture added to
/// <see cref="MohistHub.OnConnectedAsync"/>. The hub reads
/// <c>?projectId=</c> from the SignalR query string (already sent by
/// the Web's <c>events-hub.ts</c>) and stores it in
/// <see cref="ConnectionSubscriptionRegistry"/> so the
/// <see cref="UserNotificationDispatcher"/> can apply the project
/// gate on every bus emit.
/// </summary>
public class MohistHubProjectAffinityTests
{
    [Fact]
    public async Task OnConnectedAsync_QueryStringHasProjectId_StoresAffinity()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHubWithQuery(grains, registry, connectionId: "conn-A", query: "?projectId=proj-alpha");

        await hub.OnConnectedAsync();

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Equal("proj-alpha", actual);
    }

    [Fact]
    public async Task OnConnectedAsync_QueryStringMissingProjectId_StoresNullAffinity()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHubWithQuery(grains, registry, connectionId: "conn-A", query: string.Empty);

        await hub.OnConnectedAsync();

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Null(actual);
    }

    [Fact]
    public async Task OnConnectedAsync_QueryStringEmptyProjectId_NormalisesToNull()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHubWithQuery(grains, registry, connectionId: "conn-A", query: "?projectId=");

        await hub.OnConnectedAsync();

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Null(actual);
    }

    [Fact]
    public async Task OnConnectedAsync_QueryStringWhitespaceProjectId_NormalisesToNull()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHubWithQuery(grains, registry, connectionId: "conn-A", query: "?projectId=%20%20");

        await hub.OnConnectedAsync();

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Null(actual);
    }

    [Fact]
    public async Task OnConnectedAsync_NoHttpContext_LeavesRegistryUnchanged()
    {
        // A connection opened over a transport that does not
        // surface an HttpContext (theoretically possible, e.g. an
        // internal test transport or a future non-HTTP transport)
        // must not throw OnConnectedAsync — the query read is a
        // null-safe `?.` and the registry default of null is the
        // documented initial state.
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHub(grains, registry, connectionId: "conn-A");

        await hub.OnConnectedAsync();

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Null(actual);
    }

    [Fact]
    public async Task OnConnectedAsync_QueryStringProjectIdPlusOtherParams_StoresOnlyProjectId()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        var hub = NewHubWithQuery(
            grains,
            registry,
            connectionId: "conn-A",
            query: "?projectId=proj-alpha&otherParam=ignored");

        await hub.OnConnectedAsync();

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Equal("proj-alpha", actual);
    }

    [Fact]
    public async Task OnConnectedAsync_QueryStringUrlEncodedProjectId_DecodesCorrectly()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var grains = new ScriptedConnectionSubscriptionGrainFactory();
        // %2F = '/'; a project id with a slash.
        var hub = NewHubWithQuery(
            grains,
            registry,
            connectionId: "conn-A",
            query: "?projectId=proj%2Falpha");

        await hub.OnConnectedAsync();

        Assert.True(registry.TryGetProjectId("conn-A", out var actual));
        Assert.Equal("proj/alpha", actual);
    }

    private static MohistHub NewHubWithQuery(
        IGrainFactory grains,
        ConnectionSubscriptionRegistry registry,
        string connectionId,
        string query)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(query);
        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));

        return new MohistHub(grains, registry)
        {
            Context = new TestHubCallerContext(connectionId, features),
            Groups = new NoopGroupManager(),
        };
    }

    private static MohistHub NewHub(
        IGrainFactory grains,
        ConnectionSubscriptionRegistry registry,
        string connectionId)
    {
        return new MohistHub(grains, registry)
        {
            Context = new TestHubCallerContext(connectionId, new FeatureCollection()),
            Groups = new NoopGroupManager(),
        };
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly string _connectionId;
        private readonly IFeatureCollection _features;
        public TestHubCallerContext(string connectionId, IFeatureCollection features)
        {
            _connectionId = connectionId;
            _features = features;
        }
        public override string ConnectionId => _connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features => _features;
        public override CancellationToken ConnectionAborted { get; } = CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class TestHttpContextFeature : IHttpContextFeature
    {
        public TestHttpContextFeature(HttpContext context) { HttpContext = context; }
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>
    /// Minimal <see cref="IGrainFactory"/> stub that satisfies only the
    /// <see cref="IConnectionSubscriptionGrain"/> lookup path used by
    /// <see cref="MohistHub.OnConnectedAsync"/>. Tests that need richer
    /// grain behaviour should reuse the
    /// <c>ScriptedConnectionSubscriptionGrainFactory</c> defined inside
    /// <c>MohistHubSpecs</c>.
    /// </summary>
    private sealed class ScriptedConnectionSubscriptionGrainFactory : IGrainFactory
    {
        public Dictionary<string, HashSet<string>> StoredSubscriptions { get; } = new(StringComparer.Ordinal);

        public IConnectionSubscriptionGrain Get(string connectionId) =>
            new ScriptedConnectionSubscriptionGrain(StoredSubscriptions, connectionId);

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

        global::Orleans.Runtime.IAddressable IGrainFactory.GetGrain(GrainId grainId)
            => throw new NotSupportedException();

        global::Orleans.Runtime.IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => throw new NotSupportedException();

        global::Orleans.Runtime.IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();

        global::Orleans.Runtime.IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey)
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

        public ScriptedConnectionSubscriptionGrain(
            Dictionary<string, HashSet<string>> stored,
            string connectionId)
        {
            _stored = stored;
            _connectionId = connectionId;
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
            if (!_stored.TryGetValue(_connectionId, out var set))
            {
                return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
            }
            return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(set, StringComparer.Ordinal));
        }
    }
}
