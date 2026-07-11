using System.Net;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Api;

public static class DeadLetterRoutes
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public static WebApplication MapDeadLetterRoutes(this WebApplication app)
    {
        if (!UsesLoopbackOnlyListener(app.Configuration))
            return app;

        var group = app.MapGroup("/api/events/dead-letters");

        group.MapGet("/", async (
            HttpContext context,
            string? handler,
            int? limit,
            IDeadLetterStore store,
            CancellationToken ct) =>
        {
            if (!IsDirectLoopbackRequest(context))
                return ApiResults.Fail("Dead-letter operations require a direct loopback caller", 403, "local_operator_required");

            var resolvedLimit = limit ?? DefaultLimit;
            if (resolvedLimit is < 1 or > MaxLimit)
                return ApiResults.BadRequest($"limit must be between 1 and {MaxLimit}");

            var rows = await store.QueryAsync(handler, resolvedLimit, ct);
            return ApiResults.Ok(rows.Select(row => new
            {
                id = row.DeadLetterId,
                origin = row.Origin,
                sourceId = row.Id,
                row.Source,
                row.EventId,
                row.Type,
                row.Time,
                row.Subject,
                row.DataContentType,
                row.Data,
                extensions = row.ExtensionsJson,
                handler = row.FailingHandler,
                error = row.ErrorMessage,
                attempts = row.AttemptCount,
                row.DeadLetteredAt,
                status = row.Status.ToString(),
                row.RedeliveryAttemptedAt,
            }));
        });

        group.MapPost("/{deadLetterId:long}/redeliver", async (
            HttpContext context,
            long deadLetterId,
            IGrainFactory grains,
            CancellationToken ct) =>
        {
            if (!IsDirectLoopbackRequest(context))
                return ApiResults.Fail("Dead-letter operations require a direct loopback caller", 403, "local_operator_required");

            var result = await grains
                .GetGrain<IDispatcherGrain>(DispatcherGrain.FixedKey)
                .RedeliverAsync(deadLetterId, ct);

            if (!result.Found)
                return ApiResults.NotFound($"Dead-letter {deadLetterId} not found");

            var response = new
            {
                id = deadLetterId,
                delivered = result.Delivered,
                attempts = result.Attempts,
                error = result.Error,
            };
            return result.Delivered
                ? ApiResults.Ok(response)
                : ApiResults.Conflict(
                    result.Error ?? "Dead-letter re-delivery failed",
                    "dead_letter_redelivery_failed",
                    response);
        });

        return app;
    }

    internal static bool IsDirectLoopbackRequest(HttpContext context)
    {
        if (HasProxyMarker(context.Request.Headers))
            return false;
        if (!IsLoopbackHost(context.Request.Host.Host))
            return false;
        if (!IsLoopbackAddress(context.Connection.RemoteIpAddress))
            return false;
        return IsLoopbackAddress(context.Connection.LocalIpAddress);
    }

    internal static bool UsesLoopbackOnlyListener(IConfiguration configuration)
    {
        var configuredUrls = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
        if (!string.IsNullOrWhiteSpace(configuredUrls))
        {
            var urls = configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return urls.Length > 0 && urls.All(url =>
                Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && !IsPublicListenerHost(uri.Host));
        }

        return !IsPublicListenerHost(configuration["Mohist:Host"] ?? "localhost");
    }

    internal static bool IsLoopbackAddress(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address);

    private static bool HasProxyMarker(IHeaderDictionary headers) =>
        headers.ContainsKey("Forwarded")
        || headers.ContainsKey("X-Forwarded-For")
        || headers.ContainsKey("X-Forwarded-Host")
        || headers.ContainsKey("X-Forwarded-Proto")
        || headers.ContainsKey("X-Forwarded-Prefix")
        || headers.ContainsKey("X-Original-For")
        || headers.ContainsKey("X-Real-IP")
        || headers.ContainsKey("Via");

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static bool IsPublicListenerHost(string host) =>
        host is "*" or "0.0.0.0" or "::" or "[::]";
}
