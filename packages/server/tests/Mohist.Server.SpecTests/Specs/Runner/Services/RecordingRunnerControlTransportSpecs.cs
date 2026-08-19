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
}
