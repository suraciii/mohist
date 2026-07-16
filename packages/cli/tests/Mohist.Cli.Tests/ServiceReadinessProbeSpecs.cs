using System.Net;
using Microsoft.Extensions.Time.Testing;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class ServiceReadinessProbeSpecs
{
    private static ServiceReadinessProbe BuildProbe(
        HttpClient http,
        TextWriter? output = null,
        TimeProvider? timeProvider = null)
        => new(http, output ?? TextWriter.Null, timeProvider);

    private static HttpClient BuildHttp(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new RecordingHttpHandler((req, ct) => Task.FromResult(responder(req)));
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };
    }

    private static HttpResponseMessage HealthOk() =>
        new(HttpStatusCode.OK) { Content = new StringContent("ok") };

    private static HttpResponseMessage IndexHtml(string html) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
        };

    private static HttpResponseMessage AssetOk() =>
        new(HttpStatusCode.OK) { Content = new StringContent("// bundle") };

    [Fact]
    public async Task WaitForServerReadyAsync_AllChecksPass_ReportsReady()
    {
        var http = BuildHttp(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/health" => HealthOk(),
                "/" => IndexHtml("<html><head><script src=\"/assets/index-abc.js\"></script></head></html>"),
                _ when path.StartsWith("/assets/") => AssetOk(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        var probe = BuildProbe(http);

        var result = await probe.WaitForServerReadyAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Null(result.LastFailure);
    }

    [Fact]
    public async Task WaitForServerReadyAsync_HealthDown_ReportsNotReady()
    {
        var http = BuildHttp(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var probe = BuildProbe(http);

        var result = await probe.WaitForServerReadyAsync(
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.NotNull(result.LastFailure);
        Assert.Contains("/api/health", result.LastFailure);
    }

    [Fact]
    public async Task WaitForServerReadyAsync_TimeoutWithNeverReady_ReportsNotReady()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var handler = new RecordingHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var probe = BuildProbe(http, timeProvider: time);

        var wait = probe.WaitForServerReadyAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        await handler.WaitForRequestCountAsync(1);
        Assert.False(wait.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(2));
        var result = await wait;

        Assert.False(result.Ready);
        Assert.NotNull(result.LastFailure);
    }

    [Fact]
    public async Task WaitForServerReadyAsync_TimeoutBeforeFirstProbeCompletes_CapturesFinalFailure()
    {
        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var handler = new RecordingHttpHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                firstRequestStarted.SetResult();
                await releaseFirstRequest.Task.WaitAsync(ct);
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var probe = BuildProbe(http, timeProvider: time);

        var wait = probe.WaitForServerReadyAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);
        await firstRequestStarted.Task;

        time.Advance(TimeSpan.FromMilliseconds(1));
        var result = await wait;

        Assert.False(result.Ready);
        Assert.Equal("GET /api/health returned 500 InternalServerError", result.LastFailure);
    }

    [Fact]
    public async Task WaitForServerReadyAsync_BecomesReadyMidPoll_ReportsReady()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var pollCount = 0;
        var handler = new RecordingHttpHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var response = path == "/api/health" && Interlocked.CompareExchange(ref pollCount, 1, 0) == 0
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : path switch
                {
                    "/api/health" => HealthOk(),
                    "/" => IndexHtml("<html><head><script src=\"/assets/index-abc.js\"></script></head></html>"),
                    _ when path.StartsWith("/assets/") => AssetOk(),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            return Task.FromResult(response);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:0") };

        var probe = BuildProbe(http, timeProvider: time);

        var wait = probe.WaitForServerReadyAsync(
            TimeSpan.FromSeconds(3),
            CancellationToken.None);
        await handler.WaitForRequestCountAsync(1);
        Assert.False(wait.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(500));
        var result = await wait;

        Assert.True(result.Ready, $"LastFailure: {result.LastFailure}");
        Assert.Null(result.LastFailure);
    }

    [Fact]
    public async Task CheckServerReadyOnceAsync_AllChecksPass_ReturnsNull()
    {
        var http = BuildHttp(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/health" => HealthOk(),
                "/" => IndexHtml("<html><body><link href=\"/assets/style.css\"/></body></html>"),
                _ when path.StartsWith("/assets/") => AssetOk(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        var probe = BuildProbe(http);

        var failure = await probe.CheckServerReadyOnceAsync(CancellationToken.None);

        Assert.Null(failure);
    }

    [Fact]
    public async Task CheckServerReadyOnceAsync_HealthDown_ReturnsFailure()
    {
        var http = BuildHttp(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var probe = BuildProbe(http);

        var failure = await probe.CheckServerReadyOnceAsync(CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Contains("/api/health", failure);
    }

    [Fact]
    public async Task CheckServerReadyOnceWithReasonAsync_AssetNotReady_SetsReason()
    {
        var http = BuildHttp(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            return path switch
            {
                "/api/health" => HealthOk(),
                "/" => IndexHtml("<html><head><script src=\"/assets/missing.js\"></script></head></html>"),
                _ when path.StartsWith("/assets/") => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        var probe = BuildProbe(http);
        var state = new ReadinessProbeState();

        var failure = await probe.CheckServerReadyOnceWithReasonAsync(CancellationToken.None, state);

        Assert.NotNull(failure);
        Assert.Equal("waiting for Web assets", state.Reason);
    }

    [Fact]
    public async Task CheckServerReadyOnceWithReasonAsync_HealthDown_SetsReason()
    {
        var http = BuildHttp(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var probe = BuildProbe(http);
        var state = new ReadinessProbeState();

        var failure = await probe.CheckServerReadyOnceWithReasonAsync(CancellationToken.None, state);

        Assert.NotNull(failure);
        Assert.Equal("waiting for Mohist API", state.Reason);
    }
}
