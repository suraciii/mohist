using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Mohist.Server.Api;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
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

    private class RecordingRunnerGrain : DispatchProxy
    {
        public int HeartbeatRepairCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IRunnerGrain.HeartbeatRepairAsync))
            {
                HeartbeatRepairCalls++;
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
