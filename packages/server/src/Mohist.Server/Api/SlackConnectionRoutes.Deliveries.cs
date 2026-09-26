using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Api;

public static partial class SlackConnectionRoutes
{
    /// <summary>
    /// Operator-facing delivery management: what the Connection has queued, and
    /// the re-send request for a delivery without a confirmed outcome. The
    /// request never queues a mutation — it returns the delivery to the
    /// adapter's provider reconciliation.
    /// </summary>
    private static void MapDeliveryManagementRoutes(RouteGroupBuilder management)
    {
        management.MapGet("/{connectionId}/deliveries", async (HttpContext context, string connectionId, AgentConnectionStore connections, SlackOutboxStore outbox, CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");
            var list = await outbox.ListAsync(projectId, connectionId, ct);
            return ApiResults.Ok(list);
        });

        management.MapPost("/{connectionId}/deliveries/{deliveryId}/resend", async (HttpContext context, string connectionId, string deliveryId, AgentConnectionStore connections, SlackOutboxStore outbox, CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");
            if (string.IsNullOrWhiteSpace(deliveryId))
                return ApiResults.BadRequest("deliveryId is required.");
            try
            {
                var request = await outbox.RequestDeliveryReconciliationAsync(projectId, connectionId, deliveryId, ct);
                if (request is null)
                    return ApiResults.Conflict(
                        "Only Delivery uncertain or dead-lettered deliveries can be reconciled, and only while the Connection is enabled.",
                        "delivery_not_reconcilable");
                return ApiResults.Ok(new
                {
                    id = request.Id,
                    state = request.State,
                    requeued = false,
                    revived = request.Revived,
                    reconciliation = "provider_reconciliation",
                });
            }
            catch (SlackOutboxRowNotFoundException)
            {
                return ApiResults.NotFound("Delivery was not found.");
            }
        });
    }
}
