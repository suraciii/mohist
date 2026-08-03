using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure.Security;
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
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(context.Request.Headers))
                return ApiResults.Fail("Manager setup requires an operator credential.", 403, "operator_credential_required");
            if (body is null
                || string.IsNullOrWhiteSpace(body.WorkspaceTeamId)
                || string.IsNullOrWhiteSpace(body.ManagerAppId)
                || string.IsNullOrWhiteSpace(body.ManagerBotUserId)
                || string.IsNullOrWhiteSpace(body.ManagerCredentialRef))
                return ApiResults.BadRequest(
                    "workspaceTeamId, managerAppId, managerBotUserId, and managerCredentialRef are required.");
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
                    body.ManagerCredentialRef,
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
        });

        manager.MapGet("/status", async (
            HttpContext context,
            string? workspaceTeamId,
            SlackManagerApplicationService service,
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(context.Request.Headers))
                return ApiResults.Fail("Manager status requires an operator credential.", 403, "operator_credential_required");
            if (string.IsNullOrWhiteSpace(workspaceTeamId))
                return ApiResults.BadRequest("workspaceTeamId is required.");
            var status = await service.GetStatusAsync(workspaceTeamId, ct);
            return status is null
                ? ApiResults.NotFound("The workspace has not been enrolled.")
                : ApiResults.Ok(status);
        });

        manager.MapPost("/ingress", async (
            HttpContext context,
            SlackManagerIngressBody body,
            SlackManagerIngressService ingress,
            OperatorCredential credential,
            CancellationToken ct) =>
        {
            if (!credential.Authorizes(context.Request.Headers))
                return ApiResults.Fail("Manager ingress requires an operator credential.", 403, "operator_credential_required");
            if (body is null
                || string.IsNullOrWhiteSpace(body.AppId)
                || string.IsNullOrWhiteSpace(body.WorkspaceTeamId)
                || string.IsNullOrWhiteSpace(body.ConversationId)
                || string.IsNullOrWhiteSpace(body.MessageTs)
                || string.IsNullOrWhiteSpace(body.SenderSlackUserId))
                return ApiResults.BadRequest(
                    "appId, workspaceTeamId, conversationId, messageTs, and senderSlackUserId are required.");
            if (HasClientIdentity(body.ExtensionData))
                return ApiResults.BadRequest(
                    "Client identity fields are not supported by the Manager API.",
                    "client_identity_not_supported");

            var result = await ingress.AcceptAsync(new SlackManagerIngressMessage(
                body.AppId,
                new SlackMessageIdentity(body.WorkspaceTeamId, body.ConversationId, body.MessageTs),
                body.SenderSlackUserId,
                body.Text ?? string.Empty,
                body.IsDirectMessage,
                body.ThreadTs), ct);
            return ApiResults.Ok(result);
        });

        return app;
    }

    private static bool HasClientIdentity(IReadOnlyDictionary<string, JsonElement>? extensionData) =>
        extensionData?.Keys.Any(key =>
            key.Equals("managerExternalId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("actor", StringComparison.OrdinalIgnoreCase)) == true;
}

public sealed class SlackManagerSetupBody
{
    public string WorkspaceTeamId { get; init; } = string.Empty;
    public string ManagerAppId { get; init; } = string.Empty;
    public string ManagerBotUserId { get; init; } = string.Empty;
    public string ManagerCredentialRef { get; init; } = string.Empty;
    public string? TransportKind { get; init; }
    public string? Readiness { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class SlackManagerIngressBody
{
    public string AppId { get; init; } = string.Empty;
    public string WorkspaceTeamId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string MessageTs { get; init; } = string.Empty;
    public string SenderSlackUserId { get; init; } = string.Empty;
    public string? Text { get; init; }
    public bool IsDirectMessage { get; init; }
    public string? ThreadTs { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
