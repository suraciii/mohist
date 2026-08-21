using System.Text.Json;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Api;

public static partial class RunnerRoutes
{
    public static WebApplication MapRunnerManagerExecutionRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner/{runnerId}").RequireScopes(Scope.Runner);
        group.MapPost("/manager-executions/{executionId}/revoke", (
            string executionId,
            ManagerExecutionCapabilityIssuer managerCredentials) =>
        {
            if (string.IsNullOrWhiteSpace(executionId))
                return ApiResults.BadRequest("executionId is required", "manager_execution_id_required");
            managerCredentials.RevokeExecution(executionId);
            return Results.Ok();
        });
        return app;
    }

    private static async Task<bool> IsManagerAgentSessionAsync(
        AgentSessionQuery sessionQuery,
        string sessionId,
        CancellationToken ct)
    {
        var records = await sessionQuery.ListByIdsAsync([sessionId], ct);
        return records.Any(record =>
            string.Equals(record.Label(AgentSessionQueryMetadataKeys.ProjectId), SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal));
    }

    internal static bool ContainsManagerCredentialExpiry(
        IReadOnlyList<AgentSessionRuntimeEventRequest> runtimeEvents) =>
        runtimeEvents.Any(runtimeEvent =>
            string.Equals(runtimeEvent.Type, RuntimeEventTypes.SessionActivity, StringComparison.Ordinal)
            && runtimeEvent.Payload.ValueKind == JsonValueKind.Object
            && runtimeEvent.Payload.TryGetProperty("reason", out var reason)
            && reason.ValueKind == JsonValueKind.String
            && string.Equals(reason.GetString(), "manager-credential-expired", StringComparison.Ordinal));

    private static void RevokeCompletedManagerFollowupLeases(
        string sessionId,
        IReadOnlyList<AgentSessionRuntimeEventRequest> runtimeEvents,
        ManagerExecutionCapabilityIssuer managerCredentials)
    {
        foreach (var runtimeEvent in runtimeEvents.Where(eventItem =>
                     string.Equals(eventItem.Type, RuntimeEventTypes.SessionActivity, StringComparison.Ordinal)))
        {
            if (runtimeEvent.Payload.ValueKind != JsonValueKind.Object
                || !runtimeEvent.Payload.TryGetProperty("activity", out var activity)
                || activity.ValueKind != JsonValueKind.String
                || activity.GetString() is not ("idle" or "unknown")
                || !runtimeEvent.Payload.TryGetProperty("operationId", out var operation)
                || operation.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(operation.GetString()))
                continue;
            managerCredentials.RevokeExecution($"manager:{sessionId}:{operation.GetString()}");
        }
    }
}
