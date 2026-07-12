using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Security;

namespace Mohist.Server.Api;

public static class DeadLetterRoutes
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public static WebApplication MapDeadLetterRoutes(this WebApplication app)
    {
        if (!UsesLoopbackOnlyListener(app.Configuration))
            return app;

        var credential = app.Services.GetRequiredService<OperatorCredential>();
        var group = app.MapGroup("/api/events/dead-letters");

        group.MapGet("/", async (
            HttpContext context,
            string? handler,
            int? limit,
            IDeadLetterStore store,
            CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!credential.Authorizes(context.Request.Headers))
                return ApiResults.Fail("Dead-letter operations require an operator credential", 403, "operator_credential_required");

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
                error = OperatorDiagnostic.Summarize(row.ErrorMessage),
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
            if (!credential.Authorizes(context.Request.Headers))
                return ApiResults.Fail("Dead-letter operations require an operator credential", 403, "operator_credential_required");

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
                error = OperatorDiagnostic.Summarize(result.Error),
            };
            return result.Delivered
                ? ApiResults.Ok(response)
                : ApiResults.Conflict(
                    OperatorDiagnostic.Summarize(result.Error) ?? "Dead-letter re-delivery failed",
                    "dead_letter_redelivery_failed",
                    response);
        });

        return app;
    }

    internal static bool UsesLoopbackOnlyListener(IConfiguration configuration)
    {
        var configuredUrls = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
        if (!string.IsNullOrWhiteSpace(configuredUrls))
        {
            var urls = configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return urls.Length > 0 && urls.All(url =>
                Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && IsLoopbackHost(uri.Host));
        }

        return IsLoopbackHost(configuration["Mohist:Host"] ?? "localhost");
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address));
}
