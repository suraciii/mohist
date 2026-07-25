using Microsoft.AspNetCore.Builder;
using OpenTelemetry;

namespace Mohist.Server.Otel;

public sealed class OtelSuppressionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsOtelRequest(context.Request.Path))
        {
            await next(context);
            return;
        }

        using var suppression = SuppressInstrumentationScope.Begin();
        await next(context);
    }

    internal static bool IsOtelRequest(PathString path) =>
        path.StartsWithSegments("/otel/v1", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/otel/api", StringComparison.OrdinalIgnoreCase);
}

public static class OtelSuppressionMiddlewareExtensions
{
    public static IApplicationBuilder UseOtelSuppression(this IApplicationBuilder app) =>
        app.UseMiddleware<OtelSuppressionMiddleware>();
}
