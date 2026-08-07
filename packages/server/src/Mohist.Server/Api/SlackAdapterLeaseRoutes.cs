using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Api;

/// <summary>
/// Operator-authenticated loopback routes for the Socket adapter lease
/// surface: discovery, validation/runtime acquire, Socket hello, renew.
/// Only acquire responses carry tokens; discovery, hello and renew stay
/// secret-free. Every route requires the shared operator token plus an
/// explicit operator identity (see <see cref="ISlackAdapterOperatorAuthenticator"/>).
/// </summary>
public static class SlackAdapterLeaseRoutes
{
    public static WebApplication MapSlackAdapterLeaseRoutes(this WebApplication app)
    {
        var leases = app.MapGroup("/api/slack-adapter/leases");

        leases.MapGet("/targets", async (
            HttpContext http,
            SlackAdapterLeaseService service,
            ISlackAdapterOperatorAuthenticator auth,
            CancellationToken ct) =>
        {
            var operatorId = await auth.AuthenticateAsync(http, ct);
            if (operatorId is null)
                return OperatorRequired();
            return ApiResults.Ok(await service.DiscoverAsync(operatorId, ct));
        });

        leases.MapPost("/acquire", async (
            HttpContext http,
            SlackAcquireLeaseBody body,
            SlackAdapterLeaseService service,
            ISlackAdapterOperatorAuthenticator auth,
            CancellationToken ct) =>
        {
            var operatorId = await auth.AuthenticateAsync(http, ct);
            if (operatorId is null)
                return OperatorRequired();
            var invalid = InvalidBody(body);
            if (invalid is not null)
                return invalid;
            if (body!.Kind != SlackLeaseKind.Validation && body.Kind != SlackLeaseKind.Runtime)
                return ApiResults.BadRequest("kind must be 'validation' or 'runtime'.", "invalid_lease_kind");
            var target = body.Target?.ToTargetRef();
            if (target is null)
                return ApiResults.BadRequest(
                    "target must identify a manager enrollment or a connection.", "invalid_target");
            if (string.IsNullOrWhiteSpace(body.AdapterId))
                return ApiResults.BadRequest("adapterId is required.");

            object? result = body.Kind == SlackLeaseKind.Validation
                ? await service.AcquireValidationLeaseAsync(operatorId, target, body.AdapterId, ct)
                : await service.AcquireRuntimeLeaseAsync(operatorId, target, body.AdapterId, ct);
            return result is null
                ? ApiResults.Conflict(
                    "The lease target cannot acquire this lease right now.", "lease_not_acquirable")
                : ApiResults.Ok(result);
        });

        leases.MapPost("/hello", async (
            HttpContext http,
            SlackHelloLeaseBody body,
            SlackAdapterLeaseService service,
            ISlackAdapterOperatorAuthenticator auth,
            CancellationToken ct) =>
        {
            var operatorId = await auth.AuthenticateAsync(http, ct);
            if (operatorId is null)
                return OperatorRequired();
            var invalid = InvalidBody(body);
            if (invalid is not null)
                return invalid;
            var target = body.Target?.ToTargetRef();
            if (target is null)
                return ApiResults.BadRequest(
                    "target must identify a manager enrollment or a connection.", "invalid_target");
            if (string.IsNullOrWhiteSpace(body.LeaseId))
                return ApiResults.BadRequest("leaseId is required.");
            if (string.IsNullOrWhiteSpace(body.AppId))
                return ApiResults.BadRequest("appId is required.");

            return await service.ReportHelloAsync(operatorId, target, body.LeaseId, body.AppId, ct) switch
            {
                SlackHelloOutcome.Verified => ApiResults.Ok(new { outcome = "verified" }),
                SlackHelloOutcome.AppIdMismatch => ApiResults.Conflict(
                    "The Socket hello app_id does not match the lease target.", "app_id_mismatch"),
                _ => ApiResults.Conflict(
                    "The lease is stale, expired, or unknown; acquire a new lease.", "lease_stale_or_expired"),
            };
        });

        leases.MapPost("/renew", async (
            HttpContext http,
            SlackRenewLeaseBody body,
            SlackAdapterLeaseService service,
            ISlackAdapterOperatorAuthenticator auth,
            CancellationToken ct) =>
        {
            var operatorId = await auth.AuthenticateAsync(http, ct);
            if (operatorId is null)
                return OperatorRequired();
            var invalid = InvalidBody(body);
            if (invalid is not null)
                return invalid;
            var target = body.Target?.ToTargetRef();
            if (target is null)
                return ApiResults.BadRequest(
                    "target must identify a manager enrollment or a connection.", "invalid_target");
            if (string.IsNullOrWhiteSpace(body.LeaseId))
                return ApiResults.BadRequest("leaseId is required.");
            if (string.IsNullOrWhiteSpace(body.AdapterId))
                return ApiResults.BadRequest("adapterId is required.");

            var result = await service.RenewLeaseAsync(operatorId, target, body.LeaseId, body.AdapterId, ct);
            return result is null
                ? ApiResults.Conflict(
                    "The lease is stale, expired, or unknown; acquire a new lease.", "lease_stale_or_expired")
                : ApiResults.Ok(result);
        });

        return app;
    }

    private static IResult OperatorRequired() =>
        ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");

    private static IResult? InvalidBody(object? body) =>
        body is null ? ApiResults.BadRequest("request body is required.", "invalid_request") : null;
}

public sealed class SlackAcquireLeaseBody
{
    public string Kind { get; init; } = string.Empty;
    public SlackLeaseTargetBody? Target { get; init; }
    public string AdapterId { get; init; } = string.Empty;
}

public sealed class SlackHelloLeaseBody
{
    public SlackLeaseTargetBody? Target { get; init; }
    public string LeaseId { get; init; } = string.Empty;
    public string AppId { get; init; } = string.Empty;
}

public sealed class SlackRenewLeaseBody
{
    public SlackLeaseTargetBody? Target { get; init; }
    public string LeaseId { get; init; } = string.Empty;
    public string AdapterId { get; init; } = string.Empty;
}

public sealed class SlackLeaseTargetBody
{
    public string Kind { get; init; } = string.Empty;
    public string? EnrollmentId { get; init; }
    public string? WorkspaceTeamId { get; init; }
    public string? ProjectId { get; init; }
    public string? ConnectionId { get; init; }

    public SlackLeaseTargetRef? ToTargetRef() => Kind switch
    {
        SlackLeaseTargetKind.Manager
            when !string.IsNullOrWhiteSpace(EnrollmentId) && !string.IsNullOrWhiteSpace(WorkspaceTeamId) =>
            new SlackLeaseTargetRef.Manager(EnrollmentId, WorkspaceTeamId),
        SlackLeaseTargetKind.Connection
            when !string.IsNullOrWhiteSpace(ProjectId) && !string.IsNullOrWhiteSpace(ConnectionId) =>
            new SlackLeaseTargetRef.Connection(ProjectId, ConnectionId),
        _ => null,
    };
}
