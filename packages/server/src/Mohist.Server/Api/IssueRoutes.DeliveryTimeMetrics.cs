using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueDeliveryTimeMetrics(this RouteGroupBuilder group)
    {
        // The literal `metrics` segment precedes `{number:int}` so the
        // int route constraint cannot collide with this endpoint.
        group.MapGet("/metrics/delivery-time", async (
            HttpContext ctx,
            string projectRef,
            IssueMetricsQuerier metricsQuery,
            string? range,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);

            if (!TryParseRangeParameter(range, out var windowDays, out var rangeError))
                return rangeError;

            var result = await metricsQuery.GetDeliveryTimesAsync(
                project.Id,
                timeProvider.GetUtcNow(),
                windowDays);

            return ApiResults.Ok(BuildResponse(result));
        });
    }

    private static DeliveryTimeMetricsResponse BuildResponse(IssueMetricsQuerier.DeliveryTimeResult result) =>
        new(
            Points: result.Points
                .Select(p => new DeliveryTimePointDto(
                    IssueNumber: p.IssueNumber,
                    CompletedAt: p.CompletedAt.ToString("o"),
                    LeadDays: p.LeadDays,
                    CycleDays: p.CycleDays))
                .ToArray(),
            PreviousCycleDays: result.PreviousAverageCycleDays);
}
