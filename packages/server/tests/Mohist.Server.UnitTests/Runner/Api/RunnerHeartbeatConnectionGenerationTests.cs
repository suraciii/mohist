using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Mohist.Server.Api;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Orleans;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.Api;

public sealed class RunnerHeartbeatConnectionGenerationTests
{
    [Fact]
    public async Task HeartbeatWithTheRegisteredConnectionKeepsItsGeneration()
    {
        const string runnerId = "runner-1";
        const string connectionId = "connection-1";
        var connections = new RunnerConnectionTracker();
        var generation = connections.Register(runnerId, connectionId);
        var runner = DispatchProxy.Create<IRunnerGrain, RecordingRunnerGrain>();
        var grains = DispatchProxy.Create<IGrainFactory, RunnerGrainFactory>();
        ((RunnerGrainFactory)(object)grains).Runner = runner;

        var context = new DefaultHttpContext();
        var body = Encoding.UTF8.GetBytes($"{{\"connectionId\":\"{connectionId}\"}}");
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);

        var response = await RunnerRoutes.HandleHeartbeatAsync(runnerId, context.Request, grains, connections);

        Assert.IsType<Ok>(response);
        Assert.Equal(generation, connections.GetConnectionGeneration(runnerId));
        Assert.Equal(1, ((RecordingRunnerGrain)(object)runner).HeartbeatRepairCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("stale-connection")]
    public async Task HeartbeatWithoutTheCurrentLeaseRefreshesPresenceOnly(string? heartbeatConnectionId)
    {
        const string runnerId = "runner-1";
        const string currentConnectionId = "current-connection";
        var connections = new RunnerConnectionTracker();
        var generation = connections.Register(runnerId, currentConnectionId);
        var runner = DispatchProxy.Create<IRunnerGrain, RecordingRunnerGrain>();
        var grains = DispatchProxy.Create<IGrainFactory, RunnerGrainFactory>();
        ((RunnerGrainFactory)(object)grains).Runner = runner;
        var context = new DefaultHttpContext();
        var connectionJson = heartbeatConnectionId is null ? "null" : $"\"{heartbeatConnectionId}\"";
        var body = Encoding.UTF8.GetBytes($$"""{"connectionId":{{connectionJson}},"version":"stale-version","generation":99}""");
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);

        await RunnerRoutes.HandleHeartbeatAsync(runnerId, context.Request, grains, connections);

        var recording = (RecordingRunnerGrain)(object)runner;
        Assert.Equal(0, recording.HeartbeatRepairCalls);
        Assert.Equal(1, recording.HeartbeatCalls);
        Assert.Equal(currentConnectionId, connections.GetConnectionId(runnerId));
        Assert.Equal(generation, connections.GetConnectionGeneration(runnerId));
    }

    private class RecordingRunnerGrain : DispatchProxy
    {
        public int HeartbeatRepairCalls { get; private set; }
        public int HeartbeatCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IRunnerGrain.HeartbeatRepairAsync))
            {
                HeartbeatRepairCalls++;
                return Task.CompletedTask;
            }

            if (targetMethod?.Name == nameof(IRunnerGrain.HeartbeatAsync))
            {
                HeartbeatCalls++;
                return Task.CompletedTask;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private class RunnerGrainFactory : DispatchProxy
    {
        public IRunnerGrain Runner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGrainFactory.GetGrain)
                && targetMethod.IsGenericMethod
                && targetMethod.GetGenericArguments()[0] == typeof(IRunnerGrain))
                return Runner;

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
