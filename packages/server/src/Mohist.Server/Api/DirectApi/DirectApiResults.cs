using Microsoft.AspNetCore.Http;

namespace Mohist.Server.Api.DirectApi;

/// <summary>
/// <see cref="IResult"/> factories for the <c>/api/v1</c> error envelope.
/// Direct API endpoints answer every error — transport, authorization,
/// validation, conflict, and lag — through this class so the surface's
/// envelope stays one shape: <c>{ "error": { "code", "message" } }</c>.
/// </summary>
public static class DirectApiResults
{
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
