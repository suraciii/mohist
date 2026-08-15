using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Mohist.Server.Otel;

/// <summary>
/// Per-request guard that prevents the OTLP ingestion port from leaking
/// the main API surface. OTLP traffic must hit <c>/otel/v1/*</c>; any
/// other path on the OTLP host is answered with a bare 404 so callers
/// can't probe the main API endpoints through the side port.
/// </summary>
/// <remarks>
/// <para>The middleware is the belt-and-braces companion to
/// <c>RequireHost</c> on the OTLP route group: <c>RequireHost</c> keeps
/// OTLP routes from being reached through the main port; this middleware
/// keeps the OTLP port from being used to reach main port routes that
/// aren't constrained away.</para>
/// <para>The middleware reads the configured OTLP port from
/// <see cref="OtelOptions"/> (the same options the routes use), so
/// tests and overrides stay coherent without separate plumbing.</para>
/// </remarks>
public sealed class OtelPortIsolationMiddleware
{
    /// <summary>
    /// Segment used by <c>PathString.StartsWithSegments</c> to recognize
    /// OTLP-side paths. The trailing slash is intentionally omitted:
    /// <c>StartsWithSegments</c> with a trailing slash only matches the
    /// prefix itself, not child segments, so the comparison must use
    /// the bare segment to match <c>/otel/v1/anything</c>.
    /// </summary>
    public const string OtlpPathSegment = "/otel/v1";

    private readonly RequestDelegate _next;
    private readonly OtelOptions _options;

    public OtelPortIsolationMiddleware(
        RequestDelegate next,
        IOptions<OtelOptions> options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);
        _next = next;
        _options = options.Value;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.Request.Path;

        if (IsOtlpPortRequest(context))
        {
            // OTLP port: only /otel/v1/ paths are allowed; anything
            // else is a probe for the main API surface and must answer
            // 404 to keep the port surface tight.
            if (!IsOtlpPath(path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
        }
        else if (IsOtlpPath(path))
        {
            // Main API port: the /otel/v1/ tree belongs to the OTLP
            // port only. Without this check the SPA fallback would
            // happily serve index.html for /otel/v1/traces on the main
            // port, leaking that the server has an OTLP listener at all.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return _next(context);
    }

    private bool IsOtlpPortRequest(HttpContext context)
    {
        if (!_options.Enabled)
            return false;

        // TestServer has no socket, so its route tests provide a logical
        // local-port header. Port 0 is valid only in that explicit seam;
        // ordinary requests must never be classified from an OS port.
        if (context.Request.Headers.TryGetValue("X-Mohist-Test-Local-Port", out var requestedPort)
            && int.TryParse(requestedPort.ToString(), out var logicalPort))
            return logicalPort == _options.Port;

        if (_options.Port <= 0)
            return false;

        return ResolveLocalPort(context) == _options.Port;
    }

    private static int ResolveLocalPort(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Mohist-Test-Local-Port", out var value)
            && int.TryParse(value, out var localPort))
            return localPort;
        return context.Connection.LocalPort;
    }

    private static bool IsOtlpPath(PathString path)
    {
        if (!path.HasValue)
            return false;
        return path.StartsWithSegments(OtlpPathSegment, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Extension to wire <see cref="OtelPortIsolationMiddleware"/> into the
/// ASP.NET Core request pipeline.
/// </summary>
public static class OtelPortIsolationMiddlewareExtensions
{
    public static IApplicationBuilder UseOtelPortIsolation(this IApplicationBuilder app)
    {
        return app.UseMiddleware<OtelPortIsolationMiddleware>();
    }
}
