using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueMetrics(this RouteGroupBuilder group)
    {
        // The literal `metrics` segment precedes `{number:int}` so the
        // int route constraint cannot collide with this endpoint.
        group.MapGet("/metrics/completion", async (
            HttpContext ctx,
            string projectRef,
            string? bucket,
            IssueQuerier issuesQuery,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);

            // v1 contract: only fixed `day` / `week` bucketing is
            // honored. Custom bucket size or time range is rejected.
            if (string.IsNullOrWhiteSpace(bucket)
                || string.Equals(bucket, "day", StringComparison.OrdinalIgnoreCase))
            {
                var result = await issuesQuery.GetCompletionBucketsAsync(
                    project.Id,
                    IssueQuerier.CompletionBucket.Day,
                    timeProvider.GetUtcNow());
                return ApiResults.Ok(BuildResponse(result));
            }

            if (string.Equals(bucket, "week", StringComparison.OrdinalIgnoreCase))
            {
                var result = await issuesQuery.GetCompletionBucketsAsync(
                    project.Id,
                    IssueQuerier.CompletionBucket.Week,
                    timeProvider.GetUtcNow());
                return ApiResults.Ok(BuildResponse(result));
            }

            return ApiResults.BadRequest(
                "Unsupported bucket value. v1 only supports 'day' or 'week'.",
                "unsupported_bucket",
                new { bucket });
        });
    }

    private static CompletionMetricsResponse BuildResponse(IssueQuerier.CompletionBucketsResult result) =>
        new(
            Bucket: result.Bucket,
            Window: new CompletionMetricsWindowDto(
                From: result.WindowFrom.ToString("o"),
                To: result.WindowTo.ToString("o")),
            Buckets: result.Buckets
                .Select(b => new CompletionMetricsBucketDto(b.Boundary, b.Completed, b.Failed))
                .ToArray(),
            CurrentTotal: new CompletionMetricsTotalsDto(
                Completed: result.CurrentTotal.Completed,
                Failed: result.CurrentTotal.Failed,
                SampleCount: result.CurrentTotal.SampleCount),
            PreviousTotal: new CompletionMetricsTotalsDto(
                Completed: result.PreviousTotal.Completed,
                Failed: result.PreviousTotal.Failed,
                SampleCount: result.PreviousTotal.SampleCount));
}
