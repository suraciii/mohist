using System.Text;
using System.Text.Json;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Security;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;

namespace Mohist.Server.Api;

public static class SlackConnectionRoutes
{
    public static WebApplication MapSlackConnectionRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectRef}/slack-connections/{connectionId}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/ingress", async (
            HttpContext http,
            SlackIngressBody body,
            string connectionId,
            AgentConnectionStore connections,
            SlackOwnerClaimService claims,
            SlackProviderInboxStore inbox,
            SlackOutboxStore outbox,
            AgentQuerier agents,
            IAgentLauncher launcher,
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            var projectId = http.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");
            if (body is null || !body.IsDirectMessage)
                return ApiResults.Ok(new { kind = "ignored" });
            if (!string.Equals(body.TeamId, connection.WorkspaceTeamId, StringComparison.Ordinal))
                return ApiResults.BadRequest("The Slack workspace does not match this Connection.", "workspace_mismatch");

            var identity = new SlackMessageIdentity(body.TeamId, body.ConversationId, body.MessageTs);
            var identityError = identity.Validate();
            if (identityError.Length != 0)
                return ApiResults.BadRequest(identityError, "invalid_slack_identity");

            var decision = await claims.HandleInboundDmAsync(
                projectId,
                connectionId,
                new SlackInboundDm(body.SenderSlackUserId, body.Text ?? string.Empty),
                ct);
            if (decision.Kind == SlackInboundDecisionKind.Claimed)
            {
                await EnqueueReplyAsync(outbox, projectId, connection, body.ConversationId, "Owner claimed successfully.", null, ct);
                return ApiResults.Ok(new { kind = "claimed" });
            }
            if (decision.Kind == SlackInboundDecisionKind.Rejected)
            {
                await EnqueueReplyAsync(outbox, projectId, connection, body.ConversationId, decision.Reason ?? "The message was rejected.", null, ct);
                return ApiResults.Ok(new { kind = "rejected", reason = decision.Reason });
            }

            if (connection.ConnectionHealth == Agent.Domain.ConnectionHealthKind.Degraded
                && connection.HealthReason?.Contains("backpressured", StringComparison.OrdinalIgnoreCase) == true)
                return ApiResults.Conflict(
                    "This Slack Connection is backpressured; retry after pending deliveries drain.",
                    "slack_backpressured");

            var prompt = RemoveBotMention(body.Text ?? string.Empty, connection.BotUserId);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                const string reason = "Please send a task for the Agent to perform.";
                await EnqueueReplyAsync(outbox, projectId, connection, body.ConversationId, reason, null, ct);
                return ApiResults.Ok(new { kind = "rejected", reason });
            }

            var agent = await agents.GetByIdAsync(projectId, connection.AgentId);
            if (agent is null)
                return ApiResults.Fail("The Agent bound to this Connection no longer exists.", 409, "agent_not_found");

            SlackProviderInboxAcceptResult accepted;
            try
            {
                accepted = await inbox.AcceptAsync(new SlackProviderInboxDraft(
                    projectId, connectionId, identity, body.SenderSlackUserId), ct);
            }
            catch (SlackProviderInboxCapacityExceededException ex)
            {
                return ApiResults.Conflict(ex.Message, "slack_inbox_backpressured");
            }

            var launch = await launcher.LaunchConnectionAsync(agent, prompt, new ConnectionLaunchOrigin(
                connectionId, body.TeamId, body.SenderSlackUserId, body.ConversationId, body.MessageTs), ct);
            await inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
            await EnqueueReplyAsync(outbox, projectId, connection, body.ConversationId,
                accepted.AlreadyExisted ? "This task was already accepted; execution is being resumed." : "Task accepted and queued for execution.",
                launch.JobKey,
                ct);
            return ApiResults.Ok(new
            {
                kind = accepted.AlreadyExisted ? "queued" : "accepted",
                sessionId = launch.SessionId,
                jobKey = launch.JobKey,
                inputId = launch.InputId,
                turnId = launch.TurnId,
            });
        });

        group.MapPost("/adapter-session", async (
            HttpContext http,
            string connectionId,
            AdapterSessionBody body,
            AgentConnectionStore connections,
            ISecretStore secrets,
            OperatorCredential credential,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            var projectId = http.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");
            if (string.IsNullOrWhiteSpace(body?.AdapterId))
                return ApiResults.BadRequest("adapterId is required.");
            var appToken = await secrets.LoadAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.AppToken), ct);
            var botToken = await secrets.LoadAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.BotToken), ct);
            if (appToken is null || botToken is null)
                return ApiResults.Conflict("Configure both Slack credentials before starting the adapter.", "credentials_required");
            await connections.UpdateAsync(projectId, connectionId, new HashSet<string>(StringComparer.Ordinal) { "lastHeartbeatAt" },
                lastHeartbeatAt: time.GetUtcNow(), ct: ct);
            return ApiResults.Ok(new
            {
                adapterId = body.AdapterId,
                appToken = Encoding.UTF8.GetString(appToken),
                botToken = Encoding.UTF8.GetString(botToken),
            });
        });

        group.MapPost("/deliveries/claim", async (
            HttpContext http,
            string connectionId,
            DeliveryClaimBody body,
            SlackOutboxStore outbox,
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            var projectId = http.GetResolvedProject().Id;
            var entry = await outbox.ClaimAsync(projectId, connectionId, body?.AdapterId ?? string.Empty, ct);
            return entry is null ? ApiResults.Ok<object?>(null) : ApiResults.Ok(entry);
        });

        group.MapPost("/deliveries/ack", async (
            HttpContext http,
            DeliveryAckBody body,
            SlackOutboxStore outbox,
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            var projectId = http.GetResolvedProject().Id;
            if (body is null || string.IsNullOrWhiteSpace(body.Id))
                return ApiResults.BadRequest("id is required.");
            if (string.Equals(body.Outcome, "delivered", StringComparison.OrdinalIgnoreCase))
                await outbox.MarkDeliveredAsync(projectId, body.Id, ct);
            else if (string.Equals(body.Outcome, "uncertain", StringComparison.OrdinalIgnoreCase))
                await outbox.MarkDeliveryUncertainAsync(projectId, body.Id, body.Reason, ct);
            else
                await outbox.ScheduleRetryAsync(projectId, body.Id, body.Reason, ct);
            return ApiResults.Ok(new { id = body.Id, outcome = body.Outcome });
        });

        return app;
    }

    private static async Task EnqueueReplyAsync(
        SlackOutboxStore outbox,
        string projectId,
        Agent.Domain.AgentConnection connection,
        string conversationId,
        string text,
        string? dispatchRef,
        CancellationToken ct) =>
        await outbox.EnqueueAsync(new SlackOutboxDraft(
            projectId,
            connection.Id,
            connection.WorkspaceTeamId,
            conversationId,
            SlackOutboxKinds.UserAction,
            dispatchRef,
            JsonSerializer.Serialize(new { text })), ct);

    private static string RemoveBotMention(string text, string botUserId)
    {
        var result = text.Trim();
        if (!string.IsNullOrWhiteSpace(botUserId))
            result = result.Replace($"<@{botUserId}>", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return result;
    }
}

public sealed class SlackIngressBody
{
    public string EventType { get; init; } = "message";
    public bool IsDirectMessage { get; init; } = true;
    public string TeamId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string MessageTs { get; init; } = string.Empty;
    public string SenderSlackUserId { get; init; } = string.Empty;
    public string? Text { get; init; }
}

public sealed class AdapterSessionBody
{
    public string AdapterId { get; init; } = string.Empty;
}

public sealed class DeliveryClaimBody
{
    public string AdapterId { get; init; } = string.Empty;
}

public sealed class DeliveryAckBody
{
    public string Id { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? Reason { get; init; }
}
