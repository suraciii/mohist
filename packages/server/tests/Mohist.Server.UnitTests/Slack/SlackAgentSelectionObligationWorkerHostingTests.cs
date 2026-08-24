using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackAgentSelectionObligationWorkerHostingTests
{
    [Fact]
    public async Task HostedLoop_UsesInjectedClockAndStopsWithoutAnotherPass()
    {
        var time = new SignalingFakeTimeProvider(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var scopes = new SignalingScopeFactory();
        var worker = new SlackAgentSelectionObligationWorker(
            scopes,
            time,
            Options.Create(new SlackProviderOptions()),
            NullLogger<SlackAgentSelectionObligationWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await time.FirstTimerCreated;
        Assert.Equal(0, scopes.CreatedCount);

        time.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1));
        Assert.Equal(0, scopes.CreatedCount);

        time.Advance(TimeSpan.FromTicks(1));
        await scopes.FirstScopeDisposed;
        Assert.Equal(1, scopes.CreatedCount);

        await worker.StopAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, scopes.CreatedCount);
    }

    private sealed class SignalingFakeTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private readonly TaskCompletionSource _firstTimerCreated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstTimerCreated => _firstTimerCreated.Task;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _firstTimerCreated.TrySetResult();
            return timer;
        }
    }

    private sealed class SignalingScopeFactory : IServiceScopeFactory
    {
        private readonly TaskCompletionSource _firstScopeDisposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _createdCount;

        public int CreatedCount => Volatile.Read(ref _createdCount);
        public Task FirstScopeDisposed => _firstScopeDisposed.Task;

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _createdCount);
            return new SignalingScope(_firstScopeDisposed);
        }
    }

    private sealed class SignalingScope(TaskCompletionSource disposed) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new SignalingServiceProvider();

        public void Dispose() => disposed.TrySetResult();
    }

    private sealed class SignalingServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            throw new BoundaryReachedException(serviceType);
    }

    private sealed class BoundaryReachedException(Type serviceType)
        : Exception($"Worker pass reached the isolated scope boundary for {serviceType.Name}.");
}
