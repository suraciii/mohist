using System.Text.Json;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Api;

/// <summary>
/// The Web login surface: exchanging an operator-level credential for a
/// browser session cookie, probing the current session, and logging out
/// by revoking it server-side. POST is exempt from auth resolution (it
/// carries the credential to be exchanged); GET and DELETE require the
/// session itself.
/// </summary>
public static class AuthSessionRoutes
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    public static WebApplication MapAuthSessionRoutes(this WebApplication app)
    {
        app.MapPost("/api/auth/session", async (
            HttpContext context,
            FileCredentialLoader fileCredentials,
            ICredentialStore credentials,
            IAuthAuditRecorder audit,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var presented = await ReadTokenAsync(context.Request, ct);
            if (presented is null)
                return ApiResults.BadRequest("request body must be a JSON object with a token string", "invalid_json_body");

            // Login is the one place a presented credential is validated
            // against operator scope by hand: session issuance is its own
            // policy, not a route scope check.
            var principalId = fileCredentials.TryResolve(presented)?.Id
                ?? await ResolveIssuedOperatorIdAsync(credentials, presented, ct);
            if (principalId is null)
                return ApiResults.Fail("Invalid token.", 401, "unauthorized");

            var now = timeProvider.GetUtcNow();
            var token = CredentialToken.Generate(CredentialKind.Session);
            var credential = new Credential(
                Guid.NewGuid().ToString(),
                principalId,
                CredentialKind.Session,
                CredentialToken.Hash(token),
                [Scope.Operator],
                Name: null,
                Prefix: null,
                ProjectId: null,
                ExpiresAt: now + SessionLifetime,
                RevokedAt: null,
                CreatedAt: now);
            await credentials.CreateAsync(credential, ct);
            await audit.RecordAsync(AuthAuditEvent.SessionEstablished(principalId, credential.Id, now), ct);

            context.Response.Cookies.Append(
                AuthResolutionMiddleware.SessionCookieName,
                token,
                new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    Secure = context.Request.IsHttps,
                    MaxAge = SessionLifetime,
                });
            return ApiResults.Ok();
        });

        app.MapGet("/api/auth/session", () => ApiResults.Ok()).RequireScopes(Scope.Operator);

        app.MapDelete("/api/auth/session", async (
            HttpContext context,
            ICredentialStore credentials,
            IAuthAuditRecorder audit,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var cookie = context.Request.Cookies[AuthResolutionMiddleware.SessionCookieName];
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                var tokenHash = CredentialToken.Hash(cookie);
                var revokedAt = timeProvider.GetUtcNow();
                var credential = await credentials.FindActiveAsync(tokenHash, ct);
                await credentials.RevokeAsync(tokenHash, revokedAt, ct);
                if (credential is not null)
                {
                    await audit.RecordAsync(AuthAuditEvent.CredentialRevoked(
                        credential.PrincipalId, credential.Id, credential.Kind, credential.Name, revokedAt), ct);
                }
            }

            context.Response.Cookies.Delete(AuthResolutionMiddleware.SessionCookieName);
            return ApiResults.Ok();
        }).RequireScopes(Scope.Operator);

        return app;
    }

    private static async Task<string?> ResolveIssuedOperatorIdAsync(
        ICredentialStore credentials,
        string token,
        CancellationToken ct)
    {
        if (!CredentialToken.TryParse(token, out _))
            return null;
        var credential = await credentials.FindActiveAsync(CredentialToken.Hash(token), ct);
        return credential is not null && credential.Scopes.Contains(Scope.Operator)
            ? credential.PrincipalId
            : null;
    }

    private static async Task<string?> ReadTokenAsync(HttpRequest request, CancellationToken ct)
    {
        JsonElement body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, JSON.Options, ct);
        }
        catch (JsonException)
        {
            return null;
        }

        return body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("token", out var token)
            && token.ValueKind == JsonValueKind.String
            ? token.GetString()
            : null;
    }
}
