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
    public async Task Agent_scope_survives_when_otel_is_off_without_publishing()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var runtime = new RuntimeObservability(false, time.GetUtcNow(), time);
        var measurements = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter == runtime.Meter)
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => measurements++);
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => measurements++);
        listener.Start();

        RequestWorkSnapshot? snapshot = null;
        var middleware = new RuntimeRequestMetricsMiddleware(_ =>
        {
            Assert.Equal("agent.status", RequestWorkScope.Current!.Snapshot().AgentPath);
            RequestWorkScope.Current.AddCandidates(3);
            RequestWorkScope.Current.AddProcessed(2);
            snapshot = RequestWorkScope.Current.Snapshot();
            return Task.CompletedTask;
        }, runtime, time);
        var context = AgentContext("/api/agent/status", "agent.status");

        await middleware.InvokeAsync(context);
        listener.RecordObservableInstruments();

        Assert.Equal(3, snapshot!.Value.Candidates);
        Assert.Equal(2, snapshot.Value.Processed);
        Assert.Equal(0, measurements);
        Assert.Empty(runtime.GetSnapshot().Routes);
    }

    [Fact]
    public async Task Enabled_agent_scope_publishes_response_local_path_counts()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var runtime = new RuntimeObservability(true, time.GetUtcNow(), time);
        var measurements = new Dictionary<string, long>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter == runtime.Meter)
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => measurements[instrument.Name] = value);
        listener.Start();

        var middleware = new RuntimeRequestMetricsMiddleware(_ =>
        {
            Assert.Equal("agent.activity", RequestWorkScope.Current!.Snapshot().AgentPath);
            RequestWorkScope.Current.AddCandidates(5);
            RequestWorkScope.Current.AddProcessed(4);
            RequestWorkScope.Current.AddTranscriptRecords(6);
            return Task.CompletedTask;
        }, runtime, time);

        await middleware.InvokeAsync(AgentContext("/api/agent/activity", "agent.activity"));

        Assert.Equal(5, measurements[RuntimeMetricCatalog.PathCandidates]);
        Assert.Equal(4, measurements[RuntimeMetricCatalog.PathProcessed]);
        Assert.Equal(6, measurements[RuntimeMetricCatalog.PathTranscriptRecords]);
    }

    [Theory]
    [InlineData("/api/agent/status/extra", "GET")]
    [InlineData("/unrelated/agent/activity", "GET")]
    [InlineData("/otel/api/status", "GET")]
    [InlineData("/api/agent/status", "POST")]
    public async Task Only_matched_get_agent_endpoints_create_off_state_scope(string path, string method)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var runtime = new RuntimeObservability(false, time.GetUtcNow(), time);
        RequestWorkScope? observed = null;
        var middleware = new RuntimeRequestMetricsMiddleware(_ =>
        {
            observed = RequestWorkScope.Current;
            return Task.CompletedTask;
        }, runtime, time);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        if (method == "POST")
            context.SetEndpoint(AgentEndpoint(path, "agent.status"));

        await middleware.InvokeAsync(context);

        Assert.Null(observed);
    }

    private static DefaultHttpContext AgentContext(string pattern, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = pattern.Replace("{projectRef}", "proj_test", StringComparison.Ordinal);
        context.SetEndpoint(AgentEndpoint(pattern, path));
        return context;
    }

    private static RouteEndpoint AgentEndpoint(string pattern, string path) => new(
        _ => Task.CompletedTask,
        RoutePatternFactory.Parse(pattern),
        0,
        new EndpointMetadataCollection(new AgentPathEndpointMetadata(path)),
        path);

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
