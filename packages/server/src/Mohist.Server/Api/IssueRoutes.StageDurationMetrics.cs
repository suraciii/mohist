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
            IssueQuerier issuesQuery,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);

            var result = await issuesQuery.GetStageDurationsAsync(
                project.Id,
                timeProvider.GetUtcNow());

            return ApiResults.Ok(BuildResponse(result));
        });
    }

    private static StageDurationMetricsResponse BuildResponse(IssueQuerier.StageDurationResult result) =>
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
