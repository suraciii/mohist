using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
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
    private static readonly Regex SlackMentionToken = new(
        @"<@(?<id>[A-Za-z0-9_-]+)(?:\|[^>]*)?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            SlackThreadSessionMappingStore threadMapping,
            SlackAmbiguousPromptStore ambiguousPrompts,
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
            if (body is null)
                return ApiResults.Ok(new { kind = "ignored" });
            if (!string.Equals(body.TeamId, connection.WorkspaceTeamId, StringComparison.Ordinal))
                return ApiResults.BadRequest("The Slack workspace does not match this Connection.", "workspace_mismatch");

            var identity = new SlackMessageIdentity(body.TeamId, body.ConversationId, body.MessageTs);
            var identityError = identity.Validate();
            if (identityError.Length != 0)
                return ApiResults.BadRequest(identityError, "invalid_slack_identity");

            var senderKind = NormalizeSenderKind(body.SenderKind);
            if (senderKind is SlackSenderKind.Bot or SlackSenderKind.Unknown)
                return ApiResults.Ok(new { kind = "ignored" });
            if (string.IsNullOrWhiteSpace(body.SenderSlackUserId))
                return ApiResults.Ok(new { kind = "ignored" });
            var senderSlackUserId = body.SenderSlackUserId!.Trim();

            if (!body.IsDirectMessage)
                return await HandleChannelIngressAsync(
                    HandleChannelIngressRequest.From(
                        projectId, connection, identity, senderSlackUserId, body,
                        connections, threadMapping, ambiguousPrompts,
                        sessions, agents, claims, inbox, outbox,
                        launcher, grains, followupDispatcher,
                        runnerHub, runnerConnections,
                        http.RequestServices),
                    ct);

            return await HandleDmIngressAsync(
                HandleDmIngressRequest.From(
                    projectId, connection, identity, senderSlackUserId, body,
                    mapping, sessions, agents, claims, inbox, outbox,
                    launcher, grains, followupDispatcher,
                    runnerHub, runnerConnections,
                    http.RequestServices),
                ct);
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
        CancellationToken ct,
        string? threadTs = null) =>
        await outbox.EnqueueAsync(new SlackOutboxDraft(
            projectId,
            connection.Id,
            connection.WorkspaceTeamId,
            conversationId,
            SlackOutboxKinds.UserAction,
            dispatchRef,
            JsonSerializer.Serialize(new { text }),
            threadTs), ct);

    private static async Task EnqueueRequiredReplyAsync(
        SlackOutboxStore outbox,
        string projectId,
        Agent.Domain.AgentConnection connection,
        string conversationId,
        string text,
        string dispatchRef,
        CancellationToken ct,
        string? threadTs = null) =>
        await outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
            projectId,
            connection.Id,
            connection.WorkspaceTeamId,
            conversationId,
            SlackOutboxKinds.UserAction,
            dispatchRef,
            JsonSerializer.Serialize(new { text }),
            threadTs), ct);

    private static string RemoveBotMention(string text, string botUserId)
    {
        if (string.IsNullOrWhiteSpace(botUserId))
            return text.Trim();

        return SlackMentionToken.Replace(text.Trim(), match =>
            string.Equals(match.Groups["id"].Value, botUserId, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : match.Value).Trim();
    }

    private static AgentSessionInputProvenance BuildSlackInputProvenance(
        string connectionId,
        SlackIngressBody body,
        string? threadTs) =>
        new(
            ProviderKind: "slack",
            WorkspaceId: body.TeamId,
            ConversationId: body.ConversationId,
            ThreadId: threadTs,
            MemberId: body.SenderSlackUserId!,
            MessageId: body.MessageTs,
            ConnectionId: connectionId);

    /// <summary>
    /// Sender kind surfaced by the adapter on the normalized envelope.
    /// The adapter sets <see cref="Bot"/> for Slack Bot subtype /
    /// <c>bot_id</c> events, <see cref="Unknown"/> when a stable user
    /// id is absent, and <see cref="Human"/> otherwise. A missing
    /// <c>SenderKind</c> field with a stable user id falls back to
    /// <see cref="Human"/> so existing DM callers keep working; the
    /// adapter populates the explicit value on every new envelope.
    /// </summary>
    internal enum SlackSenderKind
    {
        Human,
        Bot,
        Unknown,
    }

    /// <summary>
    /// Normalizes the envelope's <c>SenderKind</c> field. Returns
    /// <see cref="SlackSenderKind.Unknown"/> for a missing user id
    /// (matches the adapter's "stable identity absent" decision), and
    /// <see cref="SlackSenderKind.Human"/> when <c>SenderKind</c> is
    /// absent but a stable user id is present so legacy DM callers
    /// are not regressed.
    /// </summary>
    private static SlackSenderKind NormalizeSenderKind(string? rawKind)
    {
        var normalized = rawKind?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "bot" => SlackSenderKind.Bot,
            "unknown" => SlackSenderKind.Unknown,
            _ => SlackSenderKind.Human,
        };
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
        AgentSessionInputProvenance provenance,
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
                IdempotencyKey: idempotencyKey,
                Provenance: provenance));
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

    private static async Task<SlackProviderInboxRouteDraft> ResolveInboxRouteDraftAsync(
        string projectId,
        string connectionId,
        string conversationId,
        bool isNewTask,
        TurnControlCommand? controlCommand,
        SlackDmSessionMappingStore mapping,
        AgentSessionQuerier sessions,
        IGrainFactory grains,
        CancellationToken ct)
    {
        if (isNewTask)
            return new(SlackProviderInboxRouteKinds.NewTaskLaunch);

        var sessionId = await mapping.GetCurrentSessionIdAsync(projectId, connectionId, conversationId, ct);
        if (controlCommand is null)
            return string.IsNullOrWhiteSpace(sessionId)
                ? new(SlackProviderInboxRouteKinds.Launch)
                : new(SlackProviderInboxRouteKinds.Followup, sessionId);

        if (string.IsNullOrWhiteSpace(sessionId))
            return new(SlackProviderInboxRouteKinds.NoActiveWork);

        var target = await sessions.ResolveCancelTargetAsync(projectId, sessionId, ct);
        if (target is null)
            return new(SlackProviderInboxRouteKinds.NoActiveWork);

        var current = await grains.GetGrain<IAgentSessionGrain>(target.SessionId).ResolveCurrentTurnControlAsync();
        if (current is null)
        {
            var turns = await grains.GetGrain<IAgentSessionGrain>(target.SessionId).ListTurnsAsync();
            return new(turns.Count == 0 ? SlackProviderInboxRouteKinds.NoActiveWork : SlackProviderInboxRouteKinds.AlreadyEnded);
        }

        return controlCommand == TurnControlCommand.Cancel
            ? new(SlackProviderInboxRouteKinds.Cancel, target.SessionId, current.TurnId)
            : new(SlackProviderInboxRouteKinds.Stop, target.SessionId, current.TurnId);
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

    /// <summary>
    /// Routes the DM branch of the Slack ingress. Owner-claim / transfer
    /// detection runs first (handled by <c>SlackOwnerClaimService</c>);
    /// the message then either starts a new AgentJob + Session via
    /// <c>LaunchConnectionAsync</c>, continues the current DM session,
    /// or is rejected because the prompt is empty / the Agent is not
    /// ready / the user asked to cancel or stop. Idempotent under
    /// Slack redelivery: the inbox dedups the message identity, and the
    /// session grain + AgentJob grain collapse replays onto the same
    /// session.
    /// </summary>
    private static async Task<IResult> HandleDmIngressAsync(HandleDmIngressRequest req, CancellationToken ct)
    {
        var body = req.Body;
        var projectId = req.ProjectId;
        var connection = req.Connection;

        var decision = await req.Claims.HandleInboundDmAsync(
            projectId,
            connection.Id,
            new SlackInboundDm(req.SenderSlackUserId, body.Text ?? string.Empty),
            ct);
        if (decision.Kind == SlackInboundDecisionKind.Claimed)
        {
            await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, "Owner claimed successfully.", null, ct, body.ThreadTs);
            return ApiResults.Ok(new { kind = "claimed" });
        }
        if (decision.Kind == SlackInboundDecisionKind.Transferred)
        {
            await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, "Owner transferred successfully.", null, ct, body.ThreadTs);
            return ApiResults.Ok(new { kind = "transferred" });
        }
        if (decision.Kind == SlackInboundDecisionKind.Rejected)
        {
            await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, decision.Reason ?? "The message was rejected.", null, ct, body.ThreadTs);
            return ApiResults.Ok(new { kind = "rejected", reason = decision.Reason });
        }

        if (IsBackpressured(connection))
            return ApiResults.Conflict(
                "This Slack Connection is backpressured; retry after pending deliveries drain.",
                "slack_backpressured");

        var prompt = RemoveBotMention(body.Text ?? string.Empty, connection.BotUserId);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            const string reason = "Please send a task for the Agent to perform.";
            await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
            return ApiResults.Ok(new { kind = "rejected", reason });
        }

        var isNewTask = TryStripNewTaskMarker(prompt, out var newTaskPrompt);
        if (isNewTask && string.IsNullOrWhiteSpace(newTaskPrompt))
        {
            const string reason = "Please send a task for the Agent to perform.";
            await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
            return ApiResults.Ok(new { kind = "rejected", reason });
        }

        TurnControlCommand controlCommand = default;
        var isControl = !isNewTask && TryGetTurnControlCommand(prompt, out controlCommand);
        AgentInfo? agent = null;
        AgentConnectionDispatchDecision? dispatchDecision = null;
        if (!isControl)
        {
            agent = await req.Agents.GetByIdAsync(projectId, connection.AgentId);
            if (agent is null)
                return ApiResults.Fail("The Agent bound to this Connection no longer exists.", 409, "agent_not_found");

            dispatchDecision = AgentConnectionDispatchDecision.For(
                AgentReadinessDeriver.Derive(agent.AgentConfig));
            if (!dispatchDecision.Accepted)
            {
                await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId,
                    dispatchDecision.Reason!, null, ct, body.ThreadTs);
                return ApiResults.Ok(new { kind = dispatchDecision.Kind, reason = dispatchDecision.Reason });
            }
        }

        var routeDraft = await ResolveInboxRouteDraftAsync(
            projectId,
            connection.Id,
            body.ConversationId,
            isNewTask,
            isControl ? controlCommand : null,
            req.DmMapping,
            req.Sessions,
            req.Grains,
            ct);

        SlackProviderInboxAcceptResult accepted;
        try
        {
            accepted = await req.Inbox.AcceptAsync(new SlackProviderInboxDraft(
                projectId, connection.Id, req.Identity, req.SenderSlackUserId, body.ThreadTs), routeDraft, ct);
        }
        catch (SlackProviderInboxCapacityExceededException ex)
        {
            return ApiResults.Conflict(ex.Message, "slack_inbox_backpressured");
        }

        var route = await req.Inbox.GetRouteAsync(projectId, accepted.Id, ct);

        if (route.Kind is SlackProviderInboxRouteKinds.Cancel or SlackProviderInboxRouteKinds.Stop)
        {
            var control = await ExecuteTurnControlAsync(
                projectId,
                route.Kind == SlackProviderInboxRouteKinds.Cancel ? TurnControlCommand.Cancel : TurnControlCommand.Stop,
                route.SessionId!,
                route.TurnId!,
                req.Sessions,
                req.Grains,
                req.RunnerHub,
                req.RunnerConnections,
                ct);
            await EnqueueRequiredReplyAsync(
                req.Outbox,
                projectId,
                connection,
                body.ConversationId,
                control.Reply,
                $"slack-ack:{req.Identity.AsKey()}",
                ct,
                body.ThreadTs);
            await req.Inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
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
            await EnqueueRequiredReplyAsync(req.Outbox, projectId, connection, body.ConversationId,
                reply,
                $"slack-ack:{req.Identity.AsKey()}",
                ct,
                body.ThreadTs);
            await req.Inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
            return ApiResults.Ok(new { kind = route.Kind, control = true });
        }

        if (route.Kind is SlackProviderInboxRouteKinds.Launch or SlackProviderInboxRouteKinds.NewTaskLaunch)
        {
            var isRoutedNewTask = route.Kind == SlackProviderInboxRouteKinds.NewTaskLaunch;
            var sessionId = route.SessionId;
            AgentLaunchResult? launch = null;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                launch = await req.Launcher.LaunchConnectionAsync(
                    agent!,
                    isRoutedNewTask ? newTaskPrompt : prompt,
                    new ConnectionLaunchOrigin(
                        connection.Id, body.TeamId, req.SenderSlackUserId, body.ConversationId, body.MessageTs, body.ThreadTs),
                    ct);
                sessionId = await req.Inbox.SetRouteSessionIdAsync(projectId, accepted.Id, launch.SessionId, ct);
            }

            await req.DmMapping.SetCurrentSessionIdAsync(
                projectId,
                connection.Id,
                body.TeamId,
                req.SenderSlackUserId,
                body.ConversationId,
                sessionId,
                body.MessageTs,
                ct);
            var acknowledgement = isRoutedNewTask
                ? BuildNewTaskAck(accepted.AlreadyExisted, dispatchDecision!.Reason)
                : accepted.AlreadyExisted
                    ? "This task was already accepted; execution is being resumed."
                    : dispatchDecision!.Reason ?? "Task accepted and queued for execution.";
            await EnqueueRequiredReplyAsync(req.Outbox, projectId, connection, body.ConversationId,
                acknowledgement,
                $"slack-ack:{req.Identity.AsKey()}",
                ct,
                body.ThreadTs);
            await req.Inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
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
            BuildSlackInputProvenance(connection.Id, body, body.ThreadTs),
            req.Grains,
            req.FollowupDispatcher,
            ct);
        var followupAck = BuildFollowupAck(followupResult.Status, accepted.AlreadyExisted);
        await EnqueueRequiredReplyAsync(req.Outbox, projectId, connection, body.ConversationId,
            followupAck,
            $"slack-ack:{req.Identity.AsKey()}",
            ct,
            body.ThreadTs);
        await req.Inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
        return ApiResults.Ok(new
        {
            kind = followupResult.Kind,
            sessionId = followupResult.SessionId,
            inputId = followupResult.InputId,
            turnId = followupResult.TurnId,
            followup = true,
        });
    }

    /// <summary>
    /// Owner-only channel state machine. Classifies the message BEFORE
    /// the inbox row is written (514 D5 principle): Bot/unknown senders
    /// and plain unbound-channel messages return without persisting an
    /// inbox row. A binding lookup is reconciled from the inbox route
    /// or Session provenance when missing, so a launch that crashed
    /// between <c>LaunchConnectionAsync</c> and <c>BindAsync</c> still
    /// routes subsequent thread replies to the original session.
    /// <para>
    /// Workspace-scoped multi-Agent attribution (D4) and the once-only
    /// ambiguity prompt (D5) live here. Mention parsing yields the
    /// ordered list of stable Slack user ids the adapter extracted; the
    /// state machine intersects them with the workspace's identity-bound
    /// Bots (<c>M ∩ W</c>) so arbitrary human mentions are never
    /// treated as Bot mentions.
    /// </para>
    /// </summary>
    private static async Task<IResult> HandleChannelIngressAsync(HandleChannelIngressRequest req, CancellationToken ct)
    {
        var body = req.Body;
        var projectId = req.ProjectId;
        var connection = req.Connection;

        if (IsBackpressured(connection))
            return ApiResults.Conflict(
                "This Slack Connection is backpressured; retry after pending deliveries drain.",
                "slack_backpressured");

        var rootTs = !string.IsNullOrWhiteSpace(body.ThreadTs) ? body.ThreadTs : body.MessageTs;
        var mentionedUserIds = BuildMentionedBotIds(body.MentionedUserIds);
        var ownBotUserId = connection.BotUserId ?? string.Empty;

        var workspaceBots = await req.Connections.ListBoundBotsByWorkspaceAsync(body.TeamId, ct);
        var mentionedWorkspaceBots = MentionedWorkspaceBots(mentionedUserIds, workspaceBots);
        var threadBindings = await req.ThreadMapping.ListBindingsByWorkspaceAsync(
            body.TeamId, body.ConversationId, rootTs, ct);

        if (mentionedWorkspaceBots.Count >= 2)
        {
            var mentionedConnectionIds = mentionedWorkspaceBots
                .Select(bot => bot.ConnectionId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (!mentionedConnectionIds.Contains(connection.Id, StringComparer.Ordinal))
                return ApiResults.Ok(new { kind = "ignored" });
            if (!string.Equals(req.SenderSlackUserId, connection.OwnerSlackUserId, StringComparison.Ordinal))
                return await RejectNonOwnerChannelMessageAsync(req, ct);
            return await HandleAmbiguousPromptAsync(
                req,
                mentionedWorkspaceBots.Select(b => b.BotUserId).ToArray(),
                mentionedConnectionIds,
                ct);
        }

        if (mentionedWorkspaceBots.Count == 1)
        {
            var addressedBot = mentionedWorkspaceBots[0];
            if (!string.Equals(addressedBot.BotUserId, ownBotUserId, StringComparison.Ordinal))
                return ApiResults.Ok(new { kind = "ignored" });

            if (!string.Equals(req.SenderSlackUserId, connection.OwnerSlackUserId, StringComparison.Ordinal))
            {
                const string reason = "This Slack Connection is available only to its owner.";
                await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                return ApiResults.Ok(new { kind = "rejected", reason });
            }

            var prompt = RemoveBotMention(body.Text ?? string.Empty, ownBotUserId);
            var isRootMention = string.IsNullOrWhiteSpace(body.ThreadTs);

            var ownBinding = threadBindings.FirstOrDefault(
                binding => string.Equals(binding.ConnectionId, connection.Id, StringComparison.Ordinal));
            var otherBotsInThread = threadBindings.Any(
                binding => !string.Equals(binding.ConnectionId, connection.Id, StringComparison.Ordinal));

            if (ownBinding is not null && !isRootMention)
                return await DispatchChannelFollowupAsync(req, ownBinding.SessionId, prompt, ct);

            if (isRootMention)
            {
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    const string reason = "Please send a task for the Agent to perform.";
                    await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                    return ApiResults.Ok(new { kind = "rejected", reason });
                }
                return await LaunchChannelRootAsync(req, prompt, rootTs, ct);
            }

            if (otherBotsInThread)
            {
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    const string reason = "Please send a task for the Agent to perform.";
                    await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                    return ApiResults.Ok(new { kind = "rejected", reason });
                }
                return await LaunchChannelRootAsync(req, prompt, rootTs, ct);
            }

            var reconciled = await ReconcileSessionIdAsync(
                req, projectId, body.TeamId, body.ConversationId, rootTs, ct);
            if (reconciled is not null)
                return await DispatchChannelFollowupAsync(req, reconciled, prompt, ct);

            if (string.IsNullOrWhiteSpace(prompt))
            {
                const string reason = "Please send a task for the Agent to perform.";
                await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                return ApiResults.Ok(new { kind = "rejected", reason });
            }
            return await LaunchChannelRootAsync(req, prompt, rootTs, ct);
        }

        if (threadBindings.Count >= 2)
        {
            var bindingConnectionIds = threadBindings
                .Select(binding => binding.ConnectionId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (!bindingConnectionIds.Contains(connection.Id, StringComparer.Ordinal))
                return ApiResults.Ok(new { kind = "ignored" });
            if (!string.Equals(req.SenderSlackUserId, connection.OwnerSlackUserId, StringComparison.Ordinal))
                return await RejectNonOwnerChannelMessageAsync(req, ct);
            var botLookup = workspaceBots.ToDictionary(b => b.ConnectionId, b => b.BotUserId, StringComparer.Ordinal);
            var botLabels = threadBindings
                .Select(binding => botLookup.TryGetValue(binding.ConnectionId, out var label) ? label : binding.ConnectionId)
                .ToArray();
            return await HandleAmbiguousPromptAsync(req, botLabels, bindingConnectionIds, ct);
        }

        if (threadBindings.Count == 1)
        {
            var binding = threadBindings[0];
            if (!string.Equals(binding.ConnectionId, connection.Id, StringComparison.Ordinal))
                return ApiResults.Ok(new { kind = "ignored" });

            if (!string.Equals(req.SenderSlackUserId, connection.OwnerSlackUserId, StringComparison.Ordinal))
            {
                const string reason = "This Slack Connection is available only to its owner.";
                await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                return ApiResults.Ok(new { kind = "rejected", reason });
            }

            var prompt = RemoveBotMention(body.Text ?? string.Empty, ownBotUserId);
            return await DispatchChannelFollowupAsync(req, binding.SessionId, prompt, ct);
        }

        if (!string.IsNullOrWhiteSpace(body.ThreadTs))
        {
            var reconciled = await ReconcileSessionIdAsync(
                req, projectId, body.TeamId, body.ConversationId, rootTs, ct);
            if (reconciled is not null)
            {
                if (!string.Equals(req.SenderSlackUserId, connection.OwnerSlackUserId, StringComparison.Ordinal))
                {
                    const string reason = "This Slack Connection is available only to its owner.";
                    await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                    return ApiResults.Ok(new { kind = "rejected", reason });
                }
                var prompt = RemoveBotMention(body.Text ?? string.Empty, ownBotUserId);
                return await DispatchChannelFollowupAsync(req, reconciled, prompt, ct);
            }
        }

        return ApiResults.Ok(new { kind = "ignored" });
    }

    /// <summary>
    /// Reconciles a session id for the inbound thread when no binding
    /// row is present. Order:
    /// <list type="number">
    /// <item><description>the inbox route whose message identity equals the thread root (the launch path persists the session id BEFORE the reply per D2);</description></item>
    /// <item><description>the unique AgentSession row whose provenance labels match (connection, conversation, root message ts).</description></item>
    /// </list>
    /// When both recovery sources agree, the binding row is repaired
    /// so subsequent lookups stay index-only.
    /// </summary>
    private static async Task<string?> ReconcileSessionIdAsync(
        HandleChannelIngressRequest req,
        string projectId,
        string workspaceTeamId,
        string conversationId,
        string rootTs,
        CancellationToken ct)
    {
        var inboxSessionId = await ResolveInboxRootSessionIdAsync(
            req, projectId, req.Connection.Id, workspaceTeamId, conversationId, rootTs, ct);
        if (!string.IsNullOrWhiteSpace(inboxSessionId))
        {
            await req.ThreadMapping.UpsertAsync(
                projectId, workspaceTeamId, req.Connection.Id, conversationId, rootTs,
                req.SenderSlackUserId, inboxSessionId, rootTs, ct);
            return inboxSessionId;
        }

        var provenanceSessionId = await ResolveSessionProvenanceAsync(
            req, projectId, req.Connection.Id, workspaceTeamId, conversationId, rootTs, ct);
        if (!string.IsNullOrWhiteSpace(provenanceSessionId))
        {
            await req.ThreadMapping.UpsertAsync(
                projectId, workspaceTeamId, req.Connection.Id, conversationId, rootTs,
                req.SenderSlackUserId, provenanceSessionId, rootTs, ct);
            return provenanceSessionId;
        }

        return null;
    }

    private static async Task<string?> ResolveInboxRootSessionIdAsync(
        HandleChannelIngressRequest req,
        string projectId,
        string connectionId,
        string workspaceTeamId,
        string conversationId,
        string threadTs,
        CancellationToken ct)
    {
        await using var scope = req.Services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<SlackProviderInboxStore>();
        var root = await inbox.FindRootRouteSessionIdAsync(
            projectId, connectionId, workspaceTeamId, conversationId, threadTs, ct);
        return root;
    }

    private static async Task<string?> ResolveSessionProvenanceAsync(
        HandleChannelIngressRequest req,
        string projectId,
        string connectionId,
        string workspaceTeamId,
        string conversationId,
        string threadTs,
        CancellationToken ct)
    {
        return await req.Sessions.FindSessionIdBySlackThreadProvenanceAsync(
            projectId, connectionId, conversationId, threadTs, ct);
    }

    /// <summary>
    /// Filters the parsed mention list down to the subset that maps to
    /// identity-bound Mohist Bots in the same workspace. The result is
    /// the <c>M ∩ W</c> set D4 uses to attribute channel messages —
    /// arbitrary human mentions are never treated as Bot mentions, and
    /// a Bot managed by another Mohist Server never appears here.
    /// Deduplicates by <c>BotUserId</c> so multiple Connections bound to
    /// the same Bot (a test setup convenience or a future multi-workspace
    /// Bot) never collapse a single-Bot mention into a multi-Bot prompt.
    /// </summary>
    private static IReadOnlyList<WorkspaceBoundBot> MentionedWorkspaceBots(
        IReadOnlyList<string> mentionedUserIds,
        IReadOnlyList<WorkspaceBoundBot> workspaceBots)
    {
        if (mentionedUserIds.Count == 0 || workspaceBots.Count == 0)
            return Array.Empty<WorkspaceBoundBot>();
        var mentionedSet = new HashSet<string>(mentionedUserIds, StringComparer.Ordinal);
        var result = new List<WorkspaceBoundBot>(workspaceBots.Count);
        var seenBotIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bot in workspaceBots)
        {
            if (!mentionedSet.Contains(bot.BotUserId))
                continue;
            if (!seenBotIds.Add(bot.BotUserId))
                continue;
            result.Add(bot);
        }
        return result;
    }

    private static IReadOnlyList<string> BuildMentionedBotIds(IReadOnlyList<string>? mentioned)
    {
        if (mentioned is null || mentioned.Count == 0) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(mentioned.Count);
        foreach (var id in mentioned)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (seen.Add(id)) result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// Claims and posts the once-only "pick a single Agent" prompt for
    /// an ambiguous channel message. The race-winning Connection
    /// (D5 first-writer-wins on
    /// <c>(WorkspaceTeamId, ConversationId, MessageTs)</c>) enqueues a
    /// UserAction reply via its own outbox; every loser observes the
    /// row exists and no-ops, so concurrent per-Connection ingress
    /// calls and Slack redeliveries collapse to one prompt. The prompt
    /// copies the inbound <c>ThreadTs</c> onto the delivery so a root
    /// ambiguous message is prompted at the channel root and a thread
    /// ambiguous reply is prompted in the same thread.
    /// </summary>
    private static async Task<IResult> HandleAmbiguousPromptAsync(
        HandleChannelIngressRequest req,
        IReadOnlyList<string> ambiguousBotLabels,
        IReadOnlyList<string> mentionedConnectionIds,
        CancellationToken ct)
    {
        var body = req.Body;
        var projectId = req.ProjectId;
        var connection = req.Connection;

        var labelSummary = string.Join(", ", ambiguousBotLabels);
        var promptText = $"Multiple Agents could answer this; mention a single Bot to address one. Mentioned: {labelSummary}.";
        var dispatchRef = SlackAmbiguousPromptStore.PromptDispatchRef(
            body.TeamId, body.ConversationId, body.MessageTs);

        var claim = await req.AmbiguousPrompts.TryClaimAsync(
            projectId, body.TeamId, body.ConversationId, body.MessageTs,
            body.ThreadTs, connection.Id, mentionedConnectionIds, ct);

        if (!claim.Claimed)
            return ApiResults.Ok(new { kind = "ambiguous", reason = "Another Bot is responding.", winner = claim.WinningConnectionId });

        await EnqueueRequiredReplyAsync(req.Outbox, projectId, connection, body.ConversationId,
            promptText, dispatchRef, ct, body.ThreadTs);
        return ApiResults.Ok(new { kind = "ambiguous", reason = promptText });
    }

    private static async Task<IResult> RejectNonOwnerChannelMessageAsync(
        HandleChannelIngressRequest req,
        CancellationToken ct)
    {
        const string reason = "This Slack Connection is available only to its owner.";
        await EnqueueReplyAsync(req.Outbox, req.ProjectId, req.Connection, req.Body.ConversationId,
            reason, null, ct, req.Body.ThreadTs);
        return ApiResults.Ok(new { kind = "rejected", reason });
    }

    private static bool IsBackpressured(AgentConnection connection) =>
        connection.ConnectionHealth == Agent.Domain.ConnectionHealthKind.Degraded
        && connection.HealthReason?.Contains("backpressured", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<IResult> LaunchChannelRootAsync(
        HandleChannelIngressRequest req,
        string prompt,
        string rootTs,
        CancellationToken ct)
    {
        var body = req.Body;
        var projectId = req.ProjectId;
        var connection = req.Connection;
        var dispatchRef = $"slack-thread:{body.TeamId}:{body.ConversationId}:{rootTs}";

        var agent = await req.Agents.GetByIdAsync(projectId, connection.AgentId);
        if (agent is null)
            return ApiResults.Fail("The Agent bound to this Connection no longer exists.", 409, "agent_not_found");

        var dispatchDecision = AgentConnectionDispatchDecision.For(
            AgentReadinessDeriver.Derive(agent.AgentConfig));
        if (!dispatchDecision.Accepted)
        {
            await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId,
                dispatchDecision.Reason!, null, ct, body.ThreadTs);
            return ApiResults.Ok(new { kind = dispatchDecision.Kind, reason = dispatchDecision.Reason });
        }

        var routeDraft = new SlackProviderInboxRouteDraft(SlackProviderInboxRouteKinds.LaunchThread);
        SlackProviderInboxAcceptResult accepted;
        try
        {
            accepted = await req.Inbox.AcceptAsync(new SlackProviderInboxDraft(
                projectId, connection.Id, req.Identity, req.SenderSlackUserId, rootTs), routeDraft, ct);
        }
        catch (SlackProviderInboxCapacityExceededException ex)
        {
            return ApiResults.Conflict(ex.Message, "slack_inbox_backpressured");
        }

        AgentLaunchResult? launch = null;
        var sessionId = accepted.AlreadyExisted
            ? (await req.Inbox.GetRouteAsync(projectId, accepted.Id, ct)).SessionId
            : null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            launch = await req.Launcher.LaunchConnectionAsync(
                agent,
                prompt,
                new ConnectionLaunchOrigin(
                    connection.Id, body.TeamId, req.SenderSlackUserId, body.ConversationId, body.MessageTs, rootTs),
                ct);
            sessionId = await req.Inbox.SetRouteSessionIdAsync(projectId, accepted.Id, launch.SessionId, ct);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var bindResult = await req.ThreadMapping.UpsertAsync(
                projectId, body.TeamId, connection.Id, body.ConversationId, rootTs,
                req.SenderSlackUserId, sessionId, rootTs, ct);
            sessionId = bindResult.SessionId;
            if (bindResult.AlreadyExisted)
                sessionId = await req.Inbox.SetRouteSessionIdAsync(projectId, accepted.Id, sessionId, ct);
        }

        var acknowledgement = accepted.AlreadyExisted
            ? "This task was already accepted; execution is being resumed."
            : "Task accepted and queued for execution.";
        await EnqueueRequiredReplyAsync(req.Outbox, projectId, connection, body.ConversationId,
            acknowledgement, dispatchRef, ct, rootTs);
        await req.Inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
        return ApiResults.Ok(new
        {
            kind = accepted.AlreadyExisted ? "queued" : "accepted",
            sessionId,
            jobKey = launch?.JobKey,
            inputId = launch?.InputId,
            turnId = launch?.TurnId,
            threadRoot = rootTs,
        });
    }

    private static async Task<IResult> DispatchChannelFollowupAsync(
        HandleChannelIngressRequest req,
        string sessionId,
        string prompt,
        CancellationToken ct)
    {
        var body = req.Body;
        var projectId = req.ProjectId;
        var connection = req.Connection;
        var dispatchRef = $"slack-thread-followup:{body.TeamId}:{body.ConversationId}:{body.MessageTs}";

        if (string.IsNullOrWhiteSpace(prompt))
        {
            const string reason = "Please send a task for the Agent to perform.";
            await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId,
                reason, null, ct, body.ThreadTs);
            return ApiResults.Ok(new { kind = "rejected", reason });
        }

        var routeDraft = new SlackProviderInboxRouteDraft(SlackProviderInboxRouteKinds.FollowupThread, sessionId);
        SlackProviderInboxAcceptResult accepted;
        try
        {
            accepted = await req.Inbox.AcceptAsync(new SlackProviderInboxDraft(
                projectId, connection.Id, req.Identity, req.SenderSlackUserId, body.ThreadTs), routeDraft, ct);
        }
        catch (SlackProviderInboxCapacityExceededException ex)
        {
            return ApiResults.Conflict(ex.Message, "slack_inbox_backpressured");
        }

        var idempotencyKey = $"slack-thread-followup:{body.TeamId}:{body.ConversationId}:{body.MessageTs}";
        var followupResult = await RouteFollowupAsync(
            projectId,
            sessionId,
            prompt,
            idempotencyKey,
            BuildSlackInputProvenance(connection.Id, body, body.ThreadTs),
            req.Grains,
            req.FollowupDispatcher,
            ct);
        var ack = BuildFollowupAck(followupResult.Status, accepted.AlreadyExisted);
        await EnqueueRequiredReplyAsync(req.Outbox, projectId, connection, body.ConversationId,
            ack, dispatchRef, ct, body.ThreadTs);
        await req.Inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
        return ApiResults.Ok(new
        {
            kind = followupResult.Kind,
            sessionId,
            inputId = followupResult.InputId,
            turnId = followupResult.TurnId,
            followup = true,
            threadRoot = body.ThreadTs ?? body.MessageTs,
        });
    }
}

/// <summary>
/// Inputs the DM ingress needs to do its work, captured in a single
/// record so the route handler can stay a thin entry point and the
/// classifier stays a normal helper. The connection-side collaborators
/// (<c>Agents</c>, <c>Sessions</c>, <c>Inbox</c>, <c>Outbox</c>, …)
/// are resolved through DI once per HTTP request; this record just
/// forwards them.
/// </summary>
internal sealed record HandleDmIngressRequest(
    string ProjectId,
    Agent.Domain.AgentConnection Connection,
    SlackMessageIdentity Identity,
    string SenderSlackUserId,
    SlackIngressBody Body,
    SlackDmSessionMappingStore DmMapping,
    AgentSessionQuerier Sessions,
    AgentQuerier Agents,
    SlackOwnerClaimService Claims,
    SlackProviderInboxStore Inbox,
    SlackOutboxStore Outbox,
    IAgentLauncher Launcher,
    IGrainFactory Grains,
    AgentSessionFollowupDispatcher FollowupDispatcher,
    IHubContext<RunnerHub> RunnerHub,
    RunnerConnectionTracker RunnerConnections,
    IServiceProvider Services)
{
    public static HandleDmIngressRequest From(
        string projectId,
        Agent.Domain.AgentConnection connection,
        SlackMessageIdentity identity,
        string senderSlackUserId,
        SlackIngressBody body,
        SlackDmSessionMappingStore dmMapping,
        AgentSessionQuerier sessions,
        AgentQuerier agents,
        SlackOwnerClaimService claims,
        SlackProviderInboxStore inbox,
        SlackOutboxStore outbox,
        IAgentLauncher launcher,
        IGrainFactory grains,
        AgentSessionFollowupDispatcher followupDispatcher,
        IHubContext<RunnerHub> runnerHub,
        RunnerConnectionTracker runnerConnections,
        IServiceProvider services) =>
        new(projectId, connection, identity, senderSlackUserId, body,
            dmMapping, sessions, agents, claims, inbox, outbox,
            launcher, grains, followupDispatcher, runnerHub, runnerConnections, services);
}

/// <summary>
/// Inputs the channel ingress needs to do its work, captured in a single
/// record so the route handler can stay a thin entry point. The
/// channel-state-machine code is path-agnostic; the connection-scoped
/// collaborators are resolved through DI once per HTTP request and
/// forwarded.
/// </summary>
internal sealed record HandleChannelIngressRequest(
    string ProjectId,
    Agent.Domain.AgentConnection Connection,
    SlackMessageIdentity Identity,
    string SenderSlackUserId,
    SlackIngressBody Body,
    AgentConnectionStore Connections,
    SlackThreadSessionMappingStore ThreadMapping,
    SlackAmbiguousPromptStore AmbiguousPrompts,
    AgentSessionQuerier Sessions,
    AgentQuerier Agents,
    SlackOwnerClaimService Claims,
    SlackProviderInboxStore Inbox,
    SlackOutboxStore Outbox,
    IAgentLauncher Launcher,
    IGrainFactory Grains,
    AgentSessionFollowupDispatcher FollowupDispatcher,
    IHubContext<RunnerHub> RunnerHub,
    RunnerConnectionTracker RunnerConnections,
    IServiceProvider Services)
{
    public static HandleChannelIngressRequest From(
        string projectId,
        Agent.Domain.AgentConnection connection,
        SlackMessageIdentity identity,
        string senderSlackUserId,
        SlackIngressBody body,
        AgentConnectionStore connections,
        SlackThreadSessionMappingStore threadMapping,
        SlackAmbiguousPromptStore ambiguousPrompts,
        AgentSessionQuerier sessions,
        AgentQuerier agents,
        SlackOwnerClaimService claims,
        SlackProviderInboxStore inbox,
        SlackOutboxStore outbox,
        IAgentLauncher launcher,
        IGrainFactory grains,
        AgentSessionFollowupDispatcher followupDispatcher,
        IHubContext<RunnerHub> runnerHub,
        RunnerConnectionTracker runnerConnections,
        IServiceProvider services) =>
        new(projectId, connection, identity, senderSlackUserId, body,
            connections, threadMapping, ambiguousPrompts,
            sessions, agents, claims, inbox, outbox,
            launcher, grains, followupDispatcher, runnerHub, runnerConnections, services);
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
    public string? ThreadTs { get; init; }
    public IReadOnlyList<string> MentionedUserIds { get; init; } = Array.Empty<string>();
    public string? SenderSlackUserId { get; init; }
    public string? SenderKind { get; init; }
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
