using Mohist.Server.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Mohist.Server.UnitTests.Agent;

public sealed class AgentJobDispatchProbeTests
{
    private static AgentJobDispatchProbe CreateProbe() =>
        new(new FakeTimeProvider(TestTime.UtcNow));

    [Fact]
    public async Task WaitForAssignmentReadyForPollAsync_CompletesWhenSignalArrivesBeforeWaiter()
    {
        var probe = CreateProbe();

        await probe.AssignmentReadyForPollAsync("job", "runner", "work");

        var ready = await probe.WaitForAssignmentReadyForPollAsync("job");
        Assert.Equal("job", ready.AgentJobId);
        Assert.Equal("runner", ready.RunnerId);
        Assert.Equal("work", ready.WorkId);
        Assert.Equal(0, probe.RetainedReadySnapshotCount("job"));
    }

    [Fact]
    public async Task WaitForAssignmentReadyForPollAsync_CompletesWhenSignalArrivesAfterWaiter()
    {
        var probe = CreateProbe();
        var waiting = probe.WaitForAssignmentReadyForPollAsync("job");

        await probe.WaitForReadyWaiterRegisteredAsync("job");
        await probe.AssignmentReadyForPollAsync("job", "runner", "work");

        var ready = await waiting;
        Assert.Equal("work", ready.WorkId);
        Assert.Equal(0, probe.RetainedReadySnapshotCount("job"));
    }

    [Fact]
    public async Task WaitForAssignmentReadyForPollAsync_CancellationReleasesWaiter()
    {
        var probe = CreateProbe();
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
    public async Task WaitForAssignmentReadyForPollAsync_WhenPreparedButReadyEventIsLost_FailsAfterOneFakeAdvance()
    {
        var time = new FakeTimeProvider(TestTime.UtcNow);
        var probe = new AgentJobDispatchProbe(time);
        var timeout = TimeSpan.FromMinutes(1);
        await probe.AssignmentPreparedAsync("missing", "runner", "work");
        var advances = 0;
        var error = await Record.ExceptionAsync(() =>
            probe.WaitForAssignmentReadyForPollFromCurrentPointWithClockAsync(
                "missing",
                timeout,
                amount =>
                {
                    advances++;
                    time.Advance(amount);
                }));

        Assert.IsType<TimeoutException>(error);
        Assert.Equal(1, advances);
        Assert.Contains("AgentJob 'missing'", error!.Message, StringComparison.Ordinal);
        Assert.Contains("dispatch readiness", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, probe.RetainedReadySignalCount("missing"));
        Assert.Equal(0, probe.RetainedReadySnapshotCount("missing"));
    }

    [Fact]
    public async Task WaitForAssignmentReadyForPollAsync_ConsumesOnlyTheRequestedJobSnapshot()
    {
        var probe = CreateProbe();

        await probe.AssignmentReadyForPollAsync("job-a", "runner-a", "work-a");
        await probe.AssignmentReadyForPollAsync("job-b", "runner-b", "work-b");

        var ready = await probe.WaitForAssignmentReadyForPollAsync("job-a");

        Assert.Equal("work-a", ready.WorkId);
        Assert.Equal(0, probe.RetainedReadySnapshotCount("job-a"));
        Assert.Equal(1, probe.RetainedReadySnapshotCount("job-b"));

        var other = await probe.WaitForAssignmentReadyForPollAsync("job-b");
        Assert.Equal("work-b", other.WorkId);
        Assert.Equal(0, probe.RetainedReadySnapshotCount("job-b"));
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_WithoutTimeout_CompletesWhenSignalArrivesBeforeWaiter()
    {
        var probe = CreateProbe();

        await probe.AssignmentPreparedAsync("job", "runner", "work");

        await probe.WaitForAssignmentPreparedAsync("job");
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_WithoutTimeout_CompletesWhenSignalArrivesAfterWaiter()
    {
        var probe = CreateProbe();
        var waiting = probe.WaitForAssignmentPreparedAsync("job");

        await probe.WaiterRegisteredAsync("job");
        await probe.AssignmentPreparedAsync("job", "runner", "work");

        await waiting;
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_WithoutTimeout_CancellationReleasesWaiter()
    {
        var probe = CreateProbe();
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
    public async Task WaitForAssignmentPreparedAsync_WithoutTimeout_WhenSignalIsLostFailsAfterOneFakeAdvance()
    {
        var time = new FakeTimeProvider(TestTime.UtcNow);
        var advances = 0;
        var probe = new AgentJobDispatchProbe(
            time,
            amount =>
            {
                advances++;
                time.Advance(amount);
            });

        var error = await Record.ExceptionAsync(() =>
            probe.WaitForAssignmentPreparedFromCurrentPointWithClockAsync(
                "missing",
                AgentJobDispatchProbe.DefaultWaitTimeout,
                amount =>
                {
                    advances++;
                    time.Advance(amount);
                }));

        Assert.IsType<TimeoutException>(error);
        Assert.Equal(1, advances);
        Assert.Contains("AgentJob 'missing'", error!.Message, StringComparison.Ordinal);
        Assert.Equal(0, probe.RetainedSignalCount("missing"));
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_CompletesWhenSignalArrivesBeforeWaiter()
    {
        var probe = CreateProbe();

        await probe.AssignmentPreparedAsync("job", "runner", "work");
        await probe.WaitForAssignmentPreparedAsync("job", TimeSpan.FromSeconds(1));

        Assert.Equal(1, probe.PreparedCount("job"));
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_CompletesAllConcurrentWaiters()
    {
        var probe = CreateProbe();
        var first = probe.WaitForAssignmentPreparedAsync("job", TimeSpan.FromSeconds(1));
        var second = probe.WaitForAssignmentPreparedAsync("job", TimeSpan.FromSeconds(1));

        await probe.AssignmentPreparedAsync("job", "runner", "work");
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task WaitForAssignmentPreparedAsync_RetiresCancelledWaiter()
    {
        var probe = CreateProbe();
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
        var probe = CreateProbe();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            probe.WaitForAssignmentPreparedAsync("job", TimeSpan.Zero));
    }

    [Fact]
    public async Task WaitForRunnerAcceptedAsync_CompletesWhenSignalArrivesBeforeWaiter()
    {
        var probe = CreateProbe();

        await probe.RunnerAcceptedAsync("job", "runner", "work");

        var accepted = await probe.WaitForRunnerAcceptedAsync("job");

        Assert.Equal("job", accepted.AgentJobId);
        Assert.Equal("runner", accepted.RunnerId);
        Assert.Equal("work", accepted.WorkId);
        Assert.Equal(0, probe.RetainedRunnerAcceptedSnapshotCount("job"));
    }

    [Fact]
    public async Task WaitForRunnerAcceptedAsync_CompletesWhenSignalArrivesAfterWaiter()
    {
        var probe = CreateProbe();
        var waiting = probe.WaitForRunnerAcceptedAsync("job");

        await probe.WaitForRunnerAcceptedWaiterRegisteredAsync("job");
        await probe.RunnerAcceptedAsync("job", "runner", "work");

        var accepted = await waiting;
        Assert.Equal("work", accepted.WorkId);
        Assert.Equal(0, probe.RetainedRunnerAcceptedSignalCount("job"));
    }

    [Fact]
    public async Task WaitForRunnerAcceptedAsync_CancellationReleasesWaiter()
    {
        var probe = CreateProbe();
        using var cancellation = new CancellationTokenSource();
        var waiting = probe.WaitForRunnerAcceptedAsync("job", cancellation.Token);

        await probe.WaitForRunnerAcceptedWaiterRegisteredAsync("job");
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, probe.RetainedRunnerAcceptedSignalCount("job"));
    }
}
