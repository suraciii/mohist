using System.Net;
using Mohist.Cli.Tests.Support;
using Xunit;

namespace Mohist.Cli.Tests;

public class ServiceReadinessProbeSpecs
{
    private static ServiceReadinessProbe BuildProbe(
        HttpClient http,
        TextWriter? output = null)
        => new(http, output ?? TextWriter.Null);

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
        var http = BuildHttp(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var probe = BuildProbe(http);

        var start = DateTimeOffset.UtcNow;
        var result = await probe.WaitForServerReadyAsync(
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.False(result.Ready);
        Assert.NotNull(result.LastFailure);
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(100),
            $"Expected polling to last at least ~timeout window; took {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task WaitForServerReadyAsync_BecomesReadyMidPoll_ReportsReady()
    {
        var pollCount = 0;
        var http = BuildHttp(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/health" && Interlocked.CompareExchange(ref pollCount, 1, 0) == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
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
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

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
