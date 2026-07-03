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
            IssueMetricsQuerier metricsQuery,
            string? range,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var project = GetRequiredProject(ctx);

            if (!TryParseRangeParameter(range, out var windowDays, out var rangeError))
                return rangeError;

            if (string.IsNullOrWhiteSpace(bucket)
                || string.Equals(bucket, "day", StringComparison.OrdinalIgnoreCase))
            {
                var result = await metricsQuery.GetCompletionBucketsAsync(
                    project.Id,
                    IssueMetricsQuerier.CompletionBucket.Day,
                    timeProvider.GetUtcNow(),
                    windowDays);
                return ApiResults.Ok(BuildResponse(result));
            }

            if (string.Equals(bucket, "week", StringComparison.OrdinalIgnoreCase))
            {
                var result = await metricsQuery.GetCompletionBucketsAsync(
                    project.Id,
                    IssueMetricsQuerier.CompletionBucket.Week,
                    timeProvider.GetUtcNow(),
                    windowDays);
                return ApiResults.Ok(BuildResponse(result));
            }

            return ApiResults.BadRequest(
                "Unsupported bucket value. v1 only supports 'day' or 'week'.",
                "unsupported_bucket",
                new { bucket });
        });
    }

    private static CompletionMetricsResponse BuildResponse(IssueMetricsQuerier.CompletionBucketsResult result) =>
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
