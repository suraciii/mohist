using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Data.Auth;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Auth.Identity;

/// <summary>
/// Resolves the presenting credential into a
/// <see cref="MohistPrincipal"/> for every request on the auth surface
/// that is not exempt. The Bearer header wins over the
/// <c>mohist_session</c> cookie (first hit order); file credentials match
/// by constant-time comparison, issued credentials by SHA-256 hash
/// lookup. Any failure answers 401 with RFC 6750's
/// <c>invalid_token</c> challenge and never distinguishes missing,
/// expired, or revoked credentials. Tokens in the query string are always
/// rejected (RFC 6750 §2.3).
/// </summary>
public sealed class AuthResolutionMiddleware : IMiddleware, IScopedService
{
    public const string SessionCookieName = "mohist_session";

    private const string AuthorizationHeader = "Authorization";
    private const string BearerScheme = "Bearer ";
    private const string WwwAuthenticateChallenge = "Bearer error=\"invalid_token\"";
    private const string RejectionBody =
        """{"success":false,"error":"Authentication required.","code":"unauthorized"}""";

    private readonly FileCredentialLoader _fileCredentials;
    private readonly ICredentialStore _credentials;

    public AuthResolutionMiddleware(
        FileCredentialLoader fileCredentials,
        ICredentialStore credentials)
    {
        _fileCredentials = fileCredentials;
        _credentials = credentials;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path;
        if (!IsAuthSurface(path) || AuthExemptionList.IsExempt(path, context.Request.Method))
        {
            await next(context);
            return;
        }

        if (QueryCarriesToken(context.Request.Query)
            || ResolveToken(context.Request) is not { } token)
        {
            await RejectAsync(context);
            return;
        }

        var principal = _fileCredentials.TryResolve(token)
            ?? await ResolveFromStoreAsync(token, context.RequestAborted);
        if (principal is null)
        {
            await RejectAsync(context);
            return;
        }

        context.Items[MohistPrincipal.HttpContextItemKey] = principal;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, principal.Id)],
            "mohist"));
        await next(context);
    }

    private async Task<MohistPrincipal?> ResolveFromStoreAsync(string token, CancellationToken ct)
    {
        if (!CredentialToken.TryParse(token, out _))
            return null;
        var credential = await _credentials.FindActiveAsync(CredentialToken.Hash(token), ct);
        return credential is null ? null : ToPrincipal(credential);
    }

    private static MohistPrincipal ToPrincipal(Credential credential) =>
        new(
            credential.PrincipalId,
            credential.PrincipalId switch
            {
                MohistPrincipal.AdminPrincipalId => PrincipalKind.Admin,
                MohistPrincipal.ServicePrincipalId => PrincipalKind.Service,
                _ => PrincipalKind.Agent,
            },
            credential.PrincipalId,
            credential.Scopes);

    private static bool IsAuthSurface(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.Ordinal)
        || path.StartsWithSegments("/hubs", StringComparison.Ordinal)
        || path.StartsWithSegments("/otel/api", StringComparison.Ordinal);

    private static bool QueryCarriesToken(IQueryCollection query)
    {
        foreach (var key in query.Keys)
        {
            if (key.Contains("token", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? ResolveToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue(AuthorizationHeader, out var authorization))
        {
            if (authorization.Count != 1)
                return null;
            var header = authorization[0];
            if (string.IsNullOrEmpty(header)
                || !header.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var token = header[BearerScheme.Length..].Trim();
            return token.Length == 0 || token.Contains(' ') ? null : token;
        }

        return request.Cookies.TryGetValue(SessionCookieName, out var cookie)
            && !string.IsNullOrWhiteSpace(cookie)
            ? cookie
            : null;
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = WwwAuthenticateChallenge;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(RejectionBody);
    }
}

public static class AuthResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseMohistAuth(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AuthResolutionMiddleware>();
    }
}
