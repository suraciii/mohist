using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Runner;

public sealed class RunnerSessionStopDeliveryTests
{
    [Fact]
    public async Task FailureBeforeEnqueueDoesNotStartDispatch()
    {
        var delivery = new RunnerSessionStopDelivery(
            new FailingTransport(invokeEnqueued: false),
            NullLogger<RunnerSessionStopDelivery>.Instance);

        var result = await delivery.DispatchAsync(Request());

        Assert.Null(result.Reply);
        Assert.False(result.DispatchStarted);
    }

    [Fact]
    public async Task FailureAfterEnqueueStartsDispatch()
    {
        var delivery = new RunnerSessionStopDelivery(
            new FailingTransport(invokeEnqueued: true),
            NullLogger<RunnerSessionStopDelivery>.Instance);

        var result = await delivery.DispatchAsync(Request());

        Assert.Null(result.Reply);
        Assert.True(result.DispatchStarted);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAfterEnqueue()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var delivery = new RunnerSessionStopDelivery(
            new FailingTransport(invokeEnqueued: true, cancellation.Token),
            NullLogger<RunnerSessionStopDelivery>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            delivery.DispatchAsync(Request(), cancellation.Token));
    }

    private static SessionStopDeliveryRequest Request() => new(
        "project-1", "session-1", "turn-1", "operation-1", "runner-1", "generic",
        null, null, "opencode", "runtime-session-1", "/work/session-1");

    private sealed class FailingTransport(bool invokeEnqueued, CancellationToken cancellation = default)
        : IRunnerControlTransport
    {
        public bool IsConnected(string runnerId) => true;

        public Task<TResult> SendRequestAsync<TParams, TResult>(
            string runnerId,
            string method,
            TParams parameters,
            Action? requestEnqueued = null,
            CancellationToken ct = default)
        {
            if (invokeEnqueued) requestEnqueued?.Invoke();
            if (cancellation.IsCancellationRequested) return Task.FromCanceled<TResult>(cancellation);
            return Task.FromException<TResult>(new InvalidOperationException("transport failed"));
        }

        public Task SendNotificationAsync<TParams>(string runnerId, string method, TParams parameters, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
