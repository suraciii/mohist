using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueQualityMetrics(this RouteGroupBuilder group)
    {
        // The literal `metrics` segment precedes `{number:int}` so the
        // int route constraint cannot collide with this endpoint.
        group.MapGet("/metrics/quality", async (
            HttpContext ctx,
            string projectRef,
            IssueMetricsQuerier metricsQuery,
            string? range,
            TimeProvider timeProvider,
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

            var result = await metricsQuery.GetQualityAsync(
                project.Id,
                timeProvider.GetUtcNow(),
                windowDays);

            return ApiResults.Ok(BuildResponse(result));
        });
    }

    private static QualityMetricsResponse BuildResponse(IssueMetricsQuerier.QualityMetricsResult result) =>
        new(
            Window7d: BuildWindow(result.Window7d),
            Window30d: BuildWindow(result.Window30d),
            PreviousFirstTimeRightRate: result.PreviousWindow.FirstTimeRightRate,
            PreviousSampleCount: result.PreviousWindow.SampleCount,
            Trend: BuildTrend(result.Trend));

    private static QualityMetricsWindowDto BuildWindow(IssueMetricsQuerier.QualityMetricsWindow window) =>
        new(
            From: window.From.ToString("o"),
            To: window.To.ToString("o"),
            SampleCount: window.SampleCount,
            FirstTimeRightRate: window.FirstTimeRightRate,
            Stages: window.Stages
                .Select(s => new StageReworkRateDto(s.Stage, s.EnteredCount, s.ReworkRate))
                .ToArray());

    private static QualityTrendDto BuildTrend(IssueMetricsQuerier.QualityTrend trend) =>
        new(
            Bucket: trend.Bucket,
            From: trend.Window30dFrom.ToString("o"),
            To: trend.Window30dTo.ToString("o"),
            Points: trend.Points
                .Select(p => new QualityTrendPointDto(
                    Boundary: p.Boundary,
                    SampleCount: p.SampleCount,
                    FirstTimeRightRate: p.FirstTimeRightRate,
                    ReworkRate: p.ReworkRate))
                .ToArray());
}
