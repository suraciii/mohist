using Mohist.Server.Infrastructure.PublicApi;
using Xunit;

namespace Mohist.Server.UnitTests.Infrastructure.PublicApi;

public sealed class PublicProjectionNudgeTests
{
    [Fact]
    public async Task Nudge_CoalescesRepeatedSignals()
    {
        var nudge = new PublicProjectionNudge();
        nudge.Nudge();
        nudge.Nudge();
        nudge.Nudge();

        Assert.Equal(nudge.LatestGeneration, await nudge.WaitAsync(CancellationToken.None));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await nudge.WaitAsync(cancelled.Token));
    }

    [Fact]
    public async Task NudgeAndWait_CompletesOnlyAfterItsGenerationIsDrained()
    {
        var nudge = new PublicProjectionNudge();

        var drained = nudge.NudgeAndWaitAsync();
        Assert.False(drained.IsCompleted);

        var generation = await nudge.WaitAsync(CancellationToken.None);
        Assert.False(drained.IsCompleted);
        nudge.Complete(generation);

        await drained;
    }

    [Fact]
    public async Task CompletingAnEarlierGeneration_DoesNotReleaseLaterWaiters()
    {
        var nudge = new PublicProjectionNudge();

        var first = nudge.NudgeAndWaitAsync();
        var firstGeneration = await nudge.WaitAsync(CancellationToken.None);
        var second = nudge.NudgeAndWaitAsync();

        nudge.Complete(firstGeneration);
        await first;
        Assert.False(second.IsCompleted);

        var secondGeneration = await nudge.WaitAsync(CancellationToken.None);
        nudge.Complete(secondGeneration);
        await second;
    }

    [Fact]
    public async Task FailedDrain_FailsItsWaiters()
    {
        var nudge = new PublicProjectionNudge();
        var drained = nudge.NudgeAndWaitAsync();
        var generation = await nudge.WaitAsync(CancellationToken.None);

        nudge.Fail(generation, new InvalidOperationException("projection failed"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => drained);
        Assert.Equal("projection failed", error.Message);
    }
}
