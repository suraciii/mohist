using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Otel;
using Xunit;

namespace Mohist.Server.UnitTests.Telemetry;

public sealed class RequestWorkScopeTests
{
    [Fact]
    public void Snapshot_is_non_terminal_and_close_is_linearizable()
    {
        var scope = new RequestWorkScope();
        scope.AddDatabaseCalls(2);
        scope.AddDownstreamCalls(3);
        scope.SetAgentPath("agent.activity");
        scope.AddCandidates(4);

        var beforeClose = scope.Snapshot();
        var closed = scope.CloseAndSnapshot();
        scope.AddDatabaseCalls(20);
        scope.AddCandidates(20);

        Assert.Equal(2, beforeClose.DatabaseCalls);
        Assert.Equal(4, beforeClose.Candidates);
        Assert.Equal(closed, scope.Snapshot());
        Assert.Equal(2, closed.DatabaseCalls);
        Assert.Equal(4, closed.Candidates);
        Assert.Equal("agent.activity", closed.AgentPath);
    }

    [Fact]
    public async Task Middleware_publishes_fake_duration_and_clears_ambient_scope()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var runtime = new RuntimeObservability(true, time.GetUtcNow(), time);
        var measurements = new List<(string Name, double Value, object? Route)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == runtime.Meter)
                listener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            measurements.Add((instrument.Name, value, tags.ToArray().FirstOrDefault(t => t.Key == "http.route").Value));
        });
        listener.Start();

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/items/{id}"),
            0,
            EndpointMetadataCollection.Empty,
            "items"));
        var middleware = new RuntimeRequestMetricsMiddleware(
            _ =>
            {
                RequestWorkScope.Current!.AddDatabaseCalls(2);
                RequestWorkScope.Current.AddDownstreamCalls();
                time.Advance(TimeSpan.FromMilliseconds(125));
                return Task.CompletedTask;
            },
            runtime,
            time);

        await middleware.InvokeAsync(context);

        Assert.Null(RequestWorkScope.Current);
        Assert.Contains(measurements, item => item.Name == RuntimeMetricCatalog.HttpRequestDuration && item.Value == 125);
    }

    [Fact]
    public async Task Middleware_rethrows_original_exception_and_normalizes_status()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var runtime = new RuntimeObservability(true, time.GetUtcNow(), time);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        var error = new InvalidOperationException("original");
        var middleware = new RuntimeRequestMetricsMiddleware(
            _ =>
            {
                time.Advance(TimeSpan.FromMilliseconds(7));
                return Task.FromException(error);
            },
            runtime,
            time);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.Same(error, thrown);
        Assert.Null(RequestWorkScope.Current);
    }

    [Fact]
    public async Task Counting_handler_counts_each_physical_send()
    {
        var scope = new RequestWorkScope();
        using var ambient = RequestWorkScope.Push(scope);
        var sends = 0;
        using var handler = new RequestWorkCountingHandler
        {
            InnerHandler = new DelegateHandler(_ =>
            {
                sends++;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            }),
        };
        using var client = new HttpClient(handler);

        await client.GetAsync("http://example.test/one");
        await client.GetAsync("http://example.test/two");

        Assert.Equal(2, sends);
        Assert.Equal(2, scope.Snapshot().DownstreamCalls);
    }

    [Fact]
    public async Task Agent_scope_survives_when_otel_is_off_but_otel_endpoint_does_not_create_one()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var runtime = new RuntimeObservability(false, time.GetUtcNow(), time);
        RequestWorkSnapshot? agentSnapshot = null;
        RequestWorkScope? otelScope = null;
        var agent = new RuntimeRequestMetricsMiddleware(_ =>
        {
            RequestWorkScope.Current!.AddCandidates(3);
            agentSnapshot = RequestWorkScope.Current.Snapshot();
            return Task.CompletedTask;
        }, runtime, time);
        var agentContext = new DefaultHttpContext();
        agentContext.Request.Path = "/api/agent/status";
        await agent.InvokeAsync(agentContext);

        var otel = new RuntimeRequestMetricsMiddleware(_ =>
        {
            otelScope = RequestWorkScope.Current;
            return Task.CompletedTask;
        }, runtime, time);
        var otelContext = new DefaultHttpContext();
        otelContext.Request.Path = "/otel/api/status";
        await otel.InvokeAsync(otelContext);

        Assert.Equal(3, agentSnapshot!.Value.Candidates);
        Assert.Null(otelScope);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
