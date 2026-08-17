using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Api;

public static partial class SlackConnectionRoutes
{
    private static void MapSlackManagerAdapterRoutes(WebApplication app)
    {
        app.MapGet("/api/slack-manager/adapter", async (
            HttpContext http,
            SlackManagerAdapterQuerier adapters,
            CancellationToken ct) =>
        {
            var targets = await adapters.ListReadyTargetsAsync(ct);
            return ApiResults.Ok(targets.Select(target => new
            {
                ownerKind = SlackDeliveryOwnerKinds.Manager,
                enrollmentId = target.EnrollmentId,
                workspaceTeamId = target.WorkspaceTeamId,
            }).ToArray());
        });

        app.MapPost("/api/slack-manager/adapter/{enrollmentId}/deliveries/claim", async (
            HttpContext http,
            string enrollmentId,
            DeliveryClaimBody body,
            SlackOutboxStore outbox,
            SlackAdapterLeaseService leases,
            ISlackAdapterOperatorAuthenticator auth,
            CancellationToken ct) =>
        {
            var operatorId = await auth.AuthenticateAsync(http, ct);
            if (operatorId is null)
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            if (!await leases.ValidateManagerRuntimeLeaseByEnrollmentAsync(operatorId, enrollmentId, body?.LeaseId ?? string.Empty, body?.AdapterId ?? string.Empty, ct))
                return LeaseStaleOrExpired();
            var entry = await outbox.ClaimAsync(
                SlackDeliveryOwnerIds.ManagerProjectId,
                enrollmentId,
                body?.AdapterId ?? string.Empty,
                ct,
                SlackDeliveryOwnerKinds.Manager);
            return entry is null ? ApiResults.Ok<object?>(null) : ApiResults.Ok(entry);
        });

        app.MapPost("/api/slack-manager/adapter/{enrollmentId}/deliveries/claim-uncertain", async (
            HttpContext http,
            string enrollmentId,
            DeliveryClaimBody body,
            SlackOutboxStore outbox,
            SlackAdapterLeaseService leases,
            ISlackAdapterOperatorAuthenticator auth,
            CancellationToken ct) =>
        {
            var operatorId = await auth.AuthenticateAsync(http, ct);
            if (operatorId is null)
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            if (!await leases.ValidateManagerRuntimeLeaseByEnrollmentAsync(operatorId, enrollmentId, body?.LeaseId ?? string.Empty, body?.AdapterId ?? string.Empty, ct))
                return LeaseStaleOrExpired();
            var entry = await outbox.ClaimUncertainAsync(
                SlackDeliveryOwnerIds.ManagerProjectId,
                enrollmentId,
                body?.AdapterId ?? string.Empty,
                ct,
                SlackDeliveryOwnerKinds.Manager);
            return entry is null ? ApiResults.Ok<object?>(null) : ApiResults.Ok(entry);
        });

        app.MapPost("/api/slack-manager/adapter/{enrollmentId}/deliveries/ack", async (
            HttpContext http,
            string enrollmentId,
            DeliveryAckBody body,
            SlackOutboxStore outbox,
            SlackAdapterLeaseService leases,
            ISlackAdapterOperatorAuthenticator auth,
            CancellationToken ct) =>
        {
            var operatorId = await auth.AuthenticateAsync(http, ct);
            if (operatorId is null)
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            if (!await leases.ValidateManagerRuntimeLeaseByEnrollmentAsync(operatorId, enrollmentId, body?.LeaseId ?? string.Empty, body?.AdapterId ?? string.Empty, ct))
                return LeaseStaleOrExpired();
            if (body is null || string.IsNullOrWhiteSpace(body.Id))
                return ApiResults.BadRequest("id is required.");
            if (string.IsNullOrWhiteSpace(body.AdapterId))
                return ApiResults.BadRequest("adapterId is required.");

            var managerDelivery = (await outbox.ListManagerAsync(enrollmentId, ct)).Entries
                .FirstOrDefault(entry => string.Equals(entry.Id, body.Id, StringComparison.Ordinal));
            if (managerDelivery is null)
                return ApiResults.NotFound("Manager delivery was not found.");

            if (string.Equals(body.Outcome, "delivered", StringComparison.OrdinalIgnoreCase))
                await outbox.MarkDeliveredAsync(SlackDeliveryOwnerIds.ManagerProjectId, body.Id, body.ProviderMessageIdentity, body.AdapterId, ct);
            else if (string.Equals(body.Outcome, "uncertain", StringComparison.OrdinalIgnoreCase))
                await outbox.MarkDeliveryUncertainAsync(SlackDeliveryOwnerIds.ManagerProjectId, body.Id, body.Reason, body.AdapterId, ct);
            else
                await outbox.ScheduleRetryAsync(SlackDeliveryOwnerIds.ManagerProjectId, body.Id, body.Reason, body.AdapterId, ct);
            return ApiResults.Ok(new { id = body.Id, outcome = body.Outcome, ownerKind = SlackDeliveryOwnerKinds.Manager });
        });
    }
}
