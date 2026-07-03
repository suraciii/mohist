using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueCumulativeFlow(this RouteGroupBuilder group)
    {
        // The literal `metrics` segment precedes `{number:int}` so the
        // int route constraint cannot collide with this endpoint.
        group.MapGet("/metrics/cumulative-flow", async (
            HttpContext ctx,
            string projectRef,
            string? range,
            CumulativeFlowQuerier cumulativeFlow,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);

            // Uniform range vocabulary shared by the six issue-metrics
            // endpoints. Omitting `range` falls back to today's fixed
            // 90-day window (the prior D6 contract); unknown values
            // return 400.
            int? windowDays = null;
            if (!string.IsNullOrWhiteSpace(range))
            {
                if (!MetricsRange.TryParse(range, out var days))
                    return ApiResults.BadRequest(
                        "Unsupported range value. Accepted values: '7d', '30d', '90d'.",
                        "unsupported_range",
                        new { range });
                windowDays = days;
            }

            var result = await cumulativeFlow.GetAsync(
                project.Id,
                timeProvider.GetUtcNow(),
                windowDays,
                ct);

            return ApiResults.Ok(BuildResponse(result));
        });
    }

    private static CumulativeFlowResponse BuildResponse(CumulativeFlowQuerier.CumulativeFlowResult result) =>
        new(
            Snapshots: result.Snapshots
                .Select(s => new CumulativeFlowDayDto(
                    Day: s.Day,
                    Backlog: s.Backlog,
                    Plan: s.Plan,
                    Build: s.Build,
                    Check: s.Check,
                    Integrate: s.Integrate,
                    Done: s.Done))
                .ToArray(),
            RangeFrom: result.RangeFrom,
            RangeTo: result.RangeTo);
}