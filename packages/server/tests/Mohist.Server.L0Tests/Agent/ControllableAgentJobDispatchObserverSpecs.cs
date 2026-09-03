using Microsoft.Extensions.Time.Testing;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L0Tests.Agent;

[Trait("level", "L0")]
public sealed class ControllableAgentJobDispatchObserverSpecs
{
    [Fact]
    public async Task AssignmentPreparedAsync_SecondWaitForSameJobRequiresANewEvent()
    {
        var observer = new ControllableAgentJobDispatchObserver();
        const string jobId = "job";

        await observer.AssignmentPreparedAsync(jobId, "runner-1", "work-1");
        Assert.Equal("runner-1", await observer.WaitForAssignmentPreparedAsync(
            jobId,
            TimeSpan.FromSeconds(1)));

        var nextWait = observer.WaitForAssignmentPreparedAsync(jobId, TimeSpan.FromSeconds(1));
        Assert.False(nextWait.IsCompleted);

        await observer.AssignmentPreparedAsync(jobId, "runner-2", "work-2");
        Assert.Equal("runner-2", await nextWait);
    }

    [Fact]
    public async Task RunnerAcceptedAsync_TimeoutRetiresThePerJobWaiter()
    {
        var time = new FakeTimeProvider(TestTime.UtcNow);
        var observer = new ControllableAgentJobDispatchObserver(time);
        var timeout = TimeSpan.FromMinutes(1);

        var waiting = observer.WaitForRunnerAcceptedAsync("job", timeout);
        time.Advance(timeout);

        var error = await Record.ExceptionAsync(() => waiting);
        Assert.IsType<TimeoutException>(error);

        var nextWait = observer.WaitForRunnerAcceptedAsync("job", timeout);
        await observer.RunnerAcceptedAsync("job", "runner", "work");
        await nextWait;
    }

    [Fact]
    public async Task AssignmentPreparedAsync_CancellationRetiresThePerJobWaiter()
    {
        var observer = new ControllableAgentJobDispatchObserver();
        using var cancellation = new CancellationTokenSource();

        var waiting = observer.WaitForAssignmentPreparedAsync(
            "job",
            TimeSpan.FromMinutes(1),
            cancellation.Token);
        cancellation.Cancel();

        var error = await Record.ExceptionAsync(() => waiting);
        Assert.IsAssignableFrom<OperationCanceledException>(error);

        var nextWait = observer.WaitForAssignmentPreparedAsync("job", TimeSpan.FromMinutes(1));
        await observer.AssignmentPreparedAsync("job", "runner", "work");
        await nextWait;
    }
}
