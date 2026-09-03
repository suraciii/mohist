using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Foundation;

[Trait("level", "L1")]
public sealed class DefaultApplicationHostOwnerSpecs
{
    [Fact]
    public async Task Dispose_before_demand_does_not_start_the_host()
    {
        var starts = 0;
        var disposals = 0;
        var owner = new DefaultApplicationHostOwner(
            () =>
            {
                starts++;
                return Task.FromResult(new MohistIntegrationFixture());
            },
            _ =>
            {
                disposals++;
                return ValueTask.CompletedTask;
            });

        await owner.DisposeAsync();

        Assert.Equal(0, starts);
        Assert.Equal(0, disposals);
    }

    [Fact]
    public async Task Concurrent_demand_starts_one_shared_host()
    {
        var fixture = new MohistIntegrationFixture();
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var owner = new DefaultApplicationHostOwner(async () =>
        {
            Interlocked.Increment(ref starts);
            startEntered.SetResult();
            await releaseStart.Task;
            return fixture;
        });

        var first = owner.GetAsync();
        await startEntered.Task;
        var second = owner.GetAsync();
        releaseStart.SetResult();

        Assert.Same(first, second);
        Assert.Same(fixture, await first);
        Assert.Same(fixture, await second);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Failed_start_is_shared_without_retry()
    {
        var failure = new InvalidOperationException("startup failed");
        var starts = 0;
        var owner = new DefaultApplicationHostOwner(() =>
        {
            starts++;
            return Task.FromException<MohistIntegrationFixture>(failure);
        });

        var first = owner.GetAsync();
        var second = owner.GetAsync();

        Assert.Same(first, second);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => second));
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Dispose_releases_a_started_host_once()
    {
        var fixture = new MohistIntegrationFixture();
        var disposals = 0;
        var owner = new DefaultApplicationHostOwner(
            () => Task.FromResult(fixture),
            disposedFixture =>
            {
                Assert.Same(fixture, disposedFixture);
                disposals++;
                return ValueTask.CompletedTask;
            });

        Assert.Same(fixture, await owner.GetAsync());

        await owner.DisposeAsync();
        await owner.DisposeAsync();

        Assert.Equal(1, disposals);
    }

    [Fact]
    public async Task Adapters_share_the_host_without_owning_its_lifetime()
    {
        var fixture = new MohistIntegrationFixture();
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var disposals = 0;
        var owner = new DefaultApplicationHostOwner(
            async () =>
            {
                Interlocked.Increment(ref starts);
                startEntered.SetResult();
                await releaseStart.Task;
                return fixture;
            },
            _ =>
            {
                disposals++;
                return ValueTask.CompletedTask;
            });
        var first = new DefaultMohistIntegrationFixture(owner);
        var second = new DefaultMohistIntegrationFixture(owner);

        var firstInitialization = first.InitializeAsync().AsTask();
        await startEntered.Task;
        var secondInitialization = second.InitializeAsync().AsTask();
        releaseStart.SetResult();
        await Task.WhenAll(firstInitialization, secondInitialization);

        Assert.Equal(1, starts);
        Assert.Same(fixture.TimeProvider, first.TimeProvider);
        Assert.Same(fixture.TimeProvider, second.TimeProvider);

        await first.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(0, disposals);

        await owner.DisposeAsync();
        Assert.Equal(1, disposals);
    }

    [Fact]
    public async Task Dispose_waits_for_pending_start_and_rejects_later_demand()
    {
        var fixture = new MohistIntegrationFixture();
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposals = 0;
        var owner = new DefaultApplicationHostOwner(
            async () =>
            {
                startEntered.SetResult();
                await releaseStart.Task;
                return fixture;
            },
            _ =>
            {
                disposals++;
                return ValueTask.CompletedTask;
            });

        var startup = owner.GetAsync();
        await startEntered.Task;
        var disposal = owner.DisposeAsync().AsTask();
        var repeatedDisposal = owner.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        Assert.Same(disposal, repeatedDisposal);
        releaseStart.SetResult();
        Assert.Same(fixture, await startup);
        await Task.WhenAll(disposal, repeatedDisposal);

        Assert.Equal(1, disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.GetAsync());
    }

    [Fact]
    public async Task Start_failure_preserves_cleanup_failure()
    {
        var startupFailure = new InvalidOperationException("startup failed");
        var cleanupFailure = new InvalidOperationException("cleanup failed");
        var fixture = new FailingMohistIntegrationFixture(startupFailure, cleanupFailure);

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => DefaultApplicationHostOwner.StartAsync(() => fixture));

        Assert.Equal([startupFailure, cleanupFailure], failure.InnerExceptions);
    }

    private sealed class FailingMohistIntegrationFixture(
        Exception startupFailure,
        Exception cleanupFailure) : MohistIntegrationFixture
    {
        public override ValueTask InitializeAsync() => ValueTask.FromException(startupFailure);

        public override ValueTask DisposeAsync() => ValueTask.FromException(cleanupFailure);
    }
}
