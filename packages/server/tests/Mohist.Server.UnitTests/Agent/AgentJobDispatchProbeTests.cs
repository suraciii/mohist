using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Agent;

public sealed class AgentJobDispatchProbeTests
{
    [Fact]
    public async Task WaitForAssignmentPreparedAsync_CompletesWhenSignalArrivesBeforeWaiter()
    {
        var probe = new AgentJobDispatchProbe();

        await probe.AssignmentPreparedAsync("job", "runner", "work");
        await probe.WaitForAssignmentPreparedAsync("job", TimeSpan.FromSeconds(1));

        Assert.Equal(1, probe.PreparedCount("job"));
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_CompletesAllConcurrentWaiters()
    {
        var probe = new AgentJobDispatchProbe();
        var first = probe.WaitForAssignmentPreparedAsync("job", TimeSpan.FromSeconds(1));
        var second = probe.WaitForAssignmentPreparedAsync("job", TimeSpan.FromSeconds(1));

        await probe.AssignmentPreparedAsync("job", "runner", "work");
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_RetiresCancelledWaiter()
    {
        var probe = new AgentJobDispatchProbe();
        using var cancellation = new CancellationTokenSource();
        var waiting = probe.WaitForAssignmentPreparedAsync(
            "job",
            TimeSpan.FromSeconds(1),
            cancellation.Token);

        cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => waiting);

        Assert.NotNull(error);
        Assert.Contains("Cancelled while waiting", error.Message, StringComparison.Ordinal);

        await probe.AssignmentPreparedAsync("job", "runner", "work");
        await probe.WaitForAssignmentPreparedAsync("job", TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_RejectsNonPositiveTimeout()
    {
        var probe = new AgentJobDispatchProbe();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            probe.WaitForAssignmentPreparedAsync("job", TimeSpan.Zero));
    }
}
