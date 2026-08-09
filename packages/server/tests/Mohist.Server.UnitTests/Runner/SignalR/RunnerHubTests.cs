using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.TestSupport;
using Orleans;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.SignalR;

public class RunnerHubTests
{
    [Fact]
    public async Task Ping_ReturnsCallerConnectionId()
    {
        var tracker = new RunnerConnectionTracker();
        var hub = NewHub(tracker, connectionId: "conn-ping-1");

        var result = await hub.Ping();

        Assert.Equal("conn-ping-1", result);
    }

    [Fact]
    public async Task Ping_DistinctConnections_DistinctResults()
    {
        var tracker = new RunnerConnectionTracker();
        var hubA = NewHub(tracker, connectionId: "conn-A");
        var hubB = NewHub(tracker, connectionId: "conn-B");

        var resultA = await hubA.Ping();
        var resultB = await hubB.Ping();

        Assert.Equal("conn-A", resultA);
        Assert.Equal("conn-B", resultB);
        Assert.NotEqual(resultA, resultB);
    }

    [Fact]
    public void DisconnectingAnOldConnectionDoesNotRemoveTheCurrentRunnerConnection()
    {
        var tracker = new RunnerConnectionTracker();
        tracker.Register("runner-1", "old-connection");
        tracker.Register("runner-1", "new-connection");
        tracker.RegisterSession("runner-1", "session-1");

        var staleSessions = tracker.UnregisterAndGetSessions("runner-1", "old-connection");

        Assert.Empty(staleSessions);
        Assert.Equal("new-connection", tracker.GetConnectionId("runner-1"));
        Assert.Equal(["session-1"], tracker.UnregisterAndGetSessions("runner-1", "new-connection"));
    }

    [Theory]
    [InlineData(null, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("invalid/source", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef01234567", null)]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "not-a-sha256-digest")]
    public async Task OnConnectedAsync_ManagedRuntimeWithMissingOrInvalidIdentity_DoesNotResolveRunnerGrain(
        string? sourceHash,
        string? artifactDigest)
    {
        var tracker = new RunnerConnectionTracker();
        var query = new List<string>
        {
            "runnerId=runner-managed",
            "runtimeGeneration=2",
            "runtimeSessionToken=session-2",
        };
        if (sourceHash is not null)
            query.Add($"buildGitHash={sourceHash}");
        if (artifactDigest is not null)
            query.Add($"artifactDigest={artifactDigest}");
        var hub = NewHub(tracker, "connection-2", "?" + string.Join("&", query));

        await hub.OnConnectedAsync();

        Assert.Null(tracker.GetConnectionId("runner-managed"));
        Assert.Null(tracker.GetRuntimeIdentity("runner-managed", "2"));
    }

    private static RunnerHub NewHub(RunnerConnectionTracker tracker, string connectionId, string? query = null)
    {
        var features = new FeatureCollection();
        if (query is not null)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.QueryString = new QueryString(query);
            features.Set<IHttpContextFeature>(new TestHttpContextFeature(httpContext));
        }
        return new RunnerHub(tracker, new ThrowingGrainFactory(), NullLogger<RunnerHub>.Instance)
        {
            Context = new TestHubCallerContext(connectionId, features),
        };
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

    private sealed class TestHttpContextFeature(HttpContext context) : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    /// <summary>
    /// Test-only <see cref="IGrainFactory"/> that throws for every call. The Ping
    /// method under test never resolves a grain, so any accidental grain call
    /// would fail the test loudly instead of silently succeeding.
    /// </summary>
    private sealed class ThrowingGrainFactory : IGrainFactory
    {
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid grainPrimaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long grainPrimaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string grainPrimaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();
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
        global::Orleans.Runtime.IAddressable IGrainFactory.GetGrain(Type grainInterfaceType, IdSpan grainKey, string? grainClassNamePrefix)
            => throw new NotSupportedException();
        global::Orleans.Runtime.IAddressable IGrainFactory.GetGrain(Type grainInterfaceType, IdSpan grainKey)
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
}
