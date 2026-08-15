using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

public sealed class RecordingBackgroundTaskLauncherTests
{
    [Fact]
    public async Task Launch_WithCanceledToken_DoesNotQueueOrCountOrInvoke()
    {
        await using var launcher = new RecordingBackgroundTaskLauncher();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invoked = false;

        launcher.Launch(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, cancellation.Token);

        Assert.Equal(0, launcher.LaunchCount);
        Assert.Equal(0, launcher.PendingCount);
        Assert.False(invoked);
    }

    [Fact]
    public async Task ConcurrentWaiters_OwnDistinctLaunchesInEnqueueOrder()
    {
        await using var launcher = new RecordingBackgroundTaskLauncher();
        var firstWaiter = launcher.ExpectNextLaunch();
        var secondWaiter = launcher.ExpectNextLaunch();

        launcher.Launch(_ => Task.CompletedTask);
        launcher.Launch(_ => Task.CompletedTask);

        var first = await firstWaiter;
        var second = await secondWaiter;
        Assert.NotSame(first, second);
        Assert.Equal(2, launcher.LaunchCount);

        await launcher.StartAsync(first);
        await launcher.StartAsync(second);
    }

    [Fact]
    public async Task RequireExpectedLaunch_ProducerReturnedWithoutPoke_FailsImmediatelyAndRemovesWaiter()
    {
        await using var launcher = new RecordingBackgroundTaskLauncher();
        var expected = launcher.ExpectNextLaunch();

        var error = Assert.Throws<InvalidOperationException>(() =>
            launcher.RequireExpectedLaunch(expected, "producer returned without poke"));

        Assert.Equal("producer returned without poke", error.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => expected);
        launcher.Launch(_ => Task.CompletedTask);
        Assert.Equal(1, launcher.PendingCount);
    }

    [Fact]
    public async Task DrainAsync_AwaitsStartedWorkAfterTestOwnedRelease()
    {
        await using var launcher = new RecordingBackgroundTaskLauncher();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        launcher.Launch(_ => release.Task);
        var work = await launcher.ExpectNextLaunch();
        var started = launcher.StartAsync(work);
        await work.Started;

        release.TrySetResult();
        launcher.Release(work);
        await launcher.DrainAsync();
        await started;
        Assert.True(work.Completed.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DrainAsync_ReportsUnreleasedNonCooperativeCallbackWithoutWaiting()
    {
        var launcher = new RecordingBackgroundTaskLauncher();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            launcher.Launch(_ => release.Task);
            var work = await launcher.ExpectNextLaunch();
            var started = launcher.StartAsync(work);
            await work.Started;

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.DrainAsync().AsTask());
            Assert.Contains("release test-owned work", error.Message, StringComparison.Ordinal);

            release.TrySetResult();
            launcher.Release(work);
            await launcher.DrainAsync();
            await started;
        }
        finally
        {
            release.TrySetResult();
            await launcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task PreStartCancellation_CompletesBothSignalsAndRepeatedCancelIsIdempotent()
    {
        await using var launcher = new RecordingBackgroundTaskLauncher();
        using var cancellation = new CancellationTokenSource();
        var invoked = false;
        launcher.Launch(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, cancellation.Token);
        var work = await launcher.ExpectNextLaunch();
        work.HoldBeforeCallback();
        var started = launcher.StartAsync(work);

        cancellation.Cancel();
        cancellation.Cancel();
        work.ReleaseBeforeCallback();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Started);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Completed);
        await started;
        Assert.False(invoked);
    }

    [Fact]
    public async Task DisposeAsync_CancelsPreStartWorkAndCanBeCalledAgain()
    {
        var launcher = new RecordingBackgroundTaskLauncher();
        var invoked = false;
        try
        {
            launcher.Launch(_ =>
            {
                invoked = true;
                return Task.CompletedTask;
            });
            var work = await launcher.ExpectNextLaunch();
            work.HoldBeforeCallback();
            var started = launcher.StartAsync(work);

            await launcher.DisposeAsync();
            await started;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Started);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Completed);
            await launcher.DisposeAsync();
            Assert.False(invoked);
        }
        finally
        {
            await launcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentDisposeAsync_DoesNotRaceCancellationWithCtsDispose()
    {
        var launcher = new RecordingBackgroundTaskLauncher();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            launcher.Launch(_ => release.Task);
            var work = await launcher.ExpectNextLaunch();
            var started = launcher.StartAsync(work);
            await work.Started;

            release.TrySetResult();
            launcher.Release(work);
            var first = launcher.DisposeAsync().AsTask();
            var second = launcher.DisposeAsync().AsTask();
            await Task.WhenAll(first, second);
            await started;
        }
        finally
        {
            release.TrySetResult();
            await launcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DrainAsync_AllowsCancellationCallbackToReenterReleaseAndDispose()
    {
        var launcher = new RecordingBackgroundTaskLauncher();
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingBackgroundTaskLauncher.PokeWork? work = null;
        Task? reentrantDispose = null;
        try
        {
            launcher.Launch(async cancellationToken =>
            {
                using var registration = cancellationToken.Register(() =>
                {
                    launcher.Release(work!);
                    workReleased.TrySetResult();
                    reentrantDispose = launcher.DisposeAsync().AsTask();
                    callbackCompleted.TrySetResult();
                });
                await workReleased.Task;
            });
            work = await launcher.ExpectNextLaunch();
            var started = launcher.StartAsync(work);
            await work.Started;

            await launcher.DrainAsync();
            await callbackCompleted.Task;
            Assert.NotNull(reentrantDispose);
            await reentrantDispose!;
            await started;
            Assert.True(work.Completed.IsCompletedSuccessfully);
        }
        finally
        {
            await launcher.DisposeAsync();
        }
    }
}
