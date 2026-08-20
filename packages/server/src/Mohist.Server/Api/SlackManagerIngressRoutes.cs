using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Api;

public static class SlackManagerIngressRoutes
{
    public static WebApplication MapSlackManagerIngressRoutes(this WebApplication app)
    {
        var manager = app.MapGroup("/api/slack-manager");

        manager.MapPost("/setup", async (
            HttpContext context,
            SlackManagerSetupBody body,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
        {
            if (body is null
                || string.IsNullOrWhiteSpace(body.WorkspaceTeamId)
                || string.IsNullOrWhiteSpace(body.ManagerAppId)
                || string.IsNullOrWhiteSpace(body.ManagerBotUserId))
                return ApiResults.BadRequest(
                    "workspaceTeamId, managerAppId, and managerBotUserId are required.");
            if (HasCredentialAddressOverride(body.ExtensionData))
                return ApiResults.BadRequest(
                    "Credential address fields are not supported by the Manager API.",
                    "credential_address_not_supported");
            if (HasClientIdentity(body.ExtensionData))
                return ApiResults.BadRequest(
                    "Client identity fields are not supported by the Manager API.",
                    "client_identity_not_supported");

            try
            {
                return ApiResults.Ok(await service.SetupAsync(new SlackManagerSetupRequest(
                    body.WorkspaceTeamId,
                    body.ManagerAppId,
                    body.ManagerBotUserId,
                    body.TransportKind ?? SlackManagerTransportKind.Socket,
                    body.Readiness ?? SlackManagerReadiness.Ready), ct));
            }
            catch (SlackManagerConflictException ex)
            {
                return ApiResults.Conflict(ex.Message, ex.Code);
            }
            catch (SlackManagerValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, ex.Code);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_manager_setup");
            }
            catch (InvalidOperationException ex) when (ex.Message is
                "The Manager App identity cannot be changed after setup."
                or "The workspace enrollment disappeared during setup."
                or "The workspace enrollment could not be recovered after a concurrent setup.")
            {
                var code = ex.Message.StartsWith("The Manager App identity", StringComparison.Ordinal)
                    ? "manager_identity_conflict"
                    : "manager_enrollment_unavailable";
                return ApiResults.Conflict(ex.Message, code);
            }
        });

        manager.MapGet("/status", async (
            HttpContext context,
            string? workspaceTeamId,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(workspaceTeamId))
                return ApiResults.BadRequest("workspaceTeamId is required.");
            var status = await service.GetStatusAsync(workspaceTeamId, ct);
            return status is null
                ? ApiResults.NotFound("The workspace has not been enrolled.")
                : ApiResults.Ok(status);
        });

        manager.MapPost("/setup/configuration", async (
            HttpContext context,
            SlackControlSetupConfigurationBody body,
            SlackManagerSetupOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var guard = RequireLoopback(context);
            if (guard is not null) return guard;
            if (body is null
                || string.IsNullOrWhiteSpace(body.WorkspaceTeamId)
                || string.IsNullOrWhiteSpace(body.ConfigurationAccessToken)
                || string.IsNullOrWhiteSpace(body.ConfigurationRefreshToken))
                return ApiResults.BadRequest(
                    "workspaceTeamId, configurationAccessToken, and configurationRefreshToken are required.");
            if (HasCredentialAddressOverride(body.ExtensionData))
                return ApiResults.BadRequest(
                    "Credential address fields are not supported by the control-plane API.",
                    "credential_address_not_supported");
            try
            {
                return ApiResults.Ok(await orchestrator.SupplyConfigurationAsync(new(
                    body.WorkspaceTeamId,
                    new(body.ConfigurationAccessToken, body.ConfigurationRefreshToken)), ct));
            }
            catch (SlackManagerConflictException ex)
            {
                return ApiResults.Conflict(ex.Message, ex.Code);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_configuration_credentials");
            }
        });

        manager.MapPost("/setup/runtime-credentials", async (
            HttpContext context,
            SlackControlSetupRuntimeBody body,
            SlackManagerSetupOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var guard = RequireLoopback(context);
            if (guard is not null) return guard;
            if (body is null
                || string.IsNullOrWhiteSpace(body.WorkspaceTeamId)
                || string.IsNullOrWhiteSpace(body.BotToken)
                || string.IsNullOrWhiteSpace(body.AppLevelToken))
                return ApiResults.BadRequest(
                    "workspaceTeamId, botToken, and appLevelToken are required.");
            if (HasCredentialAddressOverride(body.ExtensionData))
                return ApiResults.BadRequest(
                    "Credential address fields are not supported by the control-plane API.",
                    "credential_address_not_supported");
            try
            {
                return ApiResults.Ok(await orchestrator.SupplyRuntimeCredentialsAsync(new(
                    body.WorkspaceTeamId, body.BotToken, body.AppLevelToken), ct));
            }
            catch (SlackManagerConflictException ex)
            {
                return ApiResults.Conflict(ex.Message, ex.Code);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_runtime_credentials");
            }
        });

        manager.MapGet("/setup/progress", async (
            HttpContext context,
            string? workspaceTeamId,
            SlackManagerSetupOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(workspaceTeamId))
                return ApiResults.BadRequest("workspaceTeamId is required.");
            var progress = await orchestrator.GetProgressAsync(workspaceTeamId, ct);
            return progress is null
                ? ApiResults.NotFound("The workspace has not started setup.")
                : ApiResults.Ok(progress);
        });

        manager.MapPost("/ingress", async (
            HttpContext context,
            SlackManagerIngressBody body,
            SlackManagerIngressService ingress,
            SlackAdapterLeaseService leases,
            ISlackAdapterOperatorAuthenticator auth,
            CancellationToken ct) =>
        {
            var operatorId = await auth.AuthenticateAsync(context, ct);
            if (operatorId is null)
                return ApiResults.Fail("Manager ingress requires an operator credential.", 403, "operator_credential_required");
            if (body is null
                || string.IsNullOrWhiteSpace(body.AppId)
                || string.IsNullOrWhiteSpace(body.WorkspaceTeamId)
                || string.IsNullOrWhiteSpace(body.ConversationId)
                || string.IsNullOrWhiteSpace(body.MessageTs)
                || (string.IsNullOrWhiteSpace(body.SenderSlackUserId)
                    && !string.Equals(body.SenderKind?.Trim(), "bot", StringComparison.OrdinalIgnoreCase)))
                return ApiResults.BadRequest(
                    "appId, workspaceTeamId, conversationId, messageTs, and a senderSlackUserId for non-Bot events are required.");
            if (HasClientIdentity(body.ExtensionData))
                return ApiResults.BadRequest(
                    "Client identity fields are not supported by the Manager API.",
                    "client_identity_not_supported");

            if (!await leases.ValidateManagerRuntimeLeaseByTeamAsync(
                    operatorId, body.WorkspaceTeamId, body.LeaseId, body.AdapterId, ct))
            {
                return ApiResults.Conflict(
                    "The runtime Socket lease is stale, expired, or unknown; acquire a new lease.",
                    "lease_stale_or_expired");
            }

            var result = await ingress.AcceptAsync(new SlackManagerIngressMessage(
                body.AppId,
                new SlackMessageIdentity(body.WorkspaceTeamId, body.ConversationId, body.MessageTs),
                body.SenderSlackUserId,
                body.Text ?? string.Empty,
                body.IsDirectMessage,
                body.ThreadTs,
                body.SenderKind,
                body.AuthorBot), ct);
            return ApiResults.Ok(result);
        });

        return app;
    }

    private static IResult? RequireLoopback(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } remoteAddress
            || !IPAddress.IsLoopback(remoteAddress))
            return ApiResults.Fail(
                "Slack control-plane secret operations are only available over loopback.",
                403, "loopback_required");
        return null;
    }

    private static bool HasClientIdentity(IReadOnlyDictionary<string, JsonElement>? extensionData) =>
        extensionData?.Keys.Any(key =>
            key.Equals("managerExternalId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("actor", StringComparison.OrdinalIgnoreCase)) == true;

    private static bool HasCredentialAddressOverride(IReadOnlyDictionary<string, JsonElement>? extensionData) =>
        extensionData?.Keys.Any(key =>
            key.Equals("projectId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("connectionId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("managerCredentialRef", StringComparison.OrdinalIgnoreCase)
            || key.Equals("secretAddress", StringComparison.OrdinalIgnoreCase)
            || key.Equals("secretKind", StringComparison.OrdinalIgnoreCase)) == true;
}

public sealed class SlackManagerSetupBody
{
    public string WorkspaceTeamId { get; init; } = string.Empty;
    public string ManagerAppId { get; init; } = string.Empty;
    public string ManagerBotUserId { get; init; } = string.Empty;
    public string? TransportKind { get; init; }
    public string? Readiness { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class SlackControlSetupConfigurationBody
{
    public string WorkspaceTeamId { get; init; } = string.Empty;
    public string ConfigurationAccessToken { get; init; } = string.Empty;
    public string ConfigurationRefreshToken { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class SlackControlSetupRuntimeBody
{
    public string WorkspaceTeamId { get; init; } = string.Empty;
    public string BotToken { get; init; } = string.Empty;
    public string AppLevelToken { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class SlackManagerIngressBody
{
    public string AppId { get; init; } = string.Empty;
    public string WorkspaceTeamId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string MessageTs { get; init; } = string.Empty;
    public string? SenderSlackUserId { get; init; }
    public string? SenderKind { get; init; }
    public SlackBotAuthorMetadata? AuthorBot { get; init; }
    public string? Text { get; init; }
    public bool IsDirectMessage { get; init; }
    public string? ThreadTs { get; init; }
    public string LeaseId { get; init; } = string.Empty;
    public string AdapterId { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
