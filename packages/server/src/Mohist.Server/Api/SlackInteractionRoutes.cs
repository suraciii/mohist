using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Api;

public static class SlackInteractionRoutes
{
    public static WebApplication MapSlackInteractionRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/slack-connections/{connectionId}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/interactions", async (
            HttpContext http,
            string connectionId,
            SlackInteractionRequest? request,
            AgentConnectionStore connections,
            SlackTurnControlService controls,
            SlackOutboxStore outbox,
            SlackAdapterLeaseService leases,
            ISlackAdapterOperatorAuthenticator auth,
            CancellationToken ct) =>
        {
            var operatorId = await auth.AuthenticateAsync(http, ct);
            if (operatorId is null)
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            var projectId = http.GetResolvedProject().Id;
            if (!await leases.ValidateRuntimeLeaseAsync(
                    operatorId,
                    new SlackLeaseTargetRef.Connection(projectId, connectionId),
                    request?.LeaseId ?? string.Empty,
                    request?.AdapterId ?? string.Empty,
                    ct))
            {
                return ApiResults.Conflict(
                    "The runtime Socket lease is stale, expired, or unknown; acquire a new lease.",
                    "lease_stale_or_expired");
            }
            if (request is null)
                return ApiResults.BadRequest("Interaction is required.", "interaction_missing");
            if (string.IsNullOrWhiteSpace(request.InteractionId)
                || string.IsNullOrWhiteSpace(request.TeamId)
                || string.IsNullOrWhiteSpace(request.ConversationId)
                || string.IsNullOrWhiteSpace(request.MessageTs)
                || string.IsNullOrWhiteSpace(request.ActorSlackUserId)
                || string.IsNullOrWhiteSpace(request.ActionId)
                || string.IsNullOrWhiteSpace(request.ActionValue))
                return ApiResults.BadRequest("A complete Slack interaction envelope is required.", "invalid_interaction");

            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");
            if (connection.DesiredState == Agent.Domain.DesiredStateKind.Disabled)
                return ApiResults.Conflict("This Slack Connection is disabled.", "connection_disabled");

            var result = await controls.HandleAsync(projectId, connection, request, ct);
            if (!string.Equals(result.State, "replayed", StringComparison.Ordinal))
            {
                await outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
                    projectId,
                    connection.Id,
                    connection.WorkspaceTeamId,
                    request.ConversationId,
                    SlackOutboxKinds.UserAction,
                    ActionDispatchRef(request.ActionValue),
                    JsonSerializer.Serialize(new SlackDeliveryPayload(
                        SlackDeliveryOperations.ChatUpdate,
                        result.Text,
                        ProviderMessageIdentity: new SlackProviderMessageIdentity(
                            request.ConversationId,
                            request.MessageTs),
                        Blocks: result.Blocks)),
                    request.ThreadTs), ct);
            }
            return ApiResults.Ok(new { state = result.State });
        });

        return app;
    }

    internal static string ActionDispatchRef(string actionValue) =>
        $"slack-turn-control:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actionValue))).ToLowerInvariant()}";
}

public sealed class SlackInteractionRequest
{
    public string EventType { get; init; } = "block_actions";
    public string InteractionId { get; init; } = string.Empty;
    public string TeamId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string MessageTs { get; init; } = string.Empty;
    public string? ThreadTs { get; init; }
    public string ActorSlackUserId { get; init; } = string.Empty;
    public string ActionId { get; init; } = string.Empty;
    public string ActionValue { get; init; } = string.Empty;
    public string LeaseId { get; init; } = string.Empty;
    public string AdapterId { get; init; } = string.Empty;
}
