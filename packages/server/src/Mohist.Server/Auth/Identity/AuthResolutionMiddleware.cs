using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Project.Services;

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
/// <para>
/// The resolution also records two facts for the direct external Agent
/// API boundary (<c>/api/v1</c>): the credential carrier kind and, for a
/// PAT, the resolved <see cref="ExternalAgentCaller"/>. On direct
/// API paths this layer authenticates only: route scope and Project
/// grant authorization belong to
/// <see cref="ExternalAgentApiMiddleware"/>, which enforces
/// them with the direct API's own error envelope strictly before any
/// endpoint runs.
/// </para>
/// </summary>
public sealed class AuthResolutionMiddleware : IMiddleware, IScopedService
{
    public const string SessionCookieName = "mohist_session";

    /// <summary>The path prefix of the direct external Agent API.</summary>
    public const string DirectApiPathPrefix = ExternalAgentApiMiddleware.PathPrefix;

    private const string AuthorizationHeader = "Authorization";
    private const string BearerScheme = "Bearer ";
    private const string WwwAuthenticateChallenge = "Bearer error=\"invalid_token\"";
    private const string RejectionBody =
        """{"success":false,"error":"Authentication required.","code":"unauthorized"}""";

    private readonly FileCredentialLoader _fileCredentials;
    private readonly ICredentialStore _credentials;
    private readonly ProjectRefResolver _projects;

    public AuthResolutionMiddleware(
        FileCredentialLoader fileCredentials,
        ICredentialStore credentials,
        ProjectRefResolver projects)
    {
        _fileCredentials = fileCredentials;
        _credentials = credentials;
        _projects = projects;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path;
        if (!IsAuthSurface(path) || AuthExemptionList.IsExempt(path, context.Request.Method))
        {
            await next(context);
            return;
        }

        // On the direct API this layer authenticates only; scope, grant,
        // and Project authorization — with the direct API's error
        // envelope — are owned by ExternalAgentApiMiddleware, which runs
        // immediately after this one.
        var directApi = IsDirectApiPath(path);

        if (QueryCarriesToken(context.Request.Query)
            || ResolvePresentedCredential(context.Request) is not { } presented)
        {
            await RejectAsync(context, directApi);
            return;
        }

        var principal = _fileCredentials.TryResolve(presented.Token)
            ?? await ResolveFromStoreAsync(presented.Token, context, context.RequestAborted);
        if (principal is null)
        {
            await RejectAsync(context, directApi);
            return;
        }

        context.Items[MohistPrincipal.HttpContextItemKey] = principal;
        context.Items[CredentialCarrierResolution.HttpContextItemKey] = presented.Carrier;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, principal.Id)],
            "mohist"));

        if (directApi)
        {
            await next(context);
            return;
        }

        var required = ResolveRequiredScopes(context);
        if (!ScopeSatisfaction.Satisfies(required, principal.Scopes, EffectiveMethod(context)))
        {
            await RejectForbiddenAsync(context, principal, required);
            return;
        }

        if (principal.RunnerId is { } boundRunnerId
            && ClaimedRunnerId(context) is { } claimed
            && !string.Equals(claimed, boundRunnerId, StringComparison.Ordinal))
        {
            await RejectRunnerImpersonationAsync(context, principal, claimed, boundRunnerId);
            return;
        }

        await next(context);
    }

    /// <summary>
    /// The method the readonly rule sees. SignalR clients negotiate over
    /// POST on this stack; the handshake belongs to the observation
    /// connection itself (the GET upgrade that follows), so a readonly
    /// credential on a readonly-declared hub surface is not rejected by
    /// the negotiate verb. No other surface is affected: runner scope is
    /// method-agnostic and operator satisfies everything.
    /// </summary>
    private static string EffectiveMethod(HttpContext context)
    {
        if (HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.Value?.EndsWith("/negotiate", StringComparison.Ordinal) == true)
        {
            return HttpMethods.Get;
        }

        return context.Request.Method;
    }

    /// <summary>
    /// The route's declared scopes, or the method-based default for
    /// business routes: GET is the observation surface (operator or
    /// readonly), every other method requires operator.
    /// </summary>
    private static IReadOnlyList<Scope> ResolveRequiredScopes(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<RouteScopeRequirement>();
        if (metadata is not null)
            return metadata.Scopes;

        return HttpMethods.IsGet(context.Request.Method)
            ? RouteScopeRequirementExtensions.OperatorOrReadonly
            : RouteScopeRequirementExtensions.Operator;
    }

    /// <summary>
    /// The runner id a runner-scoped request self-declares, in priority
    /// order: the <c>/api/runner/{{runnerId}}/**</c> path segment and the
    /// <c>x-mohist-runner-id</c> header (task-log uploads). The auth
    /// layer never trusts any of these — they must all match the
    /// credential's binding.
    /// </summary>
    private static string? ClaimedRunnerId(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is not null)
        {
            var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 4
                && string.Equals(segments[0], "api", StringComparison.Ordinal)
                && string.Equals(segments[1], "runner", StringComparison.Ordinal))
            {
                return segments[2];
            }
        }

        var header = context.Request.Headers["x-mohist-runner-id"].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header;
    }

    private static async Task RejectForbiddenAsync(
        HttpContext context,
        MohistPrincipal principal,
        IReadOnlyList<Scope> required)
    {
        var requiredNames = required.Select(scope => scope.Name).ToArray();
        var grantedNames = principal.Scopes.Select(scope => scope.Name).ToArray();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            success = false,
            error = $"Insufficient scope. Route requires [{string.Join(", ", requiredNames)}]; "
                + $"principal '{principal.Id}' holds [{string.Join(", ", grantedNames)}].",
            code = "forbidden",
            details = new
            {
                principal = principal.Id,
                required = requiredNames,
                granted = grantedNames,
            },
        }));
    }

    private static async Task RejectRunnerImpersonationAsync(
        HttpContext context,
        MohistPrincipal principal,
        string claimedRunnerId,
        string boundRunnerId)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            success = false,
            error = $"Runner credential of principal '{principal.Id}' is bound to '{boundRunnerId}' "
                + $"but the request claims runner '{claimedRunnerId}'.",
            code = "forbidden",
            details = new
            {
                principal = principal.Id,
                boundRunnerId,
                claimedRunnerId,
            },
        }));
    }

    private async Task<MohistPrincipal?> ResolveFromStoreAsync(string token, HttpContext context, CancellationToken ct)
    {
        if (!CredentialToken.TryParse(token, out _))
            return null;
        var credential = await _credentials.FindActiveAsync(CredentialToken.Hash(token), ct);
        if (credential is null)
            return null;

        if (credential.Kind == CredentialKind.Integration)
            await RecordIntegrationConstraintAsync(context, credential);

        if (credential.Kind == CredentialKind.Pat)
        {
            // The direct external Agent API caller facts the credential
            // already carries; grant enforcement (403 when absent) is the
            // direct boundary's call, recorded here so that boundary never
            // needs to re-resolve the token.
            context.Items[ExternalAgentCaller.HttpContextItemKey] = new ExternalAgentCaller(
                credential.Id,
                credential.PrincipalId,
                credential.Scopes,
                credential.DirectApiProjectGrant);
        }

        return ToPrincipal(credential);
    }

    /// <summary>
    /// Integration credentials are narrowed to a project: resolves the
    /// request's project (route value or query) and records the
    /// constraint evaluation on the request. The per-project narrowing
    /// judgment (denying a request outside the constrained project) is
    /// the P2 scope gate, which reads
    /// <see cref="IntegrationProjectConstraint.ItemKey"/>.
    /// </summary>
    private async Task RecordIntegrationConstraintAsync(HttpContext context, Credential credential)
    {
        var projectRef = IntegrationProjectConstraint.ExtractProjectRef(context.Request);
        var requestProjectId = projectRef is null
            ? null
            : (await _projects.ResolveAsync(projectRef))?.Id;
        context.Items[IntegrationProjectConstraint.ItemKey] =
            new IntegrationProjectConstraint.Resolution(credential.ProjectId, requestProjectId);
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
            credential.Scopes,
            RunnerId: credential.Kind == CredentialKind.Runner ? credential.Name : null,
            ProjectId: credential.ProjectId);

    private static bool IsAuthSurface(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.Ordinal)
        || path.StartsWithSegments("/hubs", StringComparison.Ordinal)
        || path.StartsWithSegments("/otel/api", StringComparison.Ordinal);

    internal static bool IsDirectApiPath(PathString path) =>
        path.StartsWithSegments(DirectApiPathPrefix, StringComparison.Ordinal);

    private static bool QueryCarriesToken(IQueryCollection query)
    {
        foreach (var key in query.Keys)
        {
            if (key.Contains("token", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static PresentedCredential? ResolvePresentedCredential(HttpRequest request)
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
            return token.Length == 0 || token.Contains(' ')
                ? null
                : new PresentedCredential(token, CredentialCarrier.Bearer);
        }

        return request.Cookies.TryGetValue(SessionCookieName, out var cookie)
            && !string.IsNullOrWhiteSpace(cookie)
            ? new PresentedCredential(cookie, CredentialCarrier.Cookie)
            : null;
    }

    private static async Task RejectAsync(HttpContext context, bool directApi)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        if (directApi)
        {
            // The direct API's own, non-classifying 401: same body and
            // challenge for every failure, written by the same writer the
            // direct boundary uses so the two can never drift.
            await DirectApiAuthResponses.WriteUnauthenticatedAsync(context);
            return;
        }

        context.Response.Headers.WWWAuthenticate = WwwAuthenticateChallenge;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(RejectionBody);
    }

    private sealed record PresentedCredential(string Token, CredentialCarrier Carrier);
}

public static class AuthResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseMohistAuth(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AuthResolutionMiddleware>();
    }
}
