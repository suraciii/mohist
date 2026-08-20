using Mohist.Server.Infrastructure.Data.Runner;
using Xunit;

namespace Mohist.Server.UnitTests.Runner.Data;

public partial class TaskLogStoreSpecs
{
    [Fact]
    public async Task AcquireReleaseTurnover_RetainsTheIdentityGateForQueuedCallers()
    {
        var registry = new TaskLogAppendGateRegistry();
        var identity = new TaskLogIdentity("agent-job", "owner", "work");
        var first = registry.Acquire(identity);
        await first.Semaphore.WaitAsync();

        var second = registry.Acquire(identity);
        var secondAcquired = NewSignal();
        var secondEntered = NewSignal();
        var secondTask = Task.Run(async () =>
        {
            secondAcquired.SetResult(true);
            await second.Semaphore.WaitAsync();
            secondEntered.SetResult(true);
        });

        await secondAcquired.Task;
        first.Semaphore.Release();
        first.Dispose();
        await secondEntered.Task;

        var third = registry.Acquire(identity);
        var thirdAcquired = NewSignal();
        var thirdEntered = NewSignal();
        var thirdTask = Task.Run(async () =>
        {
            thirdAcquired.SetResult(true);
            await third.Semaphore.WaitAsync();
            thirdEntered.SetResult(true);
        });

        await thirdAcquired.Task;
        Assert.False(thirdEntered.Task.IsCompleted);

        second.Semaphore.Release();
        second.Dispose();
        await thirdEntered.Task;
        third.Semaphore.Release();
        third.Dispose();
        await thirdTask;
        await secondTask;
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
