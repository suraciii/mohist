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
            IssueQuerier issuesQuery,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);

            var result = await issuesQuery.GetDeliveryTimesAsync(
                project.Id,
                timeProvider.GetUtcNow());

            return ApiResults.Ok(BuildResponse(result));
        });
    }

    private static DeliveryTimeMetricsResponse BuildResponse(IssueQuerier.DeliveryTimeResult result) =>
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
