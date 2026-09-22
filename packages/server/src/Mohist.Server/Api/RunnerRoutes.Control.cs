using System.Net.WebSockets;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Runner.Services.WebSocket;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    private const string RunnerConnectionIdHeader = "X-Runner-Connection-Id";

    public static WebApplication MapRunnerControlRoute(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner/{runnerId}").RequireScopes(Scope.Runner);
        group.MapGet("/control", async (
            string runnerId,
            HttpContext context,
            RunnerControlWebSocketRegistry registry,
            RunnerAuthorityAdmissionObserver authorityAdmissions,
            CancellationToken ct) =>
        {
            if (ResolvePresentedRunnerAuthority(context) is not { } presentedAuthority)
                return InvalidRunnerCredentialAuthority();
            await authorityAdmissions.ObserveAsync(
                "control",
                runnerId,
                presentedAuthority,
                ct);
            if (!context.WebSockets.IsWebSocketRequest)
                return ApiResults.BadRequest("WebSocket upgrade required", "websocket_required");

            var rawConnectionId = context.Request.Headers[RunnerConnectionIdHeader].ToString();
            if (!Guid.TryParseExact(rawConnectionId, "D", out var connectionId)
                || !string.Equals(rawConnectionId, connectionId.ToString("D"), StringComparison.Ordinal))
                return ApiResults.BadRequest(
                    $"{RunnerConnectionIdHeader} must be a canonical lowercase D-format UUID",
                    "runner_connection_id_invalid");

            var handshake = RunnerControlHandshake.FromQuery(context.Request.Query);
            if (!await registry.IsControlAuthorityCurrentAsync(
                    runnerId,
                    handshake,
                    presentedAuthority))
                return InvalidRunnerCredentialAuthority();
            if (!registry.TryReserve(connectionId, out var reservation))
                return ApiResults.Conflict("Runner connection ID is already active", "runner_connection_id_active");

            var transferred = false;
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                transferred = true;
                await registry.RunAsync(
                    runnerId,
                    reservation,
                    socket,
                    handshake,
                    presentedAuthority,
                    ct);
                return Results.Empty;
            }
            finally
            {
                if (!transferred)
                    registry.ReleaseReservation(reservation);
            }
        });
        return app;
    }

    private static RunnerPresentedAuthority? ResolvePresentedRunnerAuthority(HttpContext context)
    {
        if (context.Items[MohistPrincipal.HttpContextItemKey] is not MohistPrincipal principal)
            return null;
        if (principal.Scopes.Contains(Scope.Operator))
            return new RunnerPresentedAuthority(CredentialId: null, OperatorOverride: true);
        if (!string.IsNullOrWhiteSpace(principal.RunnerId)
            && !string.IsNullOrWhiteSpace(principal.CredentialId))
            return new RunnerPresentedAuthority(principal.CredentialId, OperatorOverride: false);
        return null;
    }

    private static IResult InvalidRunnerCredentialAuthority() =>
        ApiResults.Fail(
            "The presented credential is not the current Runner execution authority.",
            StatusCodes.Status403Forbidden,
            "runner_credential_authority_invalid");
}
