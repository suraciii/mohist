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
            IssueQuerier issuesQuery,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);

            var result = await issuesQuery.GetQualityAsync(
                project.Id,
                DateTimeOffset.UtcNow);

            return ApiResults.Ok(BuildResponse(result));
        });
    }

    private static QualityMetricsResponse BuildResponse(IssueQuerier.QualityMetricsResult result) =>
        new(
            Window7d: BuildWindow(result.Window7d),
            Window30d: BuildWindow(result.Window30d));

    private static QualityMetricsWindowDto BuildWindow(IssueQuerier.QualityMetricsWindow window) =>
        new(
            From: window.From.ToString("o"),
            To: window.To.ToString("o"),
            SampleCount: window.SampleCount,
            FirstTimeRightRate: window.FirstTimeRightRate,
            Stages: window.Stages
                .Select(s => new StageReworkRateDto(s.Stage, s.EnteredCount, s.ReworkRate))
                .ToArray());
}
