using Mohist.Server.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mohist.Server.UnitTests.Agent;

public sealed class AgentJobDispatchProbeTests
{
    [Fact]
    public async Task WaitForAssignmentReadyForPollAsync_CompletesWhenSignalArrivesBeforeWaiter()
    {
        var probe = new AgentJobDispatchProbe();

        await probe.AssignmentReadyForPollAsync("job", "runner", "work");

        var ready = await probe.WaitForAssignmentReadyForPollAsync("job");
        Assert.Equal("job", ready.AgentJobId);
        Assert.Equal("runner", ready.RunnerId);
        Assert.Equal("work", ready.WorkId);
    }

    [Fact]
    public async Task WaitForAssignmentReadyForPollAsync_CompletesWhenSignalArrivesAfterWaiter()
    {
        var probe = new AgentJobDispatchProbe();
        var waiting = probe.WaitForAssignmentReadyForPollAsync("job");

        await probe.WaitForReadyWaiterRegisteredAsync("job");
        await probe.AssignmentReadyForPollAsync("job", "runner", "work");

        var ready = await waiting;
        Assert.Equal("work", ready.WorkId);
    }

    [Fact]
    public async Task WaitForAssignmentReadyForPollAsync_CancellationReleasesWaiter()
    {
        var probe = new AgentJobDispatchProbe();
        using var cancellation = new CancellationTokenSource();
        var waiting = probe.WaitForAssignmentReadyForPollAsync("job", cancellation.Token);

        await probe.WaitForReadyWaiterRegisteredAsync("job");
        Assert.Equal(1, probe.RetainedReadySignalCount("job"));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, probe.RetainedReadySignalCount("job"));

        var nextWaiting = probe.WaitForAssignmentReadyForPollAsync("job");
        await probe.WaitForReadyWaiterRegisteredAsync("job");
        await probe.AssignmentReadyForPollAsync("job", "runner", "work");
        await nextWaiting;
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_WithoutTimeout_CompletesWhenSignalArrivesBeforeWaiter()
    {
        var probe = new AgentJobDispatchProbe();

        await probe.AssignmentPreparedAsync("job", "runner", "work");

        await probe.WaitForAssignmentPreparedAsync("job");
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_WithoutTimeout_CompletesWhenSignalArrivesAfterWaiter()
    {
        var probe = new AgentJobDispatchProbe();
        var waiting = probe.WaitForAssignmentPreparedAsync("job");

        await probe.WaiterRegisteredAsync("job");
        await probe.AssignmentPreparedAsync("job", "runner", "work");

        await waiting;
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_WithoutTimeout_CancellationReleasesWaiter()
    {
        var probe = new AgentJobDispatchProbe();
        using var cancellation = new CancellationTokenSource();
        var waiting = probe.WaitForAssignmentPreparedAsync("job", cancellation.Token);

        await probe.WaiterRegisteredAsync("job");
        Assert.Equal(1, probe.RetainedSignalCount("job"));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, probe.RetainedSignalCount("job"));

        var nextWaiting = probe.WaitForAssignmentPreparedAsync("job");
        await probe.WaiterRegisteredAsync("job");
        await probe.AssignmentPreparedAsync("job", "runner", "work");
        await nextWaiting;
    }

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

        await probe.WaiterRegisteredAsync("job");
        Assert.Equal(1, probe.RetainedSignalCount("job"));
        cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => waiting);

        Assert.NotNull(error);
        Assert.Contains("Cancelled while waiting", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, probe.RetainedSignalCount("job"));

        var nextWaiting = probe.WaitForAssignmentPreparedAsync("job", TimeSpan.FromSeconds(1));
        await probe.WaiterRegisteredAsync("job");
        Assert.Equal(1, probe.RetainedSignalCount("job"));
        await probe.AssignmentPreparedAsync("job", "runner", "work");
        await nextWaiting;
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_RetiresTimedOutWaiter()
    {
        var time = new FakeTimeProvider(TestTime.UtcNow);
        var probe = new AgentJobDispatchProbe(time);
        var timeout = TimeSpan.FromMinutes(1);
        var waiting = probe.WaitForAssignmentPreparedAsync("job", timeout);

        await probe.WaiterRegisteredAsync("job");
        Assert.Equal(1, probe.RetainedSignalCount("job"));
        time.Advance(timeout);
        var error = await Record.ExceptionAsync(() => waiting);

        Assert.NotNull(error);
        Assert.Contains("Timed out waiting", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, probe.RetainedSignalCount("job"));

        var nextWaiting = probe.WaitForAssignmentPreparedAsync("job", timeout);
        await probe.WaiterRegisteredAsync("job");
        Assert.Equal(1, probe.RetainedSignalCount("job"));
        await probe.AssignmentPreparedAsync("job", "runner", "work");
        await nextWaiting;
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_RejectsNonPositiveTimeout()
    {
        var probe = new AgentJobDispatchProbe();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            probe.WaitForAssignmentPreparedAsync("job", TimeSpan.Zero));
    }
}
