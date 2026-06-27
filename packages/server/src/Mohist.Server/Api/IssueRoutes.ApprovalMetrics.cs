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
            IssueQuerier issuesQuery,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);

            var result = await issuesQuery.GetApprovalWaitAsync(
                project.Id,
                DateTimeOffset.UtcNow);

            return ApiResults.Ok(BuildResponse(result));
        });
    }

    private static ApprovalWaitMetricsResponse BuildResponse(IssueQuerier.ApprovalWaitResult result) =>
        new(
            Window: new ApprovalWaitMetricsWindowDto(
                From: result.Window.From.ToString("o"),
                To: result.Window.To.ToString("o")),
            SampleCount: result.SampleCount,
            AverageSeconds: result.AverageSeconds,
            MedianSeconds: result.MedianSeconds,
            MaxSeconds: result.MaxSeconds);
}
