using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Slack;
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
using Mohist.Server.Workspace.Services;

namespace Mohist.Server.Api;

public static partial class SlackConnectionRoutes
{
    private static bool IsBackpressured(AgentConnection connection) =>
        connection.ConnectionHealth == ConnectionHealthKind.Degraded
        && SlackConnectionBackpressureReasons.IsBackpressureReason(connection.HealthReason);

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
            SlackSetupVerifier verifier,
            AgentReadinessService readiness,
            CancellationToken ct) =>
        {
            var projectId = context.GetResolvedProject().Id;
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");

            var ownerAvailability = ProbeOwnerAvailability(connection);
            var agent = await agents.GetByIdAsync(projectId, connection.AgentId);
            var agentReadiness = agent is null
                ? connection.AgentReadiness
                : AgentReadinessDeriver.Derive(agent.AgentConfig);
            var agentExecutability = agent is null
                ? null
                : await readiness.GetAsync(projectId, agent, ct);
            var result = ConnectionDiagnostic.Compute(
                connection,
                new DiagnosticInputs(
                    verifier.IsAdapterOnline(connection),
                    ownerAvailability,
                    agentReadiness,
                    agent?.Name,
                    agentExecutability));
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
                    "Connection identity is already bound. Re-run `mo slack install-agent` and re-supply credentials to rotate them.",
                    "identity_already_bound");
            await secrets.StoreAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.AppToken), Encoding.UTF8.GetBytes(body.AppToken), ct);
            await secrets.StoreAsync(new SecretStoreAddress(projectId, connectionId, SecretKind.BotToken), Encoding.UTF8.GetBytes(body.BotToken), ct);
            var updated = await connections.UpdateAsync(projectId, connectionId,
                new HashSet<string>(StringComparer.Ordinal) { "setupProgress" },
                setupProgress: SetupProgressKind.WaitingForSlackService, ct: ct);
            return ApiResults.Ok(updated);
        });

        management.MapPost("/{connectionId}/claim-owner", async (HttpContext context, string connectionId, SlackOwnerClaimService claims, AgentConnectionStore connections, CancellationToken ct) =>
        {
            try
            {
                var projectId = context.GetResolvedProject().Id;
                var connection = await connections.GetAsync(projectId, connectionId, ct);
                if (connection is null)
                    return ApiResults.NotFound("Slack Connection was not found.");
                var code = await claims.GenerateAsync(projectId, connectionId, ct: ct);
                return ApiResults.Ok(new { code = code.Value, expiresAt = code.ExpiresAt, botName = ClaimCodeBotName(connection) });
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.BadRequest(ex.Message, "claim_unavailable");
            }
        });

        management.MapPost("/{connectionId}/transfer-owner", async (HttpContext context, string connectionId, SlackOwnerClaimService claims, AgentConnectionStore connections, CancellationToken ct) =>
        {
            try
            {
                var projectId = context.GetResolvedProject().Id;
                var connection = await connections.GetAsync(projectId, connectionId, ct);
                if (connection is null)
                    return ApiResults.NotFound("Slack Connection was not found.");
                var code = await claims.GenerateAsync(
                    projectId,
                    connectionId,
                    Mohist.Server.Infrastructure.Data.Slack.SlackOwnerClaimCodeKinds.Transfer,
                    ct: ct);
                return ApiResults.Ok(new { code = code.Value, expiresAt = code.ExpiresAt, botName = ClaimCodeBotName(connection) });
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

        MapAgentReplyRoute(management);

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
            SlackThreadHistoryReader threadHistory,
            IOptions<SlackProviderOptions> slackProviderOptions,
            SlackAdapterLeaseService leases,
            SlackManagedBotAdmissionService managedBotAdmission,
            ISlackAdapterOperatorAuthenticator auth,
            CancellationToken ct) =>
        {
            var operatorId = await auth.AuthenticateAsync(http, ct);
            if (operatorId is null)
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            var projectId = http.GetResolvedProject().Id;
            var leaseValid = await leases.ValidateRuntimeLeaseAsync(
                operatorId,
                new SlackLeaseTargetRef.Connection(projectId, connectionId),
                body?.LeaseId ?? string.Empty,
                body?.AdapterId ?? string.Empty,
                ct);
            if (!leaseValid)
            {
                // Disabling a Connection removes it from the lease target
                // view immediately. A Bot event already in flight can still
                // be safely acknowledged when its author is managed, but
                // every other stale-lease request keeps the existing failure.
                var disabledConnection = await connections.GetAsync(projectId, connectionId, ct);
                if (disabledConnection?.DesiredState == DesiredStateKind.Disabled
                    && body is not null)
                {
                    if (ValidateIngressAppIdentity(disabledConnection, body) is { } disabledAppIdentityError)
                        return disabledAppIdentityError;
                    var disabledIdentity = new SlackMessageIdentity(
                        body.TeamId, body.ConversationId, body.MessageTs);
                    var disabledIdentityError = disabledIdentity.Validate();
                    if (disabledIdentityError.Length != 0)
                        return ApiResults.BadRequest(disabledIdentityError, "invalid_slack_identity");
                    if (!string.Equals(body.TeamId, disabledConnection.WorkspaceTeamId, StringComparison.Ordinal))
                        return ApiResults.BadRequest("The Slack workspace does not match this Connection.", "workspace_mismatch");
                    var disabledAdmission = await managedBotAdmission.EvaluateAsync(
                        disabledIdentity.WorkspaceTeamId,
                        body.SenderKind,
                        body.AuthorBot,
                        ct);
                    if (disabledAdmission.IsManaged)
                        return ApiResults.Ok(new { kind = "ignored" });
                }

                return LeaseStaleOrExpired();
            }
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");
            if (ValidateIngressAppIdentity(connection, body!) is { } appIdentityError)
                return appIdentityError;
            var identity = new SlackMessageIdentity(body!.TeamId, body.ConversationId, body.MessageTs);
            var identityError = identity.Validate();
            if (identityError.Length != 0)
                return ApiResults.BadRequest(identityError, "invalid_slack_identity");
            if (!string.Equals(body.TeamId, connection.WorkspaceTeamId, StringComparison.Ordinal))
                return ApiResults.BadRequest("The Slack workspace does not match this Connection.", "workspace_mismatch");

            var managedAdmission = await managedBotAdmission.EvaluateAsync(
                identity.WorkspaceTeamId,
                body.SenderKind,
                body.AuthorBot,
                ct);
            if (managedAdmission.IsManaged)
                return ApiResults.Ok(new { kind = "ignored" });

            var existingNudge = await outbox.FindByDispatchRefAsync(
                projectId,
                connection.Id,
                SlackOutboxKinds.UserAction,
                SlackAdmissionService.DispatchRef(connection, identity),
                ct);
            if (existingNudge is not null)
            {
                var payload = SlackDeliveryPayload.Parse(existingNudge.PayloadJson);
                return ApiResults.Ok(new
                {
                    kind = payload.ResponseKind ?? "admission_nudge",
                    reason = payload.Text ?? payload.FallbackText,
                    responseOwner = SlackIngressResponseOwners.Server,
                });
            }

            if (connection.DesiredState == DesiredStateKind.Disabled)
            {
                try
                {
                    var discarded = await inbox.AcceptAsync(
                        new SlackProviderInboxDraft(projectId, connection.Id, identity,
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

            var senderKind = SlackChannelIngressPolicy.NormalizeSenderKind(body.SenderKind);
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
                        new SlackLeaseContext(
                            operatorId, body.LeaseId, body.AdapterId,
                            (targetRef, leaseCt) => leases.ResolveRuntimeLeaseBotTokenAsync(
                                operatorId, targetRef, body.LeaseId, body.AdapterId, leaseCt)),
                        threadHistory, slackProviderOptions,
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

        group.MapPost("/channel-archive", async (
            HttpContext http,
            string connectionId,
            SlackChannelArchiveBody body,
            AgentConnectionStore connections,
            SlackAdapterLeaseService leases,
            ISlackAdapterOperatorAuthenticator auth,
            InteractionWorkspaceProvisioner provisioner,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var operatorId = await auth.AuthenticateAsync(http, ct);
            if (operatorId is null)
                return ApiResults.Fail("Slack adapter authentication is required.", 403, "operator_credential_required");
            var projectId = http.GetResolvedProject().Id;
            if (!await leases.ValidateRuntimeLeaseAsync(
                    operatorId,
                    new SlackLeaseTargetRef.Connection(projectId, connectionId),
                    body?.LeaseId ?? string.Empty,
                    body?.AdapterId ?? string.Empty,
                    ct))
            {
                return LeaseStaleOrExpired();
            }
            if (body is null || string.IsNullOrWhiteSpace(body.TeamId) || string.IsNullOrWhiteSpace(body.ConversationId))
                return ApiResults.BadRequest("teamId and conversationId are required.", "invalid_archive_target");
            var connection = await connections.GetAsync(projectId, connectionId, ct);
            if (connection is null)
                return ApiResults.NotFound("Slack Connection was not found.");
            if (!string.Equals(body.TeamId, connection.WorkspaceTeamId, StringComparison.Ordinal))
                return ApiResults.BadRequest("The Slack workspace does not match this Connection.", "workspace_mismatch");

            var archived = await provisioner.ArchiveSlackChannelAsync(
                projectId, body.TeamId, body.ConversationId, timeProvider.GetUtcNow());
            return ApiResults.Ok(new { archived });
        });

        group.MapPost("/deliveries/claim", async (
            HttpContext http,
            string connectionId,
            DeliveryClaimBody body,
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
                    body?.LeaseId ?? string.Empty,
                    body?.AdapterId ?? string.Empty,
                    ct))
            {
                return LeaseStaleOrExpired();
            }
            var entry = await outbox.ClaimAsync(projectId, connectionId, body?.AdapterId ?? string.Empty, ct);
            return entry is null ? ApiResults.Ok<object?>(null) : ApiResults.Ok(entry);
        });

        group.MapPost("/deliveries/claim-uncertain", async (
            HttpContext http,
            string connectionId,
            DeliveryClaimBody body,
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
                    body?.LeaseId ?? string.Empty,
                    body?.AdapterId ?? string.Empty,
                    ct))
            {
                return LeaseStaleOrExpired();
            }
            var entry = await outbox.ClaimUncertainAsync(projectId, connectionId, body?.AdapterId ?? string.Empty, ct);
            return entry is null ? ApiResults.Ok<object?>(null) : ApiResults.Ok(entry);
        });

        group.MapPost("/deliveries/ack", async (
            HttpContext http,
            string connectionId,
            DeliveryAckBody body,
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
                    new SlackLeaseTargetRef.Connection(projectId, connectionId ?? string.Empty),
                    body?.LeaseId ?? string.Empty,
                    body?.AdapterId ?? string.Empty,
                    ct))
            {
                return LeaseStaleOrExpired();
            }
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

    private static IResult LeaseStaleOrExpired() =>
        ApiResults.Conflict(
            "The runtime Socket lease is stale, expired, or unknown; acquire a new lease.",
            "lease_stale_or_expired");

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
        string? threadTs = null,
        JsonElement? blocks = null) =>
        await outbox.EnqueueRequiredAsync(new SlackOutboxDraft(
            projectId,
            connection.Id,
            connection.WorkspaceTeamId,
            conversationId,
            SlackOutboxKinds.UserAction,
            dispatchRef,
            JsonSerializer.Serialize(new SlackDeliveryPayload(
                SlackDeliveryOperations.PostMessage,
                text,
                ClientMessageId: dispatchRef,
                FallbackText: text,
                Blocks: blocks)),
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
            "rejected" => "Could not continue this Session safely. Please try again after its execution state is reconciled.",
            _ => "Continuing.",
        };
    }

    private static string BuildFollowupRejectionReply(bool isDirectMessage) =>
        isDirectMessage
            ? "This Session cannot continue automatically because its execution state is unresolved. Reconcile or reset it in Mohist, then send the message again."
            : "This Session cannot continue automatically because its execution state is unresolved. Reconcile or reset it in Mohist, then mention the Bot again.";

    private static async Task EnqueueFollowupRejectionAsync(
        SlackOutboxStore outbox,
        string projectId,
        Agent.Domain.AgentConnection connection,
        SlackMessageIdentity identity,
        string? threadTs,
        bool isDirectMessage,
        CancellationToken ct) =>
        await EnqueueRequiredReplyAsync(
            outbox,
            projectId,
            connection,
            identity.ConversationId,
            BuildFollowupRejectionReply(isDirectMessage),
            $"slack-followup-rejected:{identity.AsKey()}",
            ct,
            threadTs);

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

    /// <summary>
    /// Crafts the Bot's reply for a successful first-time owner claim: the
    /// confirmation plus a self-contained first-use guide (DM tasks, channel
    /// mention, thread follow-ups) so the user can start without reading docs.
    /// </summary>
    internal static string BuildOwnerClaimedReply() =>
        "Owner claimed successfully. Here's how to get started:\n" +
        "• Send me a task right here in this DM.\n" +
        "• Invite me to a channel and @ me there to assign work.\n" +
        "• Reply in the thread of my message to follow up on a task.";

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

    internal static string ProbeOwnerAvailability(AgentConnection connection)
    {
        if (connection.OwnerSlackUserId is null)
            return OwnerAvailabilityKind.NotConfigured;
        return OwnerAvailabilityKind.Unknown;
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
            await EnqueueReplyAsync(req.Outbox, projectId, connection, body.ConversationId, BuildOwnerClaimedReply(), null, ct, body.ThreadTs);
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

        var admissionService = req.Services.GetRequiredService<SlackAdmissionService>();
        var currentSessionId = isNewTask
            ? null
            : await req.DmMapping.GetCurrentSessionIdAsync(projectId, connection.Id, body.ConversationId, ct);
        var isNewWork = isNewTask || string.IsNullOrWhiteSpace(currentSessionId);
        if (isNewWork)
        {
            var existingNudge = await admissionService.FindExistingNudgeAsync(projectId, connection, req.Identity, ct);
            if (existingNudge is not null)
                return AdmissionResponse(existingNudge);
        }

        var agent = await req.Agents.GetByIdAsync(projectId, connection.AgentId);
        if (agent is null)
            return ApiResults.Fail("The Agent bound to this Connection no longer exists.", 409, "agent_not_found");

        if (isNewWork)
        {
            var admission = await admissionService
                .AdmitNewWorkAsync(projectId, connection, agent, req.Identity, body.ThreadTs, ct);
            if (!admission.Admitted)
                return ApiResults.Ok(new
                {
                    kind = admission.Kind,
                    reason = admission.Reason,
                    responseOwner = admission.ResponseOwner,
                });
        }
        else if (IsBackpressured(connection))
        {
            return ApiResults.Ok(new
            {
                kind = "backpressured",
                reason = SlackAdmissionMessages.Backpressured,
                responseOwner = SlackIngressResponseOwners.Adapter,
            });
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
                reason = SlackAdmissionMessages.Backpressured,
                responseOwner = SlackIngressResponseOwners.Adapter,
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
                var launchIds = SlackChannelLaunchService.PreMintSlackLaunchIds(projectId, req.Identity);
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
                    var time = req.Services.GetRequiredService<TimeProvider>();
                    var workspaceName = await req.Services.GetRequiredService<InteractionWorkspaceProvisioner>()
                        .EnsureSlackWorkspaceAsync(projectId, body.TeamId, body.ConversationId, time.GetUtcNow());
                    launch = await req.Launcher.LaunchConnectionAsync(
                        agent!,
                        launchPrompt,
                        new ConnectionLaunchOrigin(
                            connection.Id, body.TeamId, req.SenderSlackUserId, body.ConversationId, body.MessageTs, body.ThreadTs),
                        workspaceName: workspaceName,
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

        route = await RecoverRetrySafeDmLaunchAsync(req, accepted, route, ct);

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
            allowPendingInitialLaunch: true,
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
        var responseOwner = SlackIngressResponseOwners.None;
        if (followupResult.Status == "rejected")
        {
            await EnqueueFollowupRejectionAsync(
                req.Outbox, projectId, connection, req.Identity, body.ThreadTs, isDirectMessage: true, ct);
            responseOwner = SlackIngressResponseOwners.Server;
        }
        await req.Inbox.MarkDispatchedAsync(projectId, accepted.Id, ct);
        return ApiResults.Ok(new
        {
            kind = followupResult.Kind,
            sessionId = followupResult.SessionId,
            inputId = followupResult.InputId,
            turnId = followupResult.TurnId,
            followup = true,
            responseOwner,
        });
    }

    internal static string BuildAttachmentAck(
        string acknowledgement,
        IReadOnlyList<SlackIngressFile> files,
        SlackAttachmentBinding? binding) =>
        SlackChannelLaunchService.BuildAttachmentAck(acknowledgement, files, binding);

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

    private static string? ClaimCodeBotName(AgentConnection connection) =>
        string.IsNullOrWhiteSpace(connection.BotName) ? connection.VerifiedBotName : connection.BotName;

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
                reason = SlackAdmissionMessages.Backpressured,
                responseOwner = SlackIngressResponseOwners.Adapter,
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
                reason = SlackAdmissionMessages.Backpressured,
                responseOwner = SlackIngressResponseOwners.Adapter,
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
            allowPendingInitialLaunch: false,
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
        var responseOwner = SlackIngressResponseOwners.None;
        if (followupResult.Status == "rejected")
        {
            await EnqueueFollowupRejectionAsync(
                req.Outbox, projectId, connection, req.Identity, body.ThreadTs, isDirectMessage: false, ct);
            responseOwner = SlackIngressResponseOwners.Server;
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
            responseOwner,
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
    SlackLeaseContext LeaseContext,
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
        SlackLeaseContext leaseContext,
        SlackThreadHistoryReader threadHistory,
        IOptions<SlackProviderOptions> slackProviderOptions,
        IServiceProvider services) =>
        new(projectId, connection, identity, senderSlackUserId, body,
            connections, threadMapping, threadLaunchReservations, ambiguousPrompts,
            sessions, agents, claims, accessDecider, inbox, outbox,
            launcher, attachmentBinder, grains, followupDispatcher,
            leaseContext, threadHistory, slackProviderOptions, services);
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

public sealed partial class SlackReplyBody
{
    public string ConversationId { get; init; } = string.Empty;
    public string? ThreadTs { get; init; }
    public string? Text { get; init; }
    public string? ImageUrl { get; init; }
    public string? FileName { get; init; }
    public string? FileContentBase64 { get; init; }
}

public sealed class SlackChannelArchiveBody
{
    public string TeamId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string LeaseId { get; init; } = string.Empty;
    public string AdapterId { get; init; } = string.Empty;
}
