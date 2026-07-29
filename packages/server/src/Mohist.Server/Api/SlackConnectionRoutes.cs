using System.Text;
using System.Text.Json;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Security;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack;
using Mohist.Server.Agent.Domain;

namespace Mohist.Server.Api;

public static class SlackConnectionRoutes
{
    public static WebApplication MapSlackConnectionRoutes(this WebApplication app)
    {
        var management = app.MapGroup("/api/projects/{projectRef}/slack-connections")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        management.MapPost("/", async (HttpContext context, SlackConnectionCreateBody body, AgentConnectionStore connections, CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            if (body is null || string.IsNullOrWhiteSpace(body.AgentId))
                return ApiResults.BadRequest("agentId is required.");

            var connection = new AgentConnection
            {
                Id = $"connection_{Guid.NewGuid():N}",
                ProjectId = projectId,
                AgentId = body.AgentId.Trim(),
                ProviderKind = ConnectionProviderKind.Slack,
                WorkspaceTeamId = string.Empty,
                AppId = string.Empty,
                BotUserId = string.Empty,
                BotName = body.BotName?.Trim() ?? string.Empty,
                AvatarHash = body.AvatarHash,
            };
            try
            {
                var created = await connections.CreateAsync(connection, ct);
                return Results.Json(new ApiResponse<object>(true, new { connection = created, slackAppCreationReference = "https://api.slack.com/apps?new_app=1" }), statusCode: 201);
            }
            catch (AgentConnectionDuplicateException ex)
            {
                return ApiResults.Conflict(ex.Message, "connection_duplicate");
            }
            catch (AgentConnectionValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, ex.Code);
            }
        });

        management.MapGet("/", async (HttpContext context, AgentConnectionStore connections, CancellationToken ct) =>
            ApiResults.Ok(await connections.ListAsync(context.GetResolvedProject().Id, ct: ct)));

        app.MapGet("/api/slack-connections/adapter", async (
            HttpContext http,
            AgentConnectionStore connections,
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            return ApiResults.Ok(await connections.ListForAdapterAsync(ct));
        });

        management.MapGet("/{connectionId}", async (HttpContext context, string connectionId, AgentConnectionStore connections, CancellationToken ct) =>
        {
            var connection = await connections.GetAsync(context.GetResolvedProject().Id, connectionId, ct);
            return connection is null ? ApiResults.NotFound("Slack Connection was not found.") : ApiResults.Ok(connection);
        });

        management.MapPatch("/{connectionId}", async (HttpContext context, string connectionId, SlackConnectionEditBody body, AgentConnectionStore connections, CancellationToken ct) =>
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            if (body.BotName is not null) fields.Add("botName");
            if (body.AvatarHash is not null) fields.Add("avatarHash");
            if (fields.Count == 0) return ApiResults.BadRequest("At least one editable field is required.");
            var updated = await connections.UpdateAsync(context.GetResolvedProject().Id, connectionId, fields, body.BotName, body.AvatarHash, ct: ct);
            return updated is null ? ApiResults.NotFound("Slack Connection was not found.") : ApiResults.Ok(updated);
        });

        management.MapDelete("/{connectionId}", async (HttpContext context, string connectionId, AgentConnectionStore connections, CancellationToken ct) =>
        {
            var deleted = await connections.DeleteAsync(context.GetResolvedProject().Id, connectionId, ct);
            return deleted is null ? ApiResults.NotFound("Slack Connection was not found.") : ApiResults.Ok(deleted);
        });

        management.MapPost("/{connectionId}/configure", async (HttpContext context, string connectionId, SlackCredentialsBody body, AgentConnectionStore connections, ISecretStore secrets, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.AppToken) || string.IsNullOrWhiteSpace(body.BotToken))
                return ApiResults.BadRequest("appToken and botToken are required.");
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null) return ApiResults.NotFound("Slack Connection was not found.");
            await secrets.StoreAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.AppToken), Encoding.UTF8.GetBytes(body.AppToken), ct);
            await secrets.StoreAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.BotToken), Encoding.UTF8.GetBytes(body.BotToken), ct);
            var updated = await connections.UpdateAsync(projectId, connectionId,
                new HashSet<string>(StringComparer.Ordinal) { "setupProgress" },
                setupProgress: SetupProgressKind.WaitingForSlackService, ct: ct);
            return ApiResults.Ok(updated);
        });

        management.MapPost("/{connectionId}/claim-owner", async (HttpContext context, string connectionId, SlackOwnerClaimService claims, CancellationToken ct) =>
        {
            try
            {
                var code = await claims.GenerateAsync(context.GetResolvedProject().Id, connectionId, ct: ct);
                return ApiResults.Ok(new { code = code.Value, expiresAt = code.ExpiresAt });
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "claim_unavailable");
            }
        });

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

            var dispatchDecision = AgentConnectionDispatchDecision.For(
                AgentReadinessDeriver.Derive(agent.AgentConfig));
            if (!dispatchDecision.Accepted)
            {
                await EnqueueReplyAsync(outbox, projectId, connection, body.ConversationId,
                    dispatchDecision.Reason!, null, ct);
                return ApiResults.Ok(new { kind = dispatchDecision.Kind, reason = dispatchDecision.Reason });
            }

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
            var acknowledgement = accepted.AlreadyExisted
                ? "This task was already accepted; execution is being resumed."
                : dispatchDecision.Reason ?? "Task accepted and queued for execution.";
            await EnqueueReplyAsync(outbox, projectId, connection, body.ConversationId,
                acknowledgement,
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

public sealed class SlackConnectionCreateBody
{
    public string AgentId { get; init; } = string.Empty;
    public string WorkspaceTeamId { get; init; } = string.Empty;
    public string AppId { get; init; } = string.Empty;
    public string BotUserId { get; init; } = string.Empty;
    public string? BotName { get; init; }
    public string? AvatarHash { get; init; }
}

public sealed class SlackConnectionEditBody
{
    public string? BotName { get; init; }
    public string? AvatarHash { get; init; }
}

public sealed class SlackCredentialsBody
{
    public string AppToken { get; init; } = string.Empty;
    public string BotToken { get; init; } = string.Empty;
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
