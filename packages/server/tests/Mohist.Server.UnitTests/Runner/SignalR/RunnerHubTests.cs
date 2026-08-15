using System.Security.Claims;
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
        var oldGeneration = tracker.Register("runner-1", "old-connection");
        var newGeneration = tracker.Register("runner-1", "new-connection");
        tracker.RegisterSession("runner-1", "session-1");

        var staleSessions = tracker.UnregisterAndGetSessions("runner-1", "old-connection");

        Assert.Empty(staleSessions);
        Assert.NotEqual(oldGeneration, newGeneration);
        Assert.Equal(newGeneration, tracker.GetConnectionGeneration("runner-1"));
        Assert.Equal("new-connection", tracker.GetConnectionId("runner-1"));
        Assert.Equal(["session-1"], tracker.UnregisterAndGetSessions("runner-1", "new-connection"));
    }

    [Fact]
    public void RegisteringTheSameConnectionKeepsItsGeneration()
    {
        var tracker = new RunnerConnectionTracker();

        var initialGeneration = tracker.Register("runner-1", "connection-1");
        var repeatedGeneration = tracker.Register("runner-1", "connection-1");

        Assert.Equal(initialGeneration, repeatedGeneration);
        Assert.Equal(initialGeneration, tracker.GetConnectionGeneration("runner-1"));
    }

    [Fact]
    public void RegisteringANewConnectionChangesGeneration()
    {
        var tracker = new RunnerConnectionTracker();

        var initialGeneration = tracker.Register("runner-1", "connection-1");
        var replacementGeneration = tracker.Register("runner-1", "connection-2");

        Assert.NotEqual(initialGeneration, replacementGeneration);
        Assert.Equal(replacementGeneration, tracker.GetConnectionGeneration("runner-1"));
    }

    private static RunnerHub NewHub(RunnerConnectionTracker tracker, string connectionId)
    {
        return new RunnerHub(tracker, new ThrowingGrainFactory(), NullLogger<RunnerHub>.Instance)
        {
            Context = new TestHubCallerContext(connectionId),
        };
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
