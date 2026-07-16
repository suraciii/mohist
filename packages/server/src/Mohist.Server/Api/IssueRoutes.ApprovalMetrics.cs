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
            TimeProvider timeProvider,
            string? range,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);

            if (!TryParseRangeParameter(range, out var windowDays, out var rangeError))
                return rangeError;

            var result = await metricsQuery.GetApprovalWaitAsync(
                project.Id,
                timeProvider.GetUtcNow(),
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
