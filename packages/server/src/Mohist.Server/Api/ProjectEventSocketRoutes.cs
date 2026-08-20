using System.Net;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Events.WebSocket;

namespace Mohist.Server.Api;

public static class ProjectEventSocketRoutes
{
    public static WebApplication MapProjectEventSocketRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/events/socket")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>()
            .RequireScopes(Scope.Operator, Scope.Readonly);
        group.MapGet("", HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        EventWebSocketRegistry registry,
        CancellationToken ct)
    {
        if (context.Request.QueryString.HasValue)
            return ApiResults.BadRequest("Query parameters are not supported", "query_not_supported");

        if (!context.WebSockets.IsWebSocketRequest)
            return ApiResults.BadRequest("WebSocket upgrade required", "websocket_required");

        if (context.Items.TryGetValue(IntegrationProjectConstraint.ItemKey, out var constraint)
            && constraint is IntegrationProjectConstraint.Resolution resolution
            && !resolution.IsSatisfied)
            return Results.Json(
                new ApiResponse<object>(false, default, "Credential is constrained to another project", "forbidden"),
                statusCode: StatusCodes.Status403Forbidden);

        if (context.Items.TryGetValue(CredentialCarrierResolution.HttpContextItemKey, out var carrier)
            && carrier is CredentialCarrier.Cookie
            && !HasValidOrigin(context.Request, context.Connection.RemoteIpAddress))
            return Results.Json(
                new ApiResponse<object>(false, default, "WebSocket Origin does not match the request authority", "forbidden"),
                statusCode: StatusCodes.Status403Forbidden);

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await registry.RunAsync(context.GetResolvedProject().Id, socket, ct);
        return Results.Empty;
    }

    internal static bool HasValidOrigin(HttpRequest request, IPAddress? remoteAddress)
    {
        var scheme = request.Scheme;
        var authority = request.Host.Value;
        var forwardedProto = request.Headers["X-Forwarded-Proto"];
        var forwardedHost = request.Headers["X-Forwarded-Host"];
        var hasProto = forwardedProto.Count > 0;
        var hasHost = forwardedHost.Count > 0;

        if (IPAddress.IsLoopback(remoteAddress ?? IPAddress.None) && (hasProto || hasHost))
        {
            if (!hasProto || !hasHost
                || forwardedProto.Count != 1 || forwardedHost.Count != 1
                || forwardedProto[0]!.Contains(',') || forwardedHost[0]!.Contains(','))
                return false;
            scheme = forwardedProto[0]!;
            authority = forwardedHost[0]!;
            if (!IsValidScheme(scheme) || !IsValidAuthority(scheme, authority)) return false;
        }

        if (!request.Headers.TryGetValue("Origin", out var origins)
            || origins.Count != 1
            || origins[0]!.Contains(',')
            || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
            || origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            return false;

        return string.Equals(origin.Scheme, scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Authority, authority, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool IsValidAuthority(string scheme, string authority) =>
        !string.IsNullOrWhiteSpace(authority)
        && !authority.Any(char.IsWhiteSpace)
        && Uri.TryCreate($"{scheme}://{authority}/", UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.Equals(uri.Authority, authority, StringComparison.OrdinalIgnoreCase);
}
