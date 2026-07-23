using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure;
using Mohist.Server.Otel;
using OpenTelemetry;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public sealed class BackgroundTaskIsolationTests
{
    [Fact]
    public async Task ProductionLauncher_DoesNotFlowRequestScope()
    {
        var requestScope = new RequestWorkScope();
        using var ambient = RequestWorkScope.Push(requestScope);
        var observed = new TaskCompletionSource<RequestWorkScope?>(TaskCreationOptions.RunContinuationsAsynchronously);

        new BackgroundTaskLauncher().Launch(_ =>
        {
            observed.TrySetResult(RequestWorkScope.Current);
            return Task.CompletedTask;
        });

        Assert.Null(await observed.Task);
        Assert.Same(requestScope, RequestWorkScope.Current);
    }

    [Fact]
    public void ClosedScope_RemainsImmutable()
    {
        var scope = new RequestWorkScope();
        scope.AddDatabaseCalls(2);
        var closed = scope.CloseAndSnapshot();

        scope.AddDatabaseCalls(10);
        scope.AddDownstreamCalls(10);

        Assert.Equal(2, closed.DatabaseCalls);
        Assert.Equal(0, closed.DownstreamCalls);
        Assert.Equal(closed, scope.Snapshot());
    }

    [Fact]
    public async Task OtelSuppression_DisposesAfterFailureAndCancellation()
    {
        var sawSuppression = false;
        var middleware = new OtelSuppressionMiddleware(_ =>
        {
            sawSuppression = Sdk.SuppressInstrumentation;
            throw new InvalidOperationException("expected");
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/otel/api/status";

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.True(sawSuppression);
        Assert.False(Sdk.SuppressInstrumentation);

        sawSuppression = false;
        middleware = new OtelSuppressionMiddleware(_ =>
        {
            sawSuppression = Sdk.SuppressInstrumentation;
            throw new OperationCanceledException();
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.True(sawSuppression);
        Assert.False(Sdk.SuppressInstrumentation);
    }

    [Fact]
    public async Task OtelSuppression_LeavesNormalRequestsUnchanged()
    {
        var sawSuppression = true;
        var middleware = new OtelSuppressionMiddleware(_ =>
        {
            sawSuppression = Sdk.SuppressInstrumentation;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/health";

        await middleware.InvokeAsync(context);

        Assert.False(sawSuppression);
    }
}
