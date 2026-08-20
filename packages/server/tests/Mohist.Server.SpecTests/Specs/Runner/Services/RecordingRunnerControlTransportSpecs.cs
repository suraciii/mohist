using Mohist.Server.Runner.Services.WebSocket;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Services;

public sealed class RecordingRunnerControlTransportSpecs
{
    [Fact]
    public async Task ScopedOwnerRoutesOnlyItsExactRunnerId()
    {
        var transport = new RecordingRunnerControlTransport();
        using var owner = transport.CreateOwner("runner-1");
        owner.SetInvocationResponse("test", "response");

        Assert.True(transport.IsConnected("runner-1"));
        Assert.False(transport.IsConnected("runner-2"));
        Assert.Equal("response", await transport.SendRequestAsync<object, string>("runner-1", "test", new()));
        await Assert.ThrowsAsync<RunnerControlUnavailableException>(() =>
            transport.SendRequestAsync<object, string>("runner-2", "test", new()));
        Assert.Single(owner.Invocations);
        Assert.Empty(transport.Invocations);
    }

    [Fact]
    public async Task GlobalModeRequiresConfiguredResponseAndRecordsGlobally()
    {
        var transport = new RecordingRunnerControlTransport();
        Assert.False(transport.IsConnected("runner-1"));
        transport.SetInvocationResponse("test", "response");

        Assert.True(transport.IsConnected("runner-1"));
        Assert.Equal("response", await transport.SendRequestAsync<object, string>("runner-1", "test", new()));
        Assert.Single(transport.Invocations);
    }

    [Fact]
    public async Task GlobalModeIsIsolatedBetweenConcurrentExecutionContexts()
    {
        var transport = new RecordingRunnerControlTransport();
        using var owner = transport.CreateOwner("exact-runner");
        var firstConfigured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondConfigured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(async () =>
        {
            transport.Clear();
            transport.SetInvocationResponse("shared.method", "first-response");
            firstConfigured.SetResult();
            await secondConfigured.Task;

            Assert.True(transport.IsConnected("first-runner"));
            var response = await transport.SendRequestAsync<object, string>("first-runner", "shared.method", new());
            return (response, transport.Invocations);
        });
        var second = Task.Run(async () =>
        {
            transport.Clear();
            transport.SetInvocationResponse("shared.method", "second-response");
            secondConfigured.SetResult();
            await firstConfigured.Task;

            Assert.True(transport.IsConnected("second-runner"));
            var response = await transport.SendRequestAsync<object, string>("second-runner", "shared.method", new());
            return (response, transport.Invocations);
        });

        var results = await Task.WhenAll(first, second);

        Assert.Equal("first-response", results[0].response);
        Assert.Equal("first-runner", Assert.Single(results[0].Invocations).ConnectionId);
        Assert.Equal("second-response", results[1].response);
        Assert.Equal("second-runner", Assert.Single(results[1].Invocations).ConnectionId);
        Assert.Empty(owner.Invocations);
    }
}
