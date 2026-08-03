using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Security;
using Mohist.Server.Infrastructure.Security.Secrets;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Project.Services;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;
using Mohist.Server.Agent.Domain;

namespace Mohist.Server.Api;

public static class SlackConnectionRoutes
{
    private const string SlackAppCreationReference = "https://api.slack.com/apps?new_app=1";
    private static readonly Regex SlackMentionToken = new(
        @"<@(?<id>[A-Za-z0-9_-]+)(?:\|[^>]*)?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static WebApplication MapSlackConnectionRoutes(this WebApplication app)
    {
        var management = app.MapGroup("/api/projects/{projectRef}/slack-connections")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        management.MapPost("/", async (
            HttpContext context,
            SlackConnectionCreateBody body,
            AgentConnectionStore connections,
            AgentQuerier agents,
            CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            if (body is null || string.IsNullOrWhiteSpace(body.AgentId))
                return ApiResults.BadRequest("agentId is required.");

            var agentId = body.AgentId.Trim();
            var agent = await agents.GetByIdAsync(projectId, agentId);
            if (agent is null)
                return ApiResults.BadRequest($"Agent '{agentId}' was not found in project '{projectId}'.", "agent_not_found");
            var preview = SlackBotIdentityDeriver.Derive(agent);
            var botName = body.BotName?.Trim() ?? preview.BotName;
            var connection = new AgentConnection
            {
                Id = $"connection_{Guid.NewGuid():N}",
                ProjectId = projectId,
                AgentId = agentId,
                ProviderKind = ConnectionProviderKind.Slack,
                WorkspaceTeamId = string.Empty,
                AppId = string.Empty,
                BotUserId = string.Empty,
                BotName = botName,
                AvatarHash = body.AvatarHash,
            };
            try
            {
                var created = await connections.CreateAsync(connection, ct);
                return Results.Json(new ApiResponse<object>(true, new
                {
                    connection = created,
                    botName = created.BotName,
                    appDescription = preview.AppDescription,
                    slackAppCreationReference = SlackAppCreationReference,
                }), statusCode: 201);
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

        management.MapGet("/", async (
            HttpContext context,
            string? agentId,
            AgentConnectionStore connections,
            CancellationToken ct) =>
        {
            var rows = await connections.ListAsync(context.GetResolvedProject().Id, ct: ct);
            return ApiResults.Ok(string.IsNullOrWhiteSpace(agentId)
                ? rows
                : rows.Where(row => row.AgentId == agentId).ToList());
        });

        app.MapGet("/api/slack-connections/adapter", async (
            HttpContext http,
            AgentConnectionStore connections,
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            var targets = await connections.ListForAdapterAsync(ct);
            return ApiResults.Ok(targets.Select(target => new
            {
                ownerKind = SlackDeliveryOwnerKinds.Connection,
                projectId = target.ProjectId,
                connectionId = target.ConnectionId,
            }).ToArray());
        });

        MapSlackManagerAdapterRoutes(app);

        management.MapGet("/{connectionId}", async (
            HttpContext context,
            string connectionId,
            AgentConnectionStore connections,
            AgentQuerier agents,
            SlackManagerApplicationService manager,
            CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");

            var agent = await agents.GetByIdAsync(projectId, connection.AgentId);
            var preview = agent is null
                ? SlackBotIdentityDeriver.Derive(connection.AgentId, connection.BotName, string.Empty)
                : SlackBotIdentityDeriver.Derive(agent);
            return ApiResults.Ok(new
            {
                connection,
                botName = string.IsNullOrWhiteSpace(connection.BotName) ? preview.BotName : connection.BotName,
                appDescription = preview.AppDescription,
                slackAppCreationReference = SlackAppCreationReference,
                managedApp = await manager.GetAsync(projectId, connectionId, ct),
            });
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

        management.MapPost("/{connectionId}/enable", async (HttpContext context, string connectionId, AgentConnectionStore connections, SlackOutboxStore outbox, CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null) return ApiResults.NotFound("Slack Connection was not found.");
            if (connection.DesiredState == DesiredStateKind.Enabled)
                return ApiResults.Ok(connection);
            var updated = await connections.UpdateAsync(projectId, connectionId,
                new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
                desiredState: DesiredStateKind.Enabled, ct: ct);
            if (updated is not null)
                await outbox.PrunePendingStatusMutationsAsync(projectId, connectionId, ct);
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

        management.MapGet("/{connectionId}/access", async (HttpContext context, string connectionId, AgentConnectionStore connections, SlackConnectionAccessManager access, CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var detail = await connections.GetAsync(projectId, connectionId, ct);
            if (detail is null)
                return ApiResults.NotFound("Slack Connection was not found.");

            var allowMembers = await access.ListMembersAsync(projectId, connectionId, ct);
            return ApiResults.Ok(new
            {
                accessPolicy = detail.AccessPolicy,
                allowMembers,
                anyoneDisclosure = SlackConnectionAccessContract.AnyoneDisclosure,
            });
        });

        management.MapPost("/{connectionId}/manage-access", async (HttpContext context, string connectionId, SlackConnectionManageAccessBody body, AgentConnectionStore connections, SlackConnectionAccessManager access, CancellationToken ct) =>
        {
            if (body is null)
                return ApiResults.BadRequest("accessPolicy and allowMembers are required.");
            var projectId = context.GetResolvedProject().Id;
            try
            {
                var replaced = await access.ReplaceAsync(
                    projectId, connectionId, body.AccessPolicy, body.AllowMembers, ct);
                if (!replaced)
                    return ApiResults.NotFound("Slack Connection was not found.");
            }
            catch (SlackConnectionAccessValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, ex.Code);
            }

            var detail = await connections.GetAsync(projectId, connectionId, ct);
            if (detail is null)
                return ApiResults.NotFound("Slack Connection was not found.");

            var allowMembers = await access.ListMembersAsync(projectId, connectionId, ct);
            return ApiResults.Ok(new
            {
                connection = detail,
                accessPolicy = detail.AccessPolicy,
                allowMembers,
                anyoneDisclosure = SlackConnectionAccessContract.AnyoneDisclosure,
            });
        });

        management.MapGet("/{connectionId}/members", async (HttpContext context, string connectionId, string? q, int? limit, AgentConnectionStore connections, SlackMemberSearchService search, CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");
            if (string.IsNullOrWhiteSpace(connection.WorkspaceTeamId))
                return ApiResults.Ok(new { members = Array.Empty<SlackMemberSearchEntry>() });
            var members = await search.SearchAsync(projectId, connectionId, q, limit, ct);
            return ApiResults.Ok(new { members });
        });

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
                var updated = await outbox.ResendUncertainAsync(projectId, connectionId, deliveryId, ct);
                if (updated == 0)
                    return ApiResults.Conflict(
                        "Only Delivery uncertain rows can be resent.",
                        "delivery_not_uncertain");
                return ApiResults.Ok(new { id = deliveryId, state = SlackOutboxStates.Pending });
            }
            catch (SlackOutboxRowNotFoundException)
            {
                return ApiResults.NotFound("Delivery was not found.");
            }
            catch (SlackOutboxStateException ex)
            {
                return ApiResults.Conflict(ex.Message, "delivery_state_conflict");
            }
        });

        management.MapPost("/{connectionId}/clear-gap", async (
            HttpContext context,
            string connectionId,
            AgentConnectionStore connections,
            CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");
            var cleared = await connections.ClearOfflineGapIfSetAsync(projectId, connectionId, ct);
            return ApiResults.Ok(new { cleared = cleared > 0 });
        });

        var group = app.MapGroup("/api/projects/{projectRef}/slack-connections/{connectionId}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        group.MapPost("/ingress", async (
            HttpContext http,
            SlackIngressBody body,
            string connectionId,
            AgentConnectionStore connections,
            SlackOwnerClaimService claims,
            SlackConnectionAccessDecider accessDecider,
            SlackProviderInboxStore inbox,
            SlackOutboxStore outbox,
            SlackDmSessionMappingStore mapping,
            SlackThreadSessionMappingStore threadMapping,
            SlackThreadLaunchReservationStore threadLaunchReservations,
            SlackAmbiguousPromptStore ambiguousPrompts,
            AgentQuerier agents,
            IAgentLauncher launcher,
            SlackAttachmentInputBinder attachmentBinder,
            IGrainFactory grains,
            AgentSessionFollowupDispatcher followupDispatcher,
            AgentSessionQuerier sessions,
            ISecretStore secrets,
            SlackThreadHistoryReader threadHistory,
            IOptions<SlackProviderOptions> slackProviderOptions,
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            var projectId = http.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");
            if (body is null)
                return ApiResults.Ok(new { kind = "ignored" });
            if (connection.DesiredState == DesiredStateKind.Disabled)
            {
                var disabledIdentity = new SlackMessageIdentity(body.TeamId, body.ConversationId, body.MessageTs);
                var disabledIdentityError = disabledIdentity.Validate();
                if (disabledIdentityError.Length != 0)
                    return ApiResults.BadRequest(disabledIdentityError, "invalid_slack_identity");
                if (!string.Equals(body.TeamId, connection.WorkspaceTeamId, StringComparison.Ordinal))
                    return ApiResults.BadRequest("The Slack workspace does not match this Connection.", "workspace_mismatch");

                try
                {
                    var discarded = await inbox.AcceptAsync(
                        new SlackProviderInboxDraft(projectId, connection.Id, disabledIdentity,
                            body.SenderSlackUserId ?? "unknown", body.ThreadTs),
                        new SlackProviderInboxRouteDraft(SlackProviderInboxRouteKinds.DisabledDiscarded), ct);
                    await inbox.MarkDispatchedAsync(projectId, discarded.Id, ct);
                }
                catch (SlackProviderInboxCapacityExceededException)
                {
                    // A disabled transport event is still acknowledged. The
                    // inbox is the audit record when capacity is available.
                }
                return ApiResults.Ok(new { kind = SlackProviderInboxRouteKinds.DisabledDiscarded, reason = "This Connection is disabled.", audited = true });
            }
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
                        connections, threadMapping, threadLaunchReservations, ambiguousPrompts,
                        sessions, agents, claims, accessDecider, inbox, outbox,
                        launcher, attachmentBinder, grains, followupDispatcher,
                        secrets, threadHistory, slackProviderOptions,
                        http.RequestServices),
                    ct);

            return await HandleDmIngressAsync(
                HandleDmIngressRequest.From(
                    projectId, connection, identity, senderSlackUserId, body,
                    connections, mapping, agents, claims, inbox, outbox,
                    launcher, attachmentBinder, grains, followupDispatcher,
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

        group.MapPost("/deliveries/claim-uncertain", async (
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
            var entry = await outbox.ClaimUncertainAsync(projectId, connectionId, body?.AdapterId ?? string.Empty, ct);
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
            if (string.IsNullOrWhiteSpace(body.AdapterId))
                return ApiResults.BadRequest("adapterId is required.");
            if (string.Equals(body.Outcome, "delivered", StringComparison.OrdinalIgnoreCase))
                await outbox.MarkDeliveredAsync(projectId, body.Id, body.ProviderMessageIdentity, body.AdapterId, ct);
            else if (string.Equals(body.Outcome, "uncertain", StringComparison.OrdinalIgnoreCase))
                await outbox.MarkDeliveryUncertainAsync(projectId, body.Id, body.Reason, body.AdapterId, ct);
            else
                await outbox.ScheduleRetryAsync(projectId, body.Id, body.Reason, body.AdapterId, ct);
            return ApiResults.Ok(new { id = body.Id, outcome = body.Outcome });
        });

        return app;
    }

    private static void MapSlackManagerAdapterRoutes(WebApplication app)
    {
        app.MapGet("/api/slack-manager/adapter", async (
            HttpContext http,
            IDbContextFactory<MohistDbContext> dbFactory,
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var targets = await db.SlackWorkspaceEnrollments.AsNoTracking()
                .Where(enrollment => enrollment.DeletedAt == null
                    && enrollment.Lifecycle == SlackEnrollmentLifecycle.Active
                    && enrollment.ManagerCapability == SlackManagerCapability.Available
                    && enrollment.ManagerReadiness == SlackManagerReadiness.Ready
                    && enrollment.ManagerAppId != string.Empty
                    && enrollment.ManagerBotUserId != string.Empty
                    && enrollment.ManagerCredentialRef != string.Empty)
                .OrderBy(enrollment => enrollment.Id)
                .Select(enrollment => new
                {
                    ownerKind = SlackDeliveryOwnerKinds.Manager,
                    enrollmentId = enrollment.Id,
                    workspaceTeamId = enrollment.WorkspaceTeamId,
                })
                .ToListAsync(ct);
            return ApiResults.Ok(targets);
        });

        app.MapPost("/api/slack-manager/adapter/{enrollmentId}/session", async (
            HttpContext http,
            string enrollmentId,
            AdapterSessionBody body,
            SlackWorkspaceEnrollmentStore enrollments,
            ISecretStore secrets,
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            if (string.IsNullOrWhiteSpace(body?.AdapterId))
                return ApiResults.BadRequest("adapterId is required.");

            var enrollment = await enrollments.GetAsync(enrollmentId, ct);
            if (enrollment is null)
                return ApiResults.NotFound("Slack Manager enrollment was not found.");
            if (enrollment.Lifecycle != SlackEnrollmentLifecycle.Active)
                return ApiResults.Conflict("The Slack Manager enrollment is not active.", "manager_enrollment_inactive");
            if (enrollment.ManagerReadiness != SlackManagerReadiness.Ready)
                return ApiResults.Conflict("The Slack Manager adapter is not ready.", "manager_adapter_not_ready");

            var appToken = await secrets.LoadAsync(
                new SecretStoreAddress(SlackDeliveryOwnerIds.ManagerProjectId, enrollment.Id, SecretKind.AppToken), ct);
            var botToken = await secrets.LoadAsync(
                new SecretStoreAddress(SlackDeliveryOwnerIds.ManagerProjectId, enrollment.Id, SecretKind.BotToken), ct);
            return ApiResults.Ok(new
            {
                adapterId = body.AdapterId,
                ownerKind = SlackDeliveryOwnerKinds.Manager,
                workspaceTeamId = enrollment.WorkspaceTeamId,
                appToken = appToken is { Length: > 0 } ? Encoding.UTF8.GetString(appToken) : null,
                botToken = botToken is { Length: > 0 } ? Encoding.UTF8.GetString(botToken) : null,
            });
        });

        app.MapPost("/api/slack-manager/adapter/{enrollmentId}/deliveries/claim", async (
            HttpContext http,
            string enrollmentId,
            DeliveryClaimBody body,
            SlackOutboxStore outbox,
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
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
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
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
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(http.Request.Headers))
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
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

    private static async Task EnqueueInitialLaunchStatusAsync(
        IServiceProvider services,
        IGrainFactory grains,
        string projectId,
        Agent.Domain.AgentConnection connection,
        SlackMessageIdentity source,
        string? threadTs,
        AgentLaunchResult launch,
        string actorSlackUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(launch.SessionId) || string.IsNullOrWhiteSpace(launch.TurnId))
            return;

        var turn = await grains.GetGrain<IAgentSessionGrain>(launch.SessionId)
            .ResolveTurnControlAsync(launch.TurnId);
        if (turn is null
            || turn.Classification is not (AgentTurnControlClassification.Queued or AgentTurnControlClassification.Executing))
        {
            return;
        }

        var stopAction = await services.GetRequiredService<SlackTurnControlService>().CreateStopActionAsync(
            connection,
            launch.SessionId,
            launch.TurnId,
            launch.InputId,
            SlackStatusProjection.DispatchRef(source, "progress"),
            actorSlackUserId,
            source,
            threadTs,
            ct);

        var blocks = await BuildSessionStatusBlocksAsync(
            services,
            projectId,
            launch.SessionId,
            stopAction?.Blocks);
        await services.GetRequiredService<SlackStatusProjection>().EnqueueWorkingAsync(
            projectId,
            connection.Id,
            source,
            threadTs,
            SlackStatusProjection.DispatchRef(source, "progress"),
            blocks,
            ct);
    }

    private static async Task<SlackThreadHistoryReadResult> ReadThreadHistoryIfAnyAsync(
        HandleChannelIngressRequest req,
        string rootTs,
        CancellationToken ct)
    {
        var body = req.Body;
        return await req.ThreadHistory.ReadAsync(
            req.ProjectId,
            req.Connection.Id,
            body.ConversationId,
            rootTs,
            body.MessageTs,
            ct);
    }

    private static AgentStartupContext BuildStartupContext(
        HandleChannelIngressRequest req,
        IReadOnlyList<SlackConversationMessage> messages)
    {
        var budget = Math.Max(1, req.SlackProviderOptions.Value.StartupContextCharacterBudget);
        var (text, marker, omitted) = SlackThreadHistoryReader.ApplyBudget(
            messages,
            budget);
        return new AgentStartupContext(
            Text: text,
            Provenance: new AgentStartupContextProvenance(
                Source: "slack-thread-history",
                Truncated: marker is not null,
                TruncationMarker: marker,
                OmittedOldestMessageCount: omitted));
    }

    private static async Task<JsonElement?> BuildSessionStatusBlocksAsync(
        IServiceProvider services,
        string projectId,
        string sessionId,
        JsonElement? controlBlocks)
    {
        var links = services.GetRequiredService<SlackWebLinkBuilder>();
        if (!links.HasUsableExternalWebUrl)
            return controlBlocks;

        var project = await services.GetRequiredService<ProjectQuerier>().GetByIdAsync(projectId);
        var link = project is null
            ? null
            : links.BuildOpenSession(project.Name, sessionId);
        return CombineBlocks(controlBlocks, link?.Blocks);
    }

    private static JsonElement? CombineBlocks(JsonElement? first, JsonElement? second)
    {
        var blocks = new List<JsonElement>();
        AddBlockArray(blocks, first);
        AddBlockArray(blocks, second);
        return blocks.Count == 0 ? null : JsonSerializer.SerializeToElement(blocks);
    }

    private static void AddBlockArray(List<JsonElement> target, JsonElement? source)
    {
        if (source is not { ValueKind: JsonValueKind.Array })
            return;

        target.AddRange(source.Value.EnumerateArray().Select(block => block.Clone()));
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
        string TurnId,
        SlackAttachmentBinding? AttachmentBinding = null);

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
        Agent.Domain.AgentConnection connection,
        SlackMessageIdentity identity,
        IReadOnlyList<SlackIngressFile> files,
        string currentSessionId,
        string prompt,
        string idempotencyKey,
        AgentSessionInputProvenance provenance,
        SlackAttachmentInputBinder attachmentBinder,
        IGrainFactory grains,
        AgentSessionFollowupDispatcher followupDispatcher,
        CancellationToken ct)
    {
        var preMintedInputId = AgentLaunchCoordinatorCodec.StableToken(
            $"{currentSessionId}\n{idempotencyKey}\nfollowup-input");
        var attachmentBinding = await attachmentBinder.PrepareAsync(
            projectId,
            connection,
            identity,
            currentSessionId,
            preMintedInputId,
            files,
            ct);
        if (string.IsNullOrWhiteSpace(prompt) && attachmentBinding.AcceptedCount == 0)
        {
            await attachmentBinder.RollbackAsync(
                projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult(
                "followup_rejected", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }

        var grain = grains.GetGrain<IAgentSessionGrain>(currentSessionId);
        AgentSessionFollowupAcceptResult accept;
        try
        {
            accept = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: prompt,
                Source: "agent-session-followup",
                IdempotencyKey: idempotencyKey,
                Attachments: attachmentBinding.AcceptedDescriptors,
                PreMintedInputId: preMintedInputId,
                AttachmentResults: attachmentBinding.Results,
                Provenance: provenance));
        }
        catch (RuntimeSessionMissingException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("runtime_session_missing", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }
        catch (RecoveryOperationInProgressException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("recovery_in_progress", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }
        catch (AgentSessionFollowupCapacityExceededException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("capacity_exceeded", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }
        catch (StopOperationInProgressException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("stop_in_progress", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }
        catch (SessionActivityUnknownException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("session_activity_unknown", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }
        catch (FollowupConcurrencyLimitException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("concurrency_limit", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }
        catch (InvalidOperationException)
        {
            await attachmentBinder.RollbackAsync(projectId, currentSessionId, preMintedInputId, attachmentBinding, CancellationToken.None);
            return new FollowupRouteResult("followup_rejected", "rejected", currentSessionId, string.Empty, string.Empty, attachmentBinding);
        }

        await followupDispatcher.DispatchNextAsync(projectId, currentSessionId, ct);

        if (accept.AlreadyAccepted)
            return new FollowupRouteResult("already_accepted", "already_accepted", currentSessionId, accept.InputId, accept.TurnId, attachmentBinding);
        var status = accept.TurnStatus switch
        {
            AgentTurnStatus.Executing => "executing",
            _ => "queued",
        };
        return new FollowupRouteResult("accepted", status, currentSessionId, accept.InputId, accept.TurnId, attachmentBinding);
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
            "rejected" => "Could not continue the session. Please try again or start a new task.",
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

    private static async Task<SlackProviderInboxRouteDraft> ResolveInboxRouteDraftAsync(
        string projectId,
        string connectionId,
        string conversationId,
        bool isNewTask,
        SlackDmSessionMappingStore mapping,
        CancellationToken ct)
    {
        if (isNewTask)
            return new(SlackProviderInboxRouteKinds.NewTaskLaunch);

        var sessionId = await mapping.GetCurrentSessionIdAsync(projectId, connectionId, conversationId, ct);
        return string.IsNullOrWhiteSpace(sessionId)
            ? new(SlackProviderInboxRouteKinds.Launch)
            : new(SlackProviderInboxRouteKinds.Followup, sessionId);
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
            return ApiResults.Ok(new
            {
                kind = "backpressured",
                reason = "This Slack Connection is backpressured; retry after pending deliveries drain.",
            });

        var prompt = RemoveBotMention(body.Text ?? string.Empty, connection.BotUserId);
        if (string.IsNullOrWhiteSpace(prompt) && body.Files.Count == 0)
        {
            const string reason = "Please send a task for the Agent to perform.";
            await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
            return ApiResults.Ok(new { kind = "rejected", reason });
        }

        var isNewTask = TryStripNewTaskMarker(prompt, out var newTaskPrompt);
        if (isNewTask && string.IsNullOrWhiteSpace(newTaskPrompt) && body.Files.Count == 0)
        {
            const string reason = "Please send a task for the Agent to perform.";
            await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
            return ApiResults.Ok(new { kind = "rejected", reason });
        }

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

        var routeDraft = await ResolveInboxRouteDraftAsync(
            projectId,
            connection.Id,
            body.ConversationId,
            isNewTask,
            req.DmMapping,
            ct);

        SlackProviderInboxAcceptResult accepted;
        try
        {
            accepted = await req.Inbox.AcceptAsync(new SlackProviderInboxDraft(
                projectId, connection.Id, req.Identity, req.SenderSlackUserId, body.ThreadTs), routeDraft, ct);
        }
        catch (SlackProviderInboxCapacityExceededException)
        {
            return ApiResults.Ok(new
            {
                kind = "backpressured",
                reason = "This Slack Connection is backpressured; retry after pending deliveries drain.",
            });
        }

        if (!accepted.AlreadyExisted)
            await req.Connections.ClearOfflineGapIfSetAsync(projectId, connection.Id, ct);

        var route = await req.Inbox.GetRouteAsync(projectId, accepted.Id, ct);

        if (route.Kind is SlackProviderInboxRouteKinds.Launch or SlackProviderInboxRouteKinds.NewTaskLaunch)
        {
            var isRoutedNewTask = route.Kind == SlackProviderInboxRouteKinds.NewTaskLaunch;
            var sessionId = route.SessionId;
            AgentLaunchResult? launch = null;
            SlackAttachmentBinding? attachmentBinding = null;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                var launchIds = PreMintSlackLaunchIds(projectId, req.Identity);
                attachmentBinding = await req.AttachmentBinder.PrepareAsync(
                    projectId,
                    connection,
                    req.Identity,
                    launchIds.SessionId,
                    launchIds.InputId,
                    body.Files,
                    ct);
                var launchPrompt = isRoutedNewTask ? newTaskPrompt : prompt;
                if (string.IsNullOrWhiteSpace(launchPrompt) && attachmentBinding.AcceptedCount == 0)
                {
                    await req.AttachmentBinder.RollbackAsync(
                        projectId, launchIds.SessionId, launchIds.InputId, attachmentBinding, CancellationToken.None);
                    var rejection = BuildAttachmentAck(
                        "No usable file was accepted, so the task was not started.",
                        body.Files,
                        attachmentBinding);
                    await EnqueueRequiredReplyAsync(req.Outbox, projectId, connection, body.ConversationId,
                        rejection,
                        $"slack-ack:{req.Identity.AsKey()}",
                        ct,
                        body.ThreadTs);
                    await req.Inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
                    return ApiResults.Ok(new { kind = "rejected", reason = rejection });
                }

                try
                {
                    launch = await req.Launcher.LaunchConnectionAsync(
                        agent!,
                        launchPrompt,
                        new ConnectionLaunchOrigin(
                            connection.Id, body.TeamId, req.SenderSlackUserId, body.ConversationId, body.MessageTs, body.ThreadTs),
                        startupContext: null,
                        attachments: attachmentBinding.AcceptedDescriptors,
                        attachmentIds: attachmentBinding.AttachmentIds,
                        preMintedSessionId: launchIds.SessionId,
                        preMintedInputId: launchIds.InputId,
                        preMintedTurnId: launchIds.TurnId,
                        ct: ct);
                }
                catch
                {
                    await req.AttachmentBinder.RollbackAsync(
                        projectId, launchIds.SessionId, launchIds.InputId, attachmentBinding, CancellationToken.None);
                    throw;
                }
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
            await req.Services.GetRequiredService<SlackStatusProjection>().EnqueueReceivedAsync(
                projectId, connection.Id, req.Identity, body.ThreadTs, ct);
            if (launch is not null)
            {
                await EnqueueInitialLaunchStatusAsync(
                    req.Services,
                    req.Grains,
                    projectId,
                    connection,
                    req.Identity,
                    body.ThreadTs,
                    launch,
                    req.SenderSlackUserId,
                    ct);
            }
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
            connection,
            req.Identity,
            body.Files,
            route.SessionId!,
            prompt,
            idempotencyKey,
            BuildSlackInputProvenance(connection.Id, body, body.ThreadTs),
            req.AttachmentBinder,
            req.Grains,
            req.FollowupDispatcher,
            ct);
        await req.Services.GetRequiredService<SlackStatusProjection>().EnqueueReceivedAsync(
            projectId, connection.Id, req.Identity, body.ThreadTs, ct);
        if (followupResult.Status is "queued" or "executing")
        {
            var stopAction = await req.Services.GetRequiredService<SlackTurnControlService>().CreateStopActionAsync(
                connection,
                followupResult.SessionId,
                followupResult.TurnId,
                followupResult.InputId,
                $"agent-session-followup:{followupResult.SessionId}:{followupResult.TurnId}:progress",
                req.SenderSlackUserId,
                req.Identity,
                body.ThreadTs,
                ct);
            var blocks = await BuildSessionStatusBlocksAsync(
                req.Services,
                projectId,
                followupResult.SessionId,
                stopAction?.Blocks);
            await req.Services.GetRequiredService<SlackStatusProjection>().EnqueueWorkingAsync(
                projectId,
                connection.Id,
                req.Identity,
                body.ThreadTs,
                $"agent-session-followup:{followupResult.SessionId}:{followupResult.TurnId}:progress",
                blocks,
                ct);
        }
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

        var rootTs = !string.IsNullOrWhiteSpace(body.ThreadTs) ? body.ThreadTs : body.MessageTs;
        var mentionedUserIds = BuildMentionedBotIds(body.MentionedUserIds);
        var ownBotUserId = connection.BotUserId ?? string.Empty;

        var workspaceBots = await req.Connections.ListBoundBotsByWorkspaceAsync(body.TeamId, ct);
        var mentionedWorkspaceBots = MentionedWorkspaceBots(mentionedUserIds, workspaceBots);
        var threadBindings = await req.ThreadMapping.ListBindingsByWorkspaceAsync(
            body.TeamId, body.ConversationId, rootTs, ct);

        // The decision for THIS Connection is read once per ingress and
        // reused at the five channel owner-check sites below. Under the
        // default owner_only policy this stays a single equality check
        // (Allow iff sender == Owner) with no Slack API traffic; the
        // other policy branches swap the Allow path but keep the
        // no-cache contract.
        var decision = await req.AccessDecider.EvaluateAsync(
            connection, req.SenderSlackUserId, body.TeamId, body.ConversationId,
            isDirectMessage: false, ct);

        if (mentionedWorkspaceBots.Count >= 2)
        {
            var mentionedConnectionIds = mentionedWorkspaceBots
                .Select(bot => bot.ConnectionId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var ownerClaimantConnectionId = mentionedWorkspaceBots
                .Where(bot => string.Equals(bot.OwnerSlackUserId, req.SenderSlackUserId, StringComparison.Ordinal))
                .Select(bot => bot.ConnectionId)
                .FirstOrDefault();
            var currentConnectionIsMentioned = mentionedConnectionIds.Contains(connection.Id, StringComparer.Ordinal);
            var senderAuthorizedForCurrentConnection = decision.Allowed;
            if (!currentConnectionIsMentioned
                || (ownerClaimantConnectionId is not null
                    && !senderAuthorizedForCurrentConnection
                    && !string.Equals(ownerClaimantConnectionId, connection.Id, StringComparison.Ordinal)))
                return ApiResults.Ok(new { kind = "ignored" });
            if (!senderAuthorizedForCurrentConnection)
                return await HandleAmbiguousNonOwnerAsync(req, mentionedConnectionIds, ct);
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

            var prompt = RemoveBotMention(body.Text ?? string.Empty, ownBotUserId);
            var isRootMention = string.IsNullOrWhiteSpace(body.ThreadTs);

            var ownBinding = threadBindings.FirstOrDefault(
                binding => string.Equals(binding.ConnectionId, connection.Id, StringComparison.Ordinal));
            var otherBotsInThread = threadBindings.Any(
                binding => !string.Equals(binding.ConnectionId, connection.Id, StringComparison.Ordinal));

            if (!decision.Allowed)
                return await RejectAsync(req, decision.Reason, ct);

            if (ownBinding is not null && !isRootMention)
                return await DispatchChannelFollowupAsync(req, ownBinding.SessionId, prompt, ct);

            if (isRootMention)
            {
                if (string.IsNullOrWhiteSpace(prompt) && body.Files.Count == 0)
                {
                    const string reason = "Please send a task for the Agent to perform.";
                    await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                    return ApiResults.Ok(new { kind = "rejected", reason });
                }
                return await LaunchChannelRootAsync(req, prompt, rootTs, null, ct);
            }

            if (otherBotsInThread)
            {
                if (string.IsNullOrWhiteSpace(prompt) && body.Files.Count == 0)
                {
                    const string reason = "Please send a task for the Agent to perform.";
                    await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                    return ApiResults.Ok(new { kind = "rejected", reason });
                }
                return await LaunchChannelRootAsync(req, prompt, rootTs, null, ct);
            }

            var reconciled = await ReconcileSessionIdAsync(
                req, projectId, body.TeamId, body.ConversationId, rootTs, ct);
            if (reconciled is not null)
                return await DispatchChannelFollowupAsync(req, reconciled, prompt, ct);

            if (string.IsNullOrWhiteSpace(prompt) && body.Files.Count == 0)
            {
                const string reason = "Please send a task for the Agent to perform.";
                await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                return ApiResults.Ok(new { kind = "rejected", reason });
            }

            var historyOutcome = await ReadThreadHistoryIfAnyAsync(req, rootTs, ct);

            if (historyOutcome.Outcome == SlackThreadHistoryReadOutcome.Refused)
            {
                const string reason = "I couldn't read the full thread discussion; please re-mention me in a moment and I'll try again.";
                await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, reason, null, ct, body.ThreadTs);
                return ApiResults.Ok(new { kind = "rejected", reason });
            }

            var startupContext = historyOutcome.Outcome == SlackThreadHistoryReadOutcome.Imported
                ? BuildStartupContext(req, historyOutcome.Messages)
                : null;
            return await LaunchChannelRootAsync(req, prompt, rootTs, startupContext, ct);
        }

        if (threadBindings.Count >= 2)
        {
            var bindingConnectionIds = threadBindings
                .Select(binding => binding.ConnectionId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var ownerClaimantConnectionId = threadBindings
                .Select(binding => workspaceBots.FirstOrDefault(bot =>
                    string.Equals(bot.ConnectionId, binding.ConnectionId, StringComparison.Ordinal)
                    && string.Equals(bot.OwnerSlackUserId, req.SenderSlackUserId, StringComparison.Ordinal))?.ConnectionId)
                .FirstOrDefault(connectionId => connectionId is not null);
            var currentConnectionIsBound = bindingConnectionIds.Contains(connection.Id, StringComparer.Ordinal);
            var senderAuthorizedForCurrentConnection = decision.Allowed;
            if (!currentConnectionIsBound
                || (ownerClaimantConnectionId is not null
                    && !senderAuthorizedForCurrentConnection
                    && !string.Equals(ownerClaimantConnectionId, connection.Id, StringComparison.Ordinal)))
                return ApiResults.Ok(new { kind = "ignored" });
            if (!senderAuthorizedForCurrentConnection)
                return await HandleAmbiguousNonOwnerAsync(req, bindingConnectionIds, ct);
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

            var prompt = RemoveBotMention(body.Text ?? string.Empty, ownBotUserId);

            if (!decision.Allowed)
                return await RejectAsync(req, decision.Reason, ct);

            return await DispatchChannelFollowupAsync(req, binding.SessionId, prompt, ct);
        }

        if (!string.IsNullOrWhiteSpace(body.ThreadTs))
        {
            var reconciled = await ReconcileSessionIdAsync(
                req, projectId, body.TeamId, body.ConversationId, rootTs, ct);
            if (reconciled is not null)
            {
                var prompt = RemoveBotMention(body.Text ?? string.Empty, ownBotUserId);

                if (!decision.Allowed)
                    return await RejectAsync(req, decision.Reason, ct);
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

    private static async Task<IResult> RejectAsync(
        HandleChannelIngressRequest req,
        string reason,
        CancellationToken ct)
    {
        await EnqueueReplyAsync(req.Outbox, req.ProjectId, req.Connection, req.Body.ConversationId,
            reason, null, ct, req.Body.ThreadTs);
        return ApiResults.Ok(new { kind = "rejected", reason });
    }

    private static async Task<IResult> HandleAmbiguousNonOwnerAsync(
        HandleChannelIngressRequest req,
        IReadOnlyList<string> connectionIds,
        CancellationToken ct)
    {
        var body = req.Body;
        var claim = await req.AmbiguousPrompts.TryClaimAsync(
            req.ProjectId,
            body.TeamId,
            body.ConversationId,
            body.MessageTs,
            body.ThreadTs,
            req.Connection.Id,
            connectionIds,
            ct);
        if (!claim.Claimed)
            return ApiResults.Ok(new { kind = "ignored" });

        const string reason = "This Slack Connection is available only to its owner.";
        await EnqueueRequiredReplyAsync(
            req.Outbox,
            req.ProjectId,
            req.Connection,
            body.ConversationId,
            reason,
            SlackAmbiguousPromptStore.PromptDispatchRef(body.TeamId, body.ConversationId, body.MessageTs),
            ct,
            body.ThreadTs);
        return ApiResults.Ok(new { kind = "rejected", reason });
    }

    private static bool IsBackpressured(AgentConnection connection) =>
        connection.ConnectionHealth == Agent.Domain.ConnectionHealthKind.Degraded
        && SlackConnectionBackpressureReasons.IsBackpressureReason(connection.HealthReason);

    private static async Task<IResult> LaunchChannelRootAsync(
        HandleChannelIngressRequest req,
        string prompt,
        string rootTs,
        AgentStartupContext? startupContext,
        CancellationToken ct)
    {
        var body = req.Body;
        var projectId = req.ProjectId;
        var connection = req.Connection;
        var dispatchRef = $"slack-thread:{body.TeamId}:{body.ConversationId}:{rootTs}";

        if (IsBackpressured(connection))
            return ApiResults.Ok(new
            {
                kind = "backpressured",
                reason = "This Slack Connection is backpressured; retry after pending deliveries drain.",
            });

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

        var reservation = await req.ThreadLaunchReservations.ReserveAsync(
            projectId,
            body.TeamId,
            connection.Id,
            body.ConversationId,
            rootTs,
            body.MessageTs,
            req.SenderSlackUserId,
            ct);
        if (reservation.Kind == SlackThreadLaunchReservationKind.InProgress)
            return ApiResults.Conflict(
                "Another launch is already being established for this Slack thread; retry this message.",
                "slack_thread_launch_in_progress");
        if (reservation.Kind == SlackThreadLaunchReservationKind.Bound)
        {
            await req.ThreadMapping.UpsertAsync(
                projectId,
                body.TeamId,
                connection.Id,
                body.ConversationId,
                rootTs,
                req.SenderSlackUserId,
                reservation.SessionId!,
                rootTs,
                ct);
            return await DispatchChannelFollowupAsync(req, reservation.SessionId!, prompt, ct);
        }

        var routeDraft = new SlackProviderInboxRouteDraft(SlackProviderInboxRouteKinds.LaunchThread);
        SlackProviderInboxAcceptResult accepted;
        try
        {
            accepted = await req.Inbox.AcceptAsync(new SlackProviderInboxDraft(
                projectId, connection.Id, req.Identity, req.SenderSlackUserId, rootTs), routeDraft, ct);
        }
        catch (SlackProviderInboxCapacityExceededException)
        {
            return ApiResults.Ok(new
            {
                kind = "backpressured",
                reason = "This Slack Connection is backpressured; retry after pending deliveries drain.",
            });
        }

        if (!accepted.AlreadyExisted)
            await req.Connections.ClearOfflineGapIfSetAsync(projectId, connection.Id, ct);

        AgentLaunchResult? launch = null;
        SlackAttachmentBinding? attachmentBinding = null;
        var existingRoute = accepted.AlreadyExisted
            ? await req.Inbox.GetRouteAsync(projectId, accepted.Id, ct)
            : null;
        var sessionId = existingRoute?.SessionId ?? reservation.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var launchIds = PreMintSlackLaunchIds(projectId, req.Identity);
            attachmentBinding = await req.AttachmentBinder.PrepareAsync(
                projectId,
                connection,
                req.Identity,
                launchIds.SessionId,
                launchIds.InputId,
                body.Files,
                ct);
            if (string.IsNullOrWhiteSpace(prompt) && attachmentBinding.AcceptedCount == 0)
            {
                await req.AttachmentBinder.RollbackAsync(
                    projectId, launchIds.SessionId, launchIds.InputId, attachmentBinding, CancellationToken.None);
                var rejection = BuildAttachmentAck(
                    "No usable file was accepted, so the task was not started.",
                    body.Files,
                    attachmentBinding);
                await EnqueueRequiredReplyAsync(req.Outbox, projectId, connection, body.ConversationId,
                    rejection, dispatchRef, ct, rootTs);
                await req.Inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
                return ApiResults.Ok(new { kind = "rejected", reason = rejection });
            }

            try
            {
                launch = await req.Launcher.LaunchConnectionAsync(
                    agent,
                    prompt,
                    new ConnectionLaunchOrigin(
                        connection.Id, body.TeamId, req.SenderSlackUserId, body.ConversationId, body.MessageTs, rootTs),
                    startupContext: startupContext,
                    attachments: attachmentBinding.AcceptedDescriptors,
                    attachmentIds: attachmentBinding.AttachmentIds,
                    preMintedSessionId: launchIds.SessionId,
                    preMintedInputId: launchIds.InputId,
                    preMintedTurnId: launchIds.TurnId,
                    ct: ct);
            }
            catch
            {
                await req.AttachmentBinder.RollbackAsync(
                    projectId, launchIds.SessionId, launchIds.InputId, attachmentBinding, CancellationToken.None);
                throw;
            }
            sessionId = launch.SessionId;
        }


        if (existingRoute?.SessionId is null)
            sessionId = await req.Inbox.SetRouteSessionIdAsync(projectId, accepted.Id, sessionId!, ct);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var bindResult = await req.ThreadMapping.UpsertAsync(
                projectId, body.TeamId, connection.Id, body.ConversationId, rootTs,
                req.SenderSlackUserId, sessionId, rootTs, ct);
            sessionId = bindResult.SessionId;
            if (bindResult.AlreadyExisted)
                sessionId = await req.Inbox.SetRouteSessionIdAsync(projectId, accepted.Id, sessionId, ct);
            await req.ThreadLaunchReservations.BindSessionAsync(
                projectId,
                body.TeamId,
                connection.Id,
                body.ConversationId,
                rootTs,
                sessionId,
                ct);
        }

        await req.Services.GetRequiredService<SlackStatusProjection>().EnqueueReceivedAsync(
            projectId, connection.Id, req.Identity, body.ThreadTs, ct);
        if (launch is not null)
        {
            await EnqueueInitialLaunchStatusAsync(
                req.Services,
                req.Grains,
                projectId,
                connection,
                req.Identity,
                rootTs,
                launch,
                req.SenderSlackUserId,
                ct);
        }
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

    private static (string SessionId, string InputId, string TurnId) PreMintSlackLaunchIds(
        string projectId,
        SlackMessageIdentity identity)
    {
        var ownershipIdentity = $"{projectId}\nslack:{identity.WorkspaceTeamId}:{identity.ConversationId}:{identity.MessageTs}";
        return (
            $"agent-session-{AgentLaunchCoordinatorCodec.StableToken($"{ownershipIdentity}\nsession")}",
            AgentLaunchCoordinatorCodec.StableToken($"{ownershipIdentity}\ninput"),
            AgentLaunchCoordinatorCodec.StableToken($"{ownershipIdentity}\nturn"));
    }

    internal static string BuildAttachmentAck(
        string acknowledgement,
        IReadOnlyList<SlackIngressFile> files,
        SlackAttachmentBinding? binding)
    {
        if (binding is null || binding.Results.Count == 0)
            return acknowledgement;

        var accepted = binding.Results
            .Where(result => result.IsAccepted && result.Descriptor is not null)
            .Select(result => result.Descriptor!.OriginalFileName)
            .ToArray();
        var rejected = binding.Results
            .Select((result, index) => (Result: result, File: files[index]))
            .Where(item => !item.Result.IsAccepted)
            .Select(item => $"{item.File.Name} ({item.Result.RejectionReason}: {item.Result.RejectionMessage})")
            .ToArray();
        var parts = new List<string> { acknowledgement };
        if (accepted.Length > 0)
            parts.Add($"Files received: {string.Join(", ", accepted)}.");
        if (rejected.Length > 0)
            parts.Add($"Files not used: {string.Join("; ", rejected)}.");
        return string.Join(' ', parts);
    }

    private static string BuildLaunchAck(AgentStartupContext? startupContext, bool alreadyExisted)
    {
        if (alreadyExisted)
            return "This task was already accepted; execution is being resumed.";
        if (startupContext is null)
            return "Task accepted and queued for execution.";
        var detail = startupContext.Provenance.Truncated
            ? "Prior thread discussion is being used as background; the oldest messages were omitted to fit the bound."
            : "Prior thread discussion is being used as background.";
        return "Task accepted and queued for execution. " + detail;
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

        if (IsBackpressured(connection))
            return ApiResults.Ok(new
            {
                kind = "backpressured",
                reason = "This Slack Connection is backpressured; retry after pending deliveries drain.",
            });

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
        catch (SlackProviderInboxCapacityExceededException)
        {
            return ApiResults.Ok(new
            {
                kind = "backpressured",
                reason = "This Slack Connection is backpressured; retry after pending deliveries drain.",
            });
        }

        if (!accepted.AlreadyExisted)
            await req.Connections.ClearOfflineGapIfSetAsync(projectId, connection.Id, ct);

        var idempotencyKey = $"slack-thread-followup:{body.TeamId}:{body.ConversationId}:{body.MessageTs}";
        var followupResult = await RouteFollowupAsync(
            projectId,
            connection,
            req.Identity,
            body.Files,
            sessionId,
            prompt,
            idempotencyKey,
            BuildSlackInputProvenance(connection.Id, body, body.ThreadTs),
            req.AttachmentBinder,
            req.Grains,
            req.FollowupDispatcher,
            ct);
        await req.Services.GetRequiredService<SlackStatusProjection>().EnqueueReceivedAsync(
            projectId, connection.Id, req.Identity, body.ThreadTs, ct);
        if (followupResult.Status is "queued" or "executing")
        {
            var stopAction = await req.Services.GetRequiredService<SlackTurnControlService>().CreateStopActionAsync(
                connection,
                followupResult.SessionId,
                followupResult.TurnId,
                followupResult.InputId,
                $"agent-session-followup:{followupResult.SessionId}:{followupResult.TurnId}:progress",
                req.SenderSlackUserId,
                req.Identity,
                body.ThreadTs,
                ct);
            var blocks = await BuildSessionStatusBlocksAsync(
                req.Services,
                projectId,
                followupResult.SessionId,
                stopAction?.Blocks);
            await req.Services.GetRequiredService<SlackStatusProjection>().EnqueueWorkingAsync(
                projectId,
                connection.Id,
                req.Identity,
                body.ThreadTs,
                $"agent-session-followup:{followupResult.SessionId}:{followupResult.TurnId}:progress",
                blocks,
                ct);
        }
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
    AgentConnectionStore Connections,
    SlackDmSessionMappingStore DmMapping,
    AgentQuerier Agents,
    SlackOwnerClaimService Claims,
    SlackProviderInboxStore Inbox,
    SlackOutboxStore Outbox,
    IAgentLauncher Launcher,
    SlackAttachmentInputBinder AttachmentBinder,
    IGrainFactory Grains,
    AgentSessionFollowupDispatcher FollowupDispatcher,
    IServiceProvider Services)
{
    public static HandleDmIngressRequest From(
        string projectId,
        Agent.Domain.AgentConnection connection,
        SlackMessageIdentity identity,
        string senderSlackUserId,
        SlackIngressBody body,
        AgentConnectionStore connections,
        SlackDmSessionMappingStore dmMapping,
        AgentQuerier agents,
        SlackOwnerClaimService claims,
        SlackProviderInboxStore inbox,
        SlackOutboxStore outbox,
        IAgentLauncher launcher,
        SlackAttachmentInputBinder attachmentBinder,
        IGrainFactory grains,
        AgentSessionFollowupDispatcher followupDispatcher,
        IServiceProvider services) =>
        new(projectId, connection, identity, senderSlackUserId, body,
            connections, dmMapping, agents, claims, inbox, outbox,
            launcher, attachmentBinder, grains, followupDispatcher, services);

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
    SlackThreadLaunchReservationStore ThreadLaunchReservations,
    SlackAmbiguousPromptStore AmbiguousPrompts,
    AgentSessionQuerier Sessions,
    AgentQuerier Agents,
    SlackOwnerClaimService Claims,
    SlackConnectionAccessDecider AccessDecider,
    SlackProviderInboxStore Inbox,
    SlackOutboxStore Outbox,
    IAgentLauncher Launcher,
    SlackAttachmentInputBinder AttachmentBinder,
    IGrainFactory Grains,
    AgentSessionFollowupDispatcher FollowupDispatcher,
    ISecretStore Secrets,
    SlackThreadHistoryReader ThreadHistory,
    IOptions<SlackProviderOptions> SlackProviderOptions,
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
        SlackThreadLaunchReservationStore threadLaunchReservations,
        SlackAmbiguousPromptStore ambiguousPrompts,
        AgentSessionQuerier sessions,
        AgentQuerier agents,
        SlackOwnerClaimService claims,
        SlackConnectionAccessDecider accessDecider,
        SlackProviderInboxStore inbox,
        SlackOutboxStore outbox,
        IAgentLauncher launcher,
        SlackAttachmentInputBinder attachmentBinder,
        IGrainFactory grains,
        AgentSessionFollowupDispatcher followupDispatcher,
        ISecretStore secrets,
        SlackThreadHistoryReader threadHistory,
        IOptions<SlackProviderOptions> slackProviderOptions,
        IServiceProvider services) =>
        new(projectId, connection, identity, senderSlackUserId, body,
            connections, threadMapping, threadLaunchReservations, ambiguousPrompts,
            sessions, agents, claims, accessDecider, inbox, outbox,
            launcher, attachmentBinder, grains, followupDispatcher,
            secrets, threadHistory, slackProviderOptions, services);
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

public sealed class SlackConnectionManageAccessBody
{
    public string? AccessPolicy { get; init; }
    public IReadOnlyList<string>? AllowMembers { get; init; }
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
    public IReadOnlyList<SlackIngressFile> Files { get; init; } = Array.Empty<SlackIngressFile>();
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
    public string AdapterId { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public SlackProviderMessageIdentity? ProviderMessageIdentity { get; init; }
}
