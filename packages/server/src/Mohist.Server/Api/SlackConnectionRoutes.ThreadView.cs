using Mohist.Server.Slack.Services;

namespace Mohist.Server.Api;

public static partial class SlackConnectionRoutes
{
    /// <summary>
    /// Reads one page of the channel thread a Session is bound to. The route
    /// sits next to its Connection management siblings but selects by Session:
    /// the Session's recorded provenance, not the caller, decides which
    /// Connection, channel, and thread are read.
    /// </summary>
    private static void MapThreadViewRoute(RouteGroupBuilder management) =>
        management.MapGet("/thread", async (
            HttpContext context,
            string? sessionId,
            int? limit,
            string? continuation,
            SlackThreadReadService reads,
            CancellationToken ct) =>
        {
            var result = await reads.ReadAsync(
                new SlackThreadReadRequest(
                    context.GetResolvedProject().Id,
                    sessionId ?? string.Empty,
                    limit ?? SlackThreadReadService.DefaultLimit,
                    continuation),
                ct);
            return result.View is { } view
                ? ApiResults.Ok(view)
                : ThreadReadFailure(result.Error!);
        });

    private static IResult ThreadReadFailure(SlackThreadReadError error)
    {
        var details = error.RetryAfter is null && error.ProviderError is null
            ? null
            : new
            {
                retryAfterSeconds = error.RetryAfter is { } delay
                    ? (int)Math.Ceiling(delay.TotalSeconds)
                    : (int?)null,
                providerError = error.ProviderError,
            };

        return error.Code switch
        {
            "invalid_request" or "continuation_invalid" or "cursor_rejected" =>
                ApiResults.BadRequest(error.Message, error.Code, details),
            "session_not_found" => ApiResults.Fail(error.Message, 404, error.Code, details),
            "rate_limited" => ApiResults.Fail(error.Message, 429, error.Code, details),
            "provider_rejected" or "provider_unavailable" or "invalid_provider_response" =>
                ApiResults.Fail(error.Message, 502, error.Code, details),
            _ => ApiResults.Conflict(error.Message, error.Code, details),
        };
    }
}
