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
        var group = app.MapGroup("/api/events/dead-letters");

        group.MapGet("/", async (
            HttpContext context,
            string? handler,
            int? limit,
            IDeadLetterStore store,
            CancellationToken ct) =>
        {
            if (!IsLocalOperator(context.Connection.RemoteIpAddress))
                return ApiResults.Fail("Dead-letter operations require a loopback caller", 403, "local_operator_required");

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
            if (!IsLocalOperator(context.Connection.RemoteIpAddress))
                return ApiResults.Fail("Dead-letter operations require a loopback caller", 403, "local_operator_required");

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

    internal static bool IsLocalOperator(IPAddress? remoteAddress) =>
        remoteAddress is null || IPAddress.IsLoopback(remoteAddress);
}
