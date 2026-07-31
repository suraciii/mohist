using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Security;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
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

        management.MapGet("/{connectionId}/diagnostic", async (
            HttpContext context,
            string connectionId,
            AgentConnectionStore connections,
            AgentQuerier agents,
            ISecretStore secrets,
            ISlackApiClient slack,
            SlackSetupVerifier verifier,
            CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");

            var ownerAvailability = await ProbeOwnerAvailabilityAsync(connection, secrets, slack, ct);
            var agent = await agents.GetByIdAsync(projectId, connection.AgentId);
            var agentReadiness = agent is null
                ? connection.AgentReadiness
                : AgentReadinessDeriver.Derive(agent.AgentConfig);
            var result = ConnectionDiagnostic.Compute(
                connection,
                new DiagnosticInputs(
                    verifier.IsAdapterOnline(connection),
                    ownerAvailability,
                    agentReadiness,
                    agent?.Name));
            return ApiResults.Ok(result);
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
            var projectId = context.GetResolvedProject().Id;
            var deleted = await connections.DeleteAsync(projectId, connectionId, ct);
            if (deleted is null) return ApiResults.NotFound("Slack Connection was not found.");
            return ApiResults.Ok(new
            {
                connection = deleted,
                slackAppRemovalNote = "Mohist-side records (credentials, inbox entries, conversation mappings, pending outbound deliveries, and owner claim codes) were removed. The Slack App remains installed on the workspace until a workspace admin uninstalls it manually.",
            });
        });

        management.MapPost("/{connectionId}/disable", async (HttpContext context, string connectionId, AgentConnectionStore connections, CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null) return ApiResults.NotFound("Slack Connection was not found.");
            if (connection.DesiredState == DesiredStateKind.Disabled)
                return ApiResults.Ok(connection);
            var updated = await connections.UpdateAsync(projectId, connectionId,
                new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
                desiredState: DesiredStateKind.Disabled, ct: ct);
            return updated is null ? ApiResults.NotFound("Slack Connection was not found.") : ApiResults.Ok(updated);
        });

        management.MapPost("/{connectionId}/enable", async (HttpContext context, string connectionId, AgentConnectionStore connections, CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null) return ApiResults.NotFound("Slack Connection was not found.");
            if (connection.DesiredState == DesiredStateKind.Enabled)
                return ApiResults.Ok(connection);
            var updated = await connections.UpdateAsync(projectId, connectionId,
                new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
                desiredState: DesiredStateKind.Enabled, ct: ct);
            return updated is null ? ApiResults.NotFound("Slack Connection was not found.") : ApiResults.Ok(updated);
        });

        management.MapPost("/{connectionId}/configure", async (HttpContext context, string connectionId, SlackCredentialsBody body, AgentConnectionStore connections, ISecretStore secrets, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.AppToken) || string.IsNullOrWhiteSpace(body.BotToken))
                return ApiResults.BadRequest("appToken and botToken are required.");
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null) return ApiResults.NotFound("Slack Connection was not found.");
            if (AgentConnectionStore.HasBoundIdentity(connection))
                return ApiResults.Conflict(
                    "Connection identity is already bound. Use rotate-credentials to update credentials.",
                    "use_rotate_credentials");
            await secrets.StoreAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.AppToken), Encoding.UTF8.GetBytes(body.AppToken), ct);
            await secrets.StoreAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.BotToken), Encoding.UTF8.GetBytes(body.BotToken), ct);
            var updated = await connections.UpdateAsync(projectId, connectionId,
                new HashSet<string>(StringComparer.Ordinal) { "setupProgress" },
                setupProgress: SetupProgressKind.WaitingForSlackService, ct: ct);
            return ApiResults.Ok(updated);
        });

        management.MapPost("/{connectionId}/rotate-credentials", async (HttpContext context, string connectionId, SlackCredentialsBody body, AgentConnectionStore connections, ISecretStore secrets, SlackSetupVerifier verifier, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.AppToken) || string.IsNullOrWhiteSpace(body.BotToken))
                return ApiResults.BadRequest("appToken and botToken are required.");
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null) return ApiResults.NotFound("Slack Connection was not found.");
            if (!AgentConnectionStore.HasBoundIdentity(connection))
                return ApiResults.BadRequest(
                    "Connection identity is not bound yet. Use configure to set up credentials.",
                    "identity_not_bound");

            var check = await verifier.VerifyRotationAsync(projectId, connectionId, body.AppToken, body.BotToken, ct);
            if (!check.Verified)
                return ApiResults.BadRequest(check.Reason ?? "Slack rejected the credentials.", "credential_verification_failed");

            if (!string.Equals(check.ResolvedTeamId, connection.WorkspaceTeamId, StringComparison.Ordinal)
                || !string.Equals(check.ResolvedAppId, connection.AppId, StringComparison.Ordinal)
                || !string.Equals(check.ResolvedBotUserId, connection.BotUserId, StringComparison.Ordinal))
                return ApiResults.BadRequest(
                    $"New tokens resolve to workspace/App/Bot '{check.ResolvedTeamId}/{check.ResolvedAppId}/{check.ResolvedBotUserId}', which does not match the bound identity. Rotation cannot rebind; create a new Connection instead.",
                    "credential_binding_mismatch");

            await secrets.StoreAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.AppToken), Encoding.UTF8.GetBytes(body.AppToken), ct);
            await secrets.StoreAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.BotToken), Encoding.UTF8.GetBytes(body.BotToken), ct);

            string? newHealth = null;
            var fields = new HashSet<string>(StringComparer.Ordinal)
            {
                "healthReason", "verifiedBotName", "verifiedBotIconUrl",
            };
            if (connection.ConnectionHealth == ConnectionHealthKind.Unhealthy
                && IsCredentialRelatedHealthReason(connection.HealthReason))
            {
                fields.Add("connectionHealth");
                newHealth = ConnectionHealthKind.Healthy;
            }
            await connections.UpdateAsync(projectId, connectionId, fields,
                healthReason: null,
                connectionHealth: newHealth,
                verifiedBotName: check.VerifiedBotName,
                verifiedBotIconUrl: check.VerifiedBotIconUrl,
                ct: ct);

            return ApiResults.Ok(await connections.GetAsync(projectId, connectionId, ct));
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

        management.MapPost("/{connectionId}/transfer-owner", async (HttpContext context, string connectionId, SlackOwnerClaimService claims, CancellationToken ct) =>
        {
            try
            {
                var code = await claims.GenerateAsync(
                    context.GetResolvedProject().Id,
                    connectionId,
                    Mohist.Server.Infrastructure.Data.Slack.SlackOwnerClaimCodeKinds.Transfer,
                    ct: ct);
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
            SlackDmSessionMappingStore mapping,
            AgentQuerier agents,
             IAgentLauncher launcher,
             IGrainFactory grains,
             AgentSessionFollowupDispatcher followupDispatcher,
             AgentSessionQuerier sessions,
             IHubContext<RunnerHub> runnerHub,
             RunnerConnectionTracker runnerConnections,
             OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            var projectId = http.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");
            if (connection.DesiredState == DesiredStateKind.Disabled)
                return ApiResults.Ok(new { kind = "rejected", reason = "This Connection is disabled." });
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
            if (decision.Kind == SlackInboundDecisionKind.Transferred)
            {
                await EnqueueReplyAsync(outbox, projectId, connection, body.ConversationId, "Owner transferred successfully.", null, ct);
                return ApiResults.Ok(new { kind = "transferred" });
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

            var isNewTask = TryStripNewTaskMarker(prompt, out var newTaskPrompt);
            if (isNewTask && string.IsNullOrWhiteSpace(newTaskPrompt))
            {
                const string reason = "Please send a task for the Agent to perform.";
                await EnqueueReplyAsync(outbox, projectId, connection, body.ConversationId, reason, null, ct);
                return ApiResults.Ok(new { kind = "rejected", reason });
            }

            TurnControlCommand controlCommand = default;
            var isControl = !isNewTask && TryGetTurnControlCommand(prompt, out controlCommand);
            AgentInfo? agent = null;
            AgentConnectionDispatchDecision? dispatchDecision = null;
            if (!isControl)
            {
                agent = await agents.GetByIdAsync(projectId, connection.AgentId);
                if (agent is null)
                    return ApiResults.Fail("The Agent bound to this Connection no longer exists.", 409, "agent_not_found");

                dispatchDecision = AgentConnectionDispatchDecision.For(
                    AgentReadinessDeriver.Derive(agent.AgentConfig));
                if (!dispatchDecision.Accepted)
                {
                    await EnqueueReplyAsync(outbox, projectId, connection, body.ConversationId,
                        dispatchDecision.Reason!, null, ct);
                    return ApiResults.Ok(new { kind = dispatchDecision.Kind, reason = dispatchDecision.Reason });
                }
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

            var route = await ResolveInboxRouteAsync(
                projectId,
                connectionId,
                body.ConversationId,
                isNewTask,
                isControl ? controlCommand : null,
                mapping,
                sessions,
                grains,
                inbox,
                accepted.Id,
                ct);

            if (route.Kind is SlackProviderInboxRouteKinds.Cancel or SlackProviderInboxRouteKinds.Stop)
            {
                var control = await ExecuteTurnControlAsync(
                    projectId,
                    route.Kind == SlackProviderInboxRouteKinds.Cancel ? TurnControlCommand.Cancel : TurnControlCommand.Stop,
                    route.SessionId!,
                    route.TurnId!,
                    sessions,
                    grains,
                    runnerHub,
                    runnerConnections,
                    ct);
                await EnqueueRequiredReplyAsync(
                    outbox,
                    projectId,
                    connection,
                    body.ConversationId,
                    control.Reply,
                    $"slack-ack:{identity.AsKey()}",
                    ct);
                await inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
                return ApiResults.Ok(new
                {
                    kind = control.Kind,
                    sessionId = control.SessionId,
                    turnId = control.TurnId,
                    control = true,
                });
            }

            if (route.Kind is SlackProviderInboxRouteKinds.NoActiveWork or SlackProviderInboxRouteKinds.AlreadyEnded)
            {
                var reply = route.Kind == SlackProviderInboxRouteKinds.AlreadyEnded
                    ? "That work has already ended; there is no active work to cancel or stop."
                    : "There is no active work to cancel or stop.";
                await EnqueueRequiredReplyAsync(outbox, projectId, connection, body.ConversationId,
                    reply,
                    $"slack-ack:{identity.AsKey()}",
                    ct);
                await inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
                return ApiResults.Ok(new { kind = route.Kind, control = true });
            }

            if (route.Kind is SlackProviderInboxRouteKinds.Launch or SlackProviderInboxRouteKinds.NewTaskLaunch)
            {
                var isRoutedNewTask = route.Kind == SlackProviderInboxRouteKinds.NewTaskLaunch;
                var sessionId = route.SessionId;
                AgentLaunchResult? launch = null;
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    launch = await launcher.LaunchConnectionAsync(
                        agent!,
                        isRoutedNewTask ? newTaskPrompt : prompt,
                        new ConnectionLaunchOrigin(
                            connectionId, body.TeamId, body.SenderSlackUserId, body.ConversationId, body.MessageTs),
                        ct);
                    sessionId = await inbox.SetRouteSessionIdAsync(projectId, accepted.Id, launch.SessionId, ct);
                }

                await mapping.SetCurrentSessionIdAsync(
                    projectId,
                    connectionId,
                    body.TeamId,
                    body.SenderSlackUserId,
                    body.ConversationId,
                    sessionId,
                    body.MessageTs,
                    ct);
                var acknowledgement = isRoutedNewTask
                    ? BuildNewTaskAck(accepted.AlreadyExisted, dispatchDecision!.Reason)
                    : accepted.AlreadyExisted
                        ? "This task was already accepted; execution is being resumed."
                        : dispatchDecision!.Reason ?? "Task accepted and queued for execution.";
                await EnqueueRequiredReplyAsync(outbox, projectId, connection, body.ConversationId,
                    acknowledgement,
                    $"slack-ack:{identity.AsKey()}",
                    ct);
                await inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
                return ApiResults.Ok(new
                {
                    kind = accepted.AlreadyExisted ? "queued" : "accepted",
                    sessionId,
                    jobKey = launch?.JobKey,
                    inputId = launch?.InputId,
                    turnId = launch?.TurnId,
                    newTask = isRoutedNewTask,
                });
            }

            var idempotencyKey = $"slack:{body.TeamId}:{body.ConversationId}:{body.MessageTs}";
            var followupResult = await RouteFollowupAsync(
                projectId,
                route.SessionId!,
                prompt,
                idempotencyKey,
                grains,
                followupDispatcher,
                ct);
            var followupAck = BuildFollowupAck(followupResult.Status, accepted.AlreadyExisted);
            await EnqueueRequiredReplyAsync(outbox, projectId, connection, body.ConversationId,
                followupAck,
                $"slack-ack:{identity.AsKey()}",
                ct);
            await inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
            return ApiResults.Ok(new
            {
                kind = followupResult.Kind,
                sessionId = followupResult.SessionId,
                inputId = followupResult.InputId,
                turnId = followupResult.TurnId,
                followup = true,
            });
        });

        group.MapPost("/adapter-session", async (
            HttpContext http,
            string connectionId,
            AdapterSessionBody body,
            AgentConnectionStore connections,
            ISecretStore secrets,
            SlackSetupVerifier verifier,
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
            if (connection.DesiredState == DesiredStateKind.Disabled)
                return ApiResults.Conflict("This Slack Connection is disabled.", "connection_disabled");
            if (string.IsNullOrWhiteSpace(body?.AdapterId))
                return ApiResults.BadRequest("adapterId is required.");
            var appToken = await secrets.LoadAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.AppToken), ct);
            var botToken = await secrets.LoadAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.BotToken), ct);
            if (appToken is null || botToken is null)
                return ApiResults.Conflict("Configure both Slack credentials before starting the adapter.", "credentials_required");
            await connections.UpdateAsync(projectId, connectionId, new HashSet<string>(StringComparer.Ordinal) { "lastHeartbeatAt" },
                lastHeartbeatAt: time.GetUtcNow(), ct: ct);
            await verifier.VerifyAsync(projectId, connectionId, ct);
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

    private static async Task EnqueueRequiredReplyAsync(
        SlackOutboxStore outbox,
        string projectId,
        Agent.Domain.AgentConnection connection,
        string conversationId,
        string text,
        string dispatchRef,
        CancellationToken ct) =>
        await outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
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

    /// <summary>
    /// Result of routing an inbound DM into the current session. The grain
    /// call's <see cref="AgentSessionFollowupAcceptResult"/> is normalized
    /// into a Slack-shaped kind plus the durable session/input/turn ids the
    /// caller needs for logging and the response payload. <see cref="Status"/>
    /// is one of <c>"queued"</c>, <c>"executing"</c>, or <c>"already_accepted"</c>
    /// (the values <see cref="BuildFollowupAck"/> consumes), not the
    /// underlying <see cref="Mohist.Server.Sessions.Domain.AgentTurnStatus"/>
    /// enum verbatim — the surface is the three-way follow-up verdict the
    /// ingress wants the Bot to read back.
    /// </summary>
    internal sealed record FollowupRouteResult(
        string Kind,
        string Status,
        string SessionId,
        string InputId,
        string TurnId);

    /// <summary>
    /// Routes a normal DM into the current session of the conversation
    /// rather than minting a new one. The session grain's idempotent
    /// accept is keyed by the Slack message identity (same format as the
    /// launch path so both layers of dedup collapse to one input); the
    /// dispatcher then pumps the queued turn the same way the HTTP
    /// follow-up route does. Exceptions raised by the grain during accept
    /// are translated to a deterministic Slack response kind so the
    /// ingress can post a coherent reply instead of crashing the call.
    /// </summary>
    private static async Task<FollowupRouteResult> RouteFollowupAsync(
        string projectId,
        string currentSessionId,
        string prompt,
        string idempotencyKey,
        IGrainFactory grains,
        AgentSessionFollowupDispatcher followupDispatcher,
        CancellationToken ct)
    {
        var grain = grains.GetGrain<IAgentSessionGrain>(currentSessionId);
        AgentSessionFollowupAcceptResult accept;
        try
        {
            accept = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: prompt,
                Source: "agent-session-followup",
                IdempotencyKey: idempotencyKey));
        }
        catch (RuntimeSessionMissingException)
        {
            return new FollowupRouteResult("runtime_session_missing", "rejected", currentSessionId, string.Empty, string.Empty);
        }
        catch (RecoveryOperationInProgressException)
        {
            return new FollowupRouteResult("recovery_in_progress", "rejected", currentSessionId, string.Empty, string.Empty);
        }
        catch (AgentSessionFollowupCapacityExceededException)
        {
            return new FollowupRouteResult("capacity_exceeded", "rejected", currentSessionId, string.Empty, string.Empty);
        }
        catch (StopOperationInProgressException)
        {
            return new FollowupRouteResult("stop_in_progress", "rejected", currentSessionId, string.Empty, string.Empty);
        }
        catch (SessionActivityUnknownException)
        {
            return new FollowupRouteResult("session_activity_unknown", "rejected", currentSessionId, string.Empty, string.Empty);
        }
        catch (FollowupConcurrencyLimitException)
        {
            return new FollowupRouteResult("concurrency_limit", "rejected", currentSessionId, string.Empty, string.Empty);
        }
        catch (InvalidOperationException)
        {
            return new FollowupRouteResult("followup_rejected", "rejected", currentSessionId, string.Empty, string.Empty);
        }

        await followupDispatcher.DispatchNextAsync(projectId, currentSessionId, ct);

        if (accept.AlreadyAccepted)
            return new FollowupRouteResult("already_accepted", "already_accepted", currentSessionId, accept.InputId, accept.TurnId);
        var status = accept.TurnStatus switch
        {
            AgentTurnStatus.Executing => "executing",
            _ => "queued",
        };
        return new FollowupRouteResult("accepted", status, currentSessionId, accept.InputId, accept.TurnId);
    }

    /// <summary>
    /// Translates the follow-up verdict into the Bot's reply text. The
    /// <c>alreadyExisted</c> flag from the inbox store takes priority so a
    /// redelivered Slack message still produces the original "already
    /// accepted" phrasing even when the grain call happens to land in a
    /// different state — the inbox dedup is the authoritative user-facing
    /// signal; the grain call is the second-layer idempotency.
    /// </summary>
    private static string BuildFollowupAck(string followupStatus, bool inboxAlreadyExisted)
    {
        if (inboxAlreadyExisted)
            return "This message was already accepted.";
        return followupStatus switch
        {
            "already_accepted" => "This message was already accepted.",
            "executing" => "Continuing. Running now.",
            "queued" => "Continuing. Will resume after the current step finishes.",
            _ => "Continuing.",
        };
    }

    /// <summary>
    /// Leading marker that the Owner uses to start a brand new task
    /// instead of continuing the DM conversation. Matched
    /// case-insensitively as a standalone leading token (followed by
    /// whitespace or end-of-string); see <see cref="TryStripNewTaskMarker"/>.
    /// </summary>
    internal const string NewTaskMarker = "new task";

    /// <summary>
    /// Detects the New task leading marker in a DM prompt. Returns true
    /// when the trimmed prompt starts with <see cref="NewTaskMarker"/>
    /// (case-insensitive) followed by whitespace or end-of-string; on
    /// success <paramref name="remaining"/> holds the trimmed text after
    /// the marker (which may be empty — the caller treats that as an
    /// empty-prompt rejection). The marker must be a standalone token:
    /// "new tasks foo" is NOT a New task command (no whitespace after
    /// "task"), only an Owner who explicitly types "new task" at the
    /// start of the message triggers the branch.
    /// </summary>
    internal static bool TryStripNewTaskMarker(string prompt, out string remaining)
    {
        remaining = string.Empty;
        if (string.IsNullOrEmpty(prompt))
            return false;
        var trimmed = prompt.TrimStart();
        if (trimmed.Length < NewTaskMarker.Length)
            return false;
        if (!trimmed.StartsWith(NewTaskMarker, StringComparison.OrdinalIgnoreCase))
            return false;
        var afterMarker = trimmed.Substring(NewTaskMarker.Length);
        if (afterMarker.Length > 0 && !char.IsWhiteSpace(afterMarker[0]))
            return false;
        remaining = afterMarker.TrimStart();
        return true;
    }

    /// <summary>
    /// Crafts the Bot's reply for the New task branch. Prefixes
    /// "Starting a new task." so the Owner can tell the new-work ack
    /// apart from the normal first-DM launch ack ("Task accepted and
    /// queued for execution.") and the follow-up ack ("Continuing.").
    /// The dispatch-decision reason supplies the operational detail
    /// (e.g. the Unknown-readiness explanation); a Ready agent falls
    /// back to the standard "Task accepted and queued for execution."
    /// phrasing. A redelivered New task message replies with the
    /// already-accepted variant just like the other branches.
    /// </summary>
    internal static string BuildNewTaskAck(bool inboxAlreadyExisted, string? dispatchDecisionReason)
    {
        if (inboxAlreadyExisted)
            return "This new task was already accepted; execution is being resumed.";
        var detail = !string.IsNullOrWhiteSpace(dispatchDecisionReason)
            ? dispatchDecisionReason
            : "Task accepted and queued for execution.";
        return "Starting a new task. " + detail;
    }

    private enum TurnControlCommand
    {
        Cancel,
        Stop,
    }

    private sealed record TurnControlReply(
        string Kind,
        string Reply,
        string? SessionId = null,
        string? TurnId = null);

    private static bool TryGetTurnControlCommand(string prompt, out TurnControlCommand command)
    {
        command = default;
        var trimmed = prompt.TrimStart();
        if (StartsWithStandaloneKeyword(trimmed, "cancel"))
        {
            command = TurnControlCommand.Cancel;
            return true;
        }
        if (StartsWithStandaloneKeyword(trimmed, "stop"))
        {
            command = TurnControlCommand.Stop;
            return true;
        }
        return false;
    }

    private static bool StartsWithStandaloneKeyword(string text, string keyword) =>
        text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
        && (text.Length == keyword.Length || char.IsWhiteSpace(text[keyword.Length]));

    private static async Task<SlackProviderInboxRoute> ResolveInboxRouteAsync(
        string projectId,
        string connectionId,
        string conversationId,
        bool isNewTask,
        TurnControlCommand? controlCommand,
        SlackDmSessionMappingStore mapping,
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        SlackProviderInboxStore inbox,
        string inboxId,
        CancellationToken ct)
    {
        var existing = await inbox.GetRouteAsync(projectId, inboxId, ct);
        if (existing is not null)
            return existing;

        if (isNewTask)
            return await inbox.GetOrAssignRouteAsync(
                projectId, inboxId, new(SlackProviderInboxRouteKinds.NewTaskLaunch), ct);

        var sessionId = await mapping.GetCurrentSessionIdAsync(projectId, connectionId, conversationId, ct);
        if (controlCommand is null)
            return await inbox.GetOrAssignRouteAsync(
                projectId,
                inboxId,
                string.IsNullOrWhiteSpace(sessionId)
                    ? new(SlackProviderInboxRouteKinds.Launch)
                    : new(SlackProviderInboxRouteKinds.Followup, sessionId),
                ct);

        if (string.IsNullOrWhiteSpace(sessionId))
            return await inbox.GetOrAssignRouteAsync(
                projectId, inboxId, new(SlackProviderInboxRouteKinds.NoActiveWork), ct);

        var target = await sessions.ResolveCancelTargetAsync(projectId, sessionId, ct);
        if (target is null)
            return await inbox.GetOrAssignRouteAsync(
                projectId, inboxId, new(SlackProviderInboxRouteKinds.NoActiveWork), ct);

        var current = await grains.GetGrain<IAgentSessionGrain>(target.SessionId).ResolveCurrentTurnControlAsync();
        if (current is null)
        {
            var turns = await grains.GetGrain<IAgentSessionGrain>(target.SessionId).ListTurnsAsync();
            return await inbox.GetOrAssignRouteAsync(
                projectId,
                inboxId,
                new(turns.Count == 0 ? SlackProviderInboxRouteKinds.NoActiveWork : SlackProviderInboxRouteKinds.AlreadyEnded),
                ct);
        }

        return await inbox.GetOrAssignRouteAsync(
            projectId,
            inboxId,
            controlCommand == TurnControlCommand.Cancel
                ? new(SlackProviderInboxRouteKinds.Cancel, target.SessionId, current.TurnId)
                : new(SlackProviderInboxRouteKinds.Stop, target.SessionId, current.TurnId),
            ct);
    }

    private static async Task<TurnControlReply> ExecuteTurnControlAsync(
        string projectId,
        TurnControlCommand command,
        string sessionId,
        string turnId,
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        IHubContext<RunnerHub> runnerHub,
        RunnerConnectionTracker runnerConnections,
        CancellationToken ct)
    {
        var target = await sessions.ResolveCancelTargetAsync(projectId, sessionId, ct);
        if (target is null)
            return new("no_active_work", "There is no active work to cancel or stop.", sessionId);

        if (command == TurnControlCommand.Cancel)
        {
            var result = await AgentSessionTurnControlOperations.CancelAsync(
                grains, target.SessionId, turnId);
            return result.Kind switch
            {
                TurnControlResultKind.Cancelled => new(
                    "cancelled", "Work cancelled.", target.SessionId, turnId),
                TurnControlResultKind.Executing => new(
                    "executing", "The work is running; use stop to request a runtime stop.", target.SessionId, turnId),
                TurnControlResultKind.AlreadyEnded => new(
                    "already_ended", "That work has already ended.", target.SessionId, turnId),
                _ => new(
                    "no_active_work", "There is no active work to cancel or stop.", target.SessionId, turnId),
            };
        }

        var stop = await AgentSessionTurnControlOperations.StopAsync(
            projectId,
            grains,
            runnerHub,
            runnerConnections,
            target,
            turnId,
            ct);
        return stop.Kind switch
        {
            TurnControlResultKind.Stopped => new(
                "stopped", "Work stopped.", target.SessionId, turnId),
            TurnControlResultKind.Unknown => new(
                "unknown", "Stop requested, but the runtime could not confirm it.", target.SessionId, turnId),
            TurnControlResultKind.StopRequested => new(
                "stop_requested", "Stop requested.", target.SessionId, turnId),
            TurnControlResultKind.NotCancellable => new(
                "not_cancellable", "The runtime reported that this work could not be stopped.", target.SessionId, turnId),
            TurnControlResultKind.Queued => new(
                "queued", "The work is queued; use cancel to cancel it.", target.SessionId, turnId),
            TurnControlResultKind.AlreadyEnded => new(
                "already_ended", "That work has already ended.", target.SessionId, turnId),
            TurnControlResultKind.RunnerUnavailable => new(
                "runner_unavailable", "Stop could not be requested because the Runner is unavailable.", target.SessionId, turnId),
            _ => new(
                "no_active_work", "There is no active work to cancel or stop.", target.SessionId, turnId),
        };
    }

    internal static async Task<string> ProbeOwnerAvailabilityAsync(
        AgentConnection connection,
        ISecretStore secrets,
        ISlackApiClient slack,
        CancellationToken ct)
    {
        if (connection.OwnerSlackUserId is null)
            return OwnerAvailabilityKind.NotConfigured;

        var token = await secrets.LoadAsync(
            new SecretStoreAddress(connection.ProjectId, connection.Id, SecretKind.BotToken), ct);
        if (token is null || token.Length == 0)
            return OwnerAvailabilityKind.Unknown;

        try
        {
            var response = await slack.UsersInfoAsync(
                connection.OwnerSlackUserId,
                Encoding.UTF8.GetString(token),
                ct);
            return SlackOwnerClaimService.IsEligibleMember(
                response,
                connection.WorkspaceTeamId,
                connection.OwnerSlackUserId)
                ? OwnerAvailabilityKind.Available
                : OwnerAvailabilityKind.Unavailable;
        }
        catch (HttpRequestException)
        {
            return OwnerAvailabilityKind.Unknown;
        }
    }

    private static bool IsCredentialRelatedHealthReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        if (reason.Contains("token", StringComparison.OrdinalIgnoreCase)) return true;
        if (reason.Contains("scope", StringComparison.OrdinalIgnoreCase)) return true;
        if (reason.Contains("credential", StringComparison.OrdinalIgnoreCase)) return true;
        if (reason.Contains("App and Bot", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
