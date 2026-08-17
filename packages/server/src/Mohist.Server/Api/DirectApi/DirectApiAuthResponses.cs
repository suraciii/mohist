using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Api.DirectApi;

/// <summary>
/// Terminal 401/403 writers for the <c>/api/v1</c> boundary. Both the
/// auth layer (for unresolvable credentials) and
/// <see cref="ExternalAgentApiMiddleware"/> (for the bearer-only carrier
/// rule) write through this one class so the direct API's
/// non-classifying 401 body can never drift between the two rejection
/// points: every failure — missing, expired, revoked, or wrong carrier —
/// answers byte-identically.
/// </summary>
public static class DirectApiAuthResponses
{
    /// <summary>
    /// The direct API's 401 challenge: a plain <c>Bearer</c>, never
    /// the control-plane <c>Bearer error="invalid_token"</c> form.
    /// </summary>
    public const string BearerChallenge = "Bearer";

    public static async Task WriteUnauthenticatedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = BearerChallenge;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            JSON.Serialize(new DirectApiErrorEnvelope(DirectApiError.Unauthenticated())));
    }

    public static async Task WriteForbiddenAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            JSON.Serialize(new DirectApiErrorEnvelope(DirectApiError.Forbidden())));
    }
}
