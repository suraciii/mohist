using Microsoft.AspNetCore.HttpLogging;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Otel;

/// <summary>
/// Suppresses HttpLogging middleware output for the OTLP self-ingestion
/// path (<c>/otel/...</c>). The server's own OTLP exporter posts traces
/// back to its own <c>/otel/v1/traces</c> endpoint every few seconds; that
/// is the OTel self-telemetry loop, not user traffic. Without this
/// interceptor the middleware would emit a request log for every export
/// poll, dominating the log volume.
/// </summary>
/// <remarks>
/// The trace ingestion capability itself is unaffected — only the
/// middleware-level request log lines are suppressed for this path.
/// Other request paths keep their default HttpLogging behavior. The
/// <c>/otel</c> prefix constant is shared with
/// <see cref="MohistOpenTelemetryRegistration.OtelIngestPathPrefix"/>
/// so the two stay in sync.
/// </remarks>
internal sealed class OtelHttpLogSuppressorInterceptor : IHttpLoggingInterceptor
{
    public ValueTask OnRequestAsync(HttpLoggingInterceptorContext logContext)
    {
        var path = logContext.HttpContext.Request.Path;
        if (path.StartsWithSegments(MohistOpenTelemetryRegistration.OtelIngestPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // None = skip all middleware logging fields for this request.
            logContext.LoggingFields = HttpLoggingFields.None;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnResponseAsync(HttpLoggingInterceptorContext logContext)
    {
        // Request-side decision already disabled all fields; nothing to do
        // on response. Kept as no-op to satisfy the interface contract.
        return ValueTask.CompletedTask;
    }
}
