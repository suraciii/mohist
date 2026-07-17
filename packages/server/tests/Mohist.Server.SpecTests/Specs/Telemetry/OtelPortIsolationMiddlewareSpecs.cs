using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mohist.Server.Otel;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Telemetry;

public class OtelPortIsolationMiddlewareSpecs
{
    private const int OtlpPort = 14318;

    [Fact]
    public async Task OtlpPort_NonOtlpPath_Returns404()
    {
        var middleware = CreateMiddleware(otlpPort: OtlpPort, isEnabled: true);

        var context = CreateContext(host: "localhost:3456", localPort: OtlpPort, path: "/otel/api/traces");
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task OtlpPort_OtlpPath_PassesThrough()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            otlpPort: OtlpPort,
            isEnabled: true,
            next: _ => { nextCalled = true; return Task.CompletedTask; });

        var context = CreateContext(host: "localhost:3456", localPort: OtlpPort, path: "/otel/v1/traces");
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OtlpPort_RootPath_Returns404()
    {
        var middleware = CreateMiddleware(otlpPort: OtlpPort, isEnabled: true);

        var context = CreateContext(host: "localhost:3456", localPort: OtlpPort, path: "/");
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task MainApiHost_OtlpPath_Returns404()
    {
        // The /otel/v1/ tree belongs to the OTLP port only; the main
        // API port must answer 404 for it so the SPA fallback doesn't
        // accidentally serve index.html for /otel/v1/traces.
        var middleware = CreateMiddleware(otlpPort: OtlpPort, isEnabled: true);

        var context = CreateContext(host: $"localhost:{OtlpPort}", localPort: 3456, path: "/otel/v1/traces");
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task MainApiHost_AnyPath_PassesThrough()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            otlpPort: OtlpPort,
            isEnabled: true,
            next: _ => { nextCalled = true; return Task.CompletedTask; });

        var context = CreateContext(host: $"localhost:{OtlpPort}", localPort: 3456, path: "/otel/api/traces");
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.NotEqual(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task OtlpDisabled_PassesThroughEverything()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            otlpPort: OtlpPort,
            isEnabled: false,
            next: _ => { nextCalled = true; return Task.CompletedTask; });

        var context = CreateContext(host: $"localhost:{OtlpPort}", localPort: OtlpPort, path: "/otel/api/traces");
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OtlpPort_OtlpPrefixOnlyPath_PassesThrough()
    {
        // /otel/v1 (without trailing slash + resource) is still under the
        // OTLP path prefix; should pass through to the next middleware.
        var nextCalled = false;
        var middleware = CreateMiddleware(
            otlpPort: OtlpPort,
            isEnabled: true,
            next: _ => { nextCalled = true; return Task.CompletedTask; });

        var context = CreateContext(host: "localhost:3456", localPort: OtlpPort, path: "/otel/v1");
        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    private static OtelPortIsolationMiddleware CreateMiddleware(
        int otlpPort,
        bool isEnabled,
        RequestDelegate? next = null)
    {
        next ??= _ => Task.CompletedTask;
        var options = new OtelOptions { Port = otlpPort, Enabled = isEnabled };
        return new OtelPortIsolationMiddleware(next, Options.Create(options));
    }

    private static HttpContext CreateContext(string host, int localPort, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Connection.LocalPort = localPort;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
