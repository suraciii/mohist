using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.PublicApi;

namespace Mohist.Server.Api.DirectApi;

/// <summary>
/// <see cref="IResult"/> factories for the <c>/api/v1</c> error envelope.
/// Direct API endpoints answer every error — transport, authorization,
/// validation, conflict, and lag — through this class so the surface's
/// envelope stays one shape: <c>{ "error": { "code", "message" } }</c>.
/// </summary>
public static class DirectApiResults
{
    /// <summary>
    /// The retry hint carried on every 503 projection_lag answer, in
    /// delta seconds. The projector's timer sweep bounds the worst
    /// case, so a short hint lets a caller retry promptly and simply
    /// receive another 503 while the projection catches up.
    /// </summary>
    public const string ProjectionLagRetryAfterSeconds = "1";

    public static IResult Error(int statusCode, string code, string message) =>
        Results.Json(
            new DirectApiErrorEnvelope(new DirectApiError(code, message)),
            statusCode: statusCode);

    public static IResult Unauthenticated() =>
        Results.Json(
            new DirectApiErrorEnvelope(DirectApiError.Unauthenticated()),
            statusCode: StatusCodes.Status401Unauthorized);

    public static IResult Forbidden() =>
        Results.Json(
            new DirectApiErrorEnvelope(DirectApiError.Forbidden()),
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// The 404 answer for a canonical resource that is absent from or
    /// does not belong to the authorized Project: the route's resource
    /// code with its fixed safe public message.
    /// </summary>
    public static IResult ResourceNotFound(string resourceNotFoundCode) =>
        Error(
            StatusCodes.Status404NotFound,
            resourceNotFoundCode,
            ResourceNotFoundMessage(resourceNotFoundCode));

    /// <summary>
    /// The projection freshness gate: the required source watermark is
    /// ahead of the stored projection checkpoint, so no snapshot is
    /// served as current state. The answer carries the error envelope
    /// and a Retry-After hint; it has no execution body and no effect.
    /// </summary>
    public static IResult ProjectionLag() => new ProjectionLagResult();

    /// <summary>
    /// The stop lifecycle has not confirmed its fenced outcome yet. The
    /// mapping remains pending, so the caller must retry the same key rather
    /// than treating the current projection as a completed command.
    /// </summary>
    public static IResult StopPending() => new StopPendingResult();

    public static IResult CursorInvalid() =>
        Error(
            StatusCodes.Status400BadRequest,
            DirectApiErrorCodes.CursorInvalid,
            "The event cursor is invalid or is not bound to this request.");

    public static IResult CursorExpired(long? earliestSequence, long? latestSequence) =>
        Results.Json(
            new DirectApiCursorExpiredEnvelope(
                new DirectApiError(
                    DirectApiErrorCodes.CursorExpired,
                    "The event cursor is older than the retained public stream."),
                earliestSequence,
                latestSequence),
            statusCode: StatusCodes.Status410Gone,
            options: JSON.PublicApi);

    /// <summary>
    /// The stop lifecycle has not confirmed its fenced outcome yet. The
    /// mapping remains pending, so the caller must retry the same key rather
    /// than treating the current projection as a completed command.
    /// </summary>
    public static IResult StopPending() => new StopPendingResult();

    public static IResult CursorInvalid() =>
        Error(
            StatusCodes.Status400BadRequest,
            DirectApiErrorCodes.CursorInvalid,
            "The event cursor is invalid or is not bound to this request.");

    public static IResult CursorExpired(long? earliestSequence, long? latestSequence) =>
        Results.Json(
            new DirectApiCursorExpiredEnvelope(
                new DirectApiError(
                    DirectApiErrorCodes.CursorExpired,
                    "The event cursor is older than the retained public stream."),
                earliestSequence,
                latestSequence),
            statusCode: StatusCodes.Status410Gone,
            options: JSON.PublicApi);

    /// <summary>
    /// The 503 projection-lag answer as a concrete result so the
    /// Retry-After hint rides on the same response as the error
    /// envelope without any secondary serialization path.
    /// </summary>
    private sealed class ProjectionLagResult : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            httpContext.Response.Headers.RetryAfter = ProjectionLagRetryAfterSeconds;
            httpContext.Response.ContentType = "application/json";
            await httpContext.Response.WriteAsync(
                JSON.Serialize(new DirectApiErrorEnvelope(new DirectApiError(
                    DirectApiErrorCodes.ProjectionLag,
                    "The public projection for this resource has not caught up yet; retry the same request shortly."))));
        }
    }

    private sealed class StopPendingResult : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            httpContext.Response.Headers.RetryAfter = ProjectionLagRetryAfterSeconds;
            httpContext.Response.ContentType = "application/json";
            await httpContext.Response.WriteAsync(
                JSON.Serialize(new DirectApiErrorEnvelope(new DirectApiError(
                    DirectApiErrorCodes.StopPending,
                    "The stop outcome is not confirmed yet; retry the same request."))));
        }
    }

    /// <summary>
    /// Serves an already-serialized public snapshot exactly as the
    /// projection committed it — the response body is the stored
    /// allowlist, byte for byte, never a re-serialization through an
    /// internal shape.
    /// </summary>
    public static IResult Snapshot(string snapshotJson) =>
        Results.Content(snapshotJson, "application/json");

    /// <summary>
    /// The one mapping from a projection read outcome to its HTTP
    /// answer, shared by the resource reads and reused by the command
    /// and event routes for their projection-sourced bodies.
    /// </summary>
    public static IResult PublicRead(PublicReadOutcome outcome, string resourceNotFoundCode) =>
        outcome.Status switch
        {
            PublicReadStatus.Found => Snapshot(outcome.SnapshotJson!),
            PublicReadStatus.NotFound => ResourceNotFound(resourceNotFoundCode),
            _ => ProjectionLag(),
        };

    public static IResult PublicEvents(PublicSessionEventReadOutcome outcome) =>
        outcome.Status switch
        {
            PublicSessionEventReadStatus.Found => Results.Json(
                outcome.Page!,
                options: JSON.PublicApi),
            PublicSessionEventReadStatus.NotFound => ResourceNotFound(DirectApiErrorCodes.SessionNotFound),
            PublicSessionEventReadStatus.ProjectionLag => ProjectionLag(),
            PublicSessionEventReadStatus.CursorInvalid => CursorInvalid(),
            PublicSessionEventReadStatus.CursorExpired => CursorExpired(
                outcome.EarliestSequence,
                outcome.LatestSequence),
            _ => throw new ArgumentOutOfRangeException(),
        };

    private static string ResourceNotFoundMessage(string code) => code switch
    {
        DirectApiErrorCodes.JobNotFound => "The requested agent job was not found in this project.",
        DirectApiErrorCodes.SessionNotFound => "The requested agent session was not found in this project.",
        DirectApiErrorCodes.InputNotFound => "The requested agent input was not found in this project.",
        DirectApiErrorCodes.TurnNotFound => "The requested agent turn was not found in this project.",
        DirectApiErrorCodes.AgentNotFound => "The requested Agent was not found in this project.",
        _ => "The requested resource was not found in this project.",
    };

    /// <summary>
    /// Placeholder answer for the route templates registered ahead of
    /// their endpoint delegates. The middleware
    /// pipeline in front of it is the shipped boundary; only a caller
    /// that passed carrier, grant, scope, and Project authorization
    /// reaches it. Later tasks replace the delegates without touching
    /// the pipeline.
    /// </summary>
    public static IResult NotImplemented() =>
        Error(
            StatusCodes.Status501NotImplemented,
            DirectApiErrorCodes.NotImplemented,
            "This direct API route is not implemented yet.");
}
