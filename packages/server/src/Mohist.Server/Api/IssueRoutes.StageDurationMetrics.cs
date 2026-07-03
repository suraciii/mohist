using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueStageDurationMetrics(this RouteGroupBuilder group)
    {
        // The literal `metrics` segment precedes `{number:int}` so the
        // int route constraint cannot collide with this endpoint.
        group.MapGet("/metrics/stage-duration", async (
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

            var result = await metricsQuery.GetStageDurationsAsync(
                project.Id,
                timeProvider.GetUtcNow(),
                windowDays);

            return ApiResults.Ok(BuildResponse(result));
        });
    }

    private static StageDurationMetricsResponse BuildResponse(IssueMetricsQuerier.StageDurationResult result) =>
        new(
            Window: new StageDurationMetricsWindowDto(
                From: result.Window.From.ToString("o"),
                To: result.Window.To.ToString("o")),
            Stages: result.Stages
                .Select(s => new StageDurationStageDto(
                    Stage: s.Stage,
                    SampleCount: s.SampleCount,
                    AverageSeconds: s.AverageSeconds,
                    MedianSeconds: s.MedianSeconds))
                .ToArray(),
            FlowEfficiencyRatio: result.FlowEfficiencyRatio,
            WaitBreakout: new StageDurationWaitBreakoutDto(
                AverageApprovalGateWaitSeconds: result.WaitBreakout.AverageApprovalGateWaitSeconds,
                AverageInactiveGapSeconds: result.WaitBreakout.AverageInactiveGapSeconds));
}
