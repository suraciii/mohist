using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueApprovalMetrics(this RouteGroupBuilder group)
    {
        // The literal `metrics` segment precedes `{number:int}` so the
        // int route constraint cannot collide with this endpoint.
        group.MapGet("/metrics/approval-wait", async (
            HttpContext ctx,
            string projectRef,
            IssueMetricsQuerier metricsQuery,
            string? range,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);

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

            var result = await metricsQuery.GetApprovalWaitAsync(
                project.Id,
                DateTimeOffset.UtcNow,
                windowDays);

            return ApiResults.Ok(BuildResponse(result));
        });
    }

    private static ApprovalWaitMetricsResponse BuildResponse(IssueMetricsQuerier.ApprovalWaitResult result) =>
        new(
            Window: new ApprovalWaitMetricsWindowDto(
                From: result.Window.From.ToString("o"),
                To: result.Window.To.ToString("o")),
            SampleCount: result.SampleCount,
            AverageSeconds: result.AverageSeconds,
            MedianSeconds: result.MedianSeconds,
            MaxSeconds: result.MaxSeconds);
}
