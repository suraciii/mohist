using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Api;

public static class SlackManagerRoutes
{
    public static WebApplication MapSlackManagerRoutes(this WebApplication app)
    {
        var manager = app.MapGroup("/api/projects/{projectRef}/slack-manager")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        manager.MapGet("/agents", async (
            HttpContext context,
            string? workspaceTeamId,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
            ApiResults.Ok(await service.ListAgentOptionsAsync(
                context.GetResolvedProject().Id, workspaceTeamId ?? string.Empty, ct)));

        manager.MapPost("/install-agent", async (
            HttpContext context,
            SlackControlInstallAgentBody body,
            SlackInstallAgentService service,
            CancellationToken ct) =>
        {
            if (body is not null && HasCredentialAddressOverride(body.ExtensionData))
                return ApiResults.BadRequest(
                    "Credential address fields are not supported by the control-plane API.",
                    "credential_address_not_supported");
            if (body is null || string.IsNullOrWhiteSpace(body.AgentId))
                return ApiResults.BadRequest("agentId is required.");
            var identityError = RejectClientIdentity(context, body.ExtensionData);
            if (identityError is not null) return identityError;
            try
            {
                var projectId = context.GetResolvedProject().Id;
                var progress = await service.InstallAsync(projectId, body.AgentId, ct);
                return ApiResults.Ok(PublicInstallProgress(progress));
            }
            catch (SlackManagerConflictException ex)
            {
                return ApiResults.Conflict(ex.Message, ex.Code);
            }
        });

        manager.MapPost("/install-agent/credentials", async (
            HttpContext context,
            SlackControlInstallAgentCredentialsBody body,
            SlackInstallAgentService service,
            CancellationToken ct) =>
        {
            var guard = RequireLoopback(context);
            if (guard is not null) return guard;
            if (body is not null && HasCredentialAddressOverride(body.ExtensionData))
                return ApiResults.BadRequest(
                    "Credential address fields are not supported by the control-plane API.",
                    "credential_address_not_supported");
            if (body is null
                || string.IsNullOrWhiteSpace(body.AgentId)
                || string.IsNullOrWhiteSpace(body.BotToken)
                || string.IsNullOrWhiteSpace(body.AppLevelToken))
                return ApiResults.BadRequest("agentId, botToken, and appLevelToken are required.");
            try
            {
                return ApiResults.Ok(await service.ProvisionCredentialsAsync(
                    context.GetResolvedProject().Id,
                    body.AgentId,
                    body.BotToken,
                    body.AppLevelToken,
                    ct));
            }
            catch (SlackManagerConflictException ex)
            {
                return ApiResults.Conflict(ex.Message, ex.Code);
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_install_credentials");
            }
        });

        manager.MapGet("/connections/{connectionId}", async (
            HttpContext context,
            string connectionId,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
        {
            var result = await service.GetAsync(context.GetResolvedProject().Id, connectionId, ct);
            return result is null
                ? ApiResults.NotFound("The managed Agent App was not found.")
                : ApiResults.Ok(PublicManagedApp(result));
        });

        manager.MapPost("/connections/{connectionId}/create", async (
            HttpContext context,
            string connectionId,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
            OperationResult(await service.CreateAgentAppAsync(
                context.GetResolvedProject().Id, connectionId, ct)));

        manager.MapPost("/connections/{connectionId}/reconcile-create", async (
            HttpContext context,
            string connectionId,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
            OperationResult(await service.ReconcileCreateAsync(
                context.GetResolvedProject().Id, connectionId, ct)));

        manager.MapPost("/connections/{connectionId}/disable", async (
            HttpContext context,
            string connectionId,
            AgentConnectionStore connections,
            CancellationToken ct) =>
            await SetDesiredStateAsync(context, connectionId, DesiredStateKind.Disabled, connections, ct));

        manager.MapPost("/connections/{connectionId}/enable", async (
            HttpContext context,
            string connectionId,
            AgentConnectionStore connections,
            CancellationToken ct) =>
            await SetDesiredStateAsync(context, connectionId, DesiredStateKind.Enabled, connections, ct));

        manager.MapPost("/connections/{connectionId}/remove-binding", async (
            HttpContext context,
            string connectionId,
            AgentConnectionStore connections,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
        {
            var deleted = await connections.DeleteAsync(context.GetResolvedProject().Id, connectionId, ct);
            if (deleted is null) return ApiResults.NotFound("Slack Connection was not found.");
            return ApiResults.Ok(new
            {
                connection = deleted,
                managedApp = await service.GetAsync(context.GetResolvedProject().Id, connectionId, ct),
                removedBinding = true,
                permanentDeleteRequired = true,
            });
        });

        manager.MapPost("/connections/{connectionId}/permanent-delete", async (
            HttpContext context,
            string connectionId,
            PermanentDeleteBody body,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
        {
            if (body is null || !string.Equals(body.Confirmation, "DELETE", StringComparison.Ordinal))
                return ApiResults.Conflict("Permanent delete requires confirmation=DELETE.", "confirmation_required");
            var identityError = RejectClientIdentity(context, body.ExtensionData);
            if (identityError is not null) return identityError;
            var result = await service.PermanentDeleteAsync(
                context.GetResolvedProject().Id, connectionId, body.Confirmation, ct);
            return result.Status switch
            {
                ManagedSlackAgentAppOperationStatus.NotFound => ApiResults.NotFound("The managed Agent App was not found."),
                ManagedSlackAgentAppOperationStatus.NotAllowed => ApiResults.Conflict(
                    result.ErrorClass ?? "Permanent delete is not currently allowed.", "permanent_delete_not_allowed"),
                _ => ApiResults.Ok(result),
            };
        });

        manager.MapPost("/connections/{connectionId}/reconcile-delete", async (
            HttpContext context,
            string connectionId,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
            OperationResult(await service.ReconcileDeleteAsync(
                context.GetResolvedProject().Id, connectionId, ct)));

        return app;
    }

    private static async Task<IResult> SetDesiredStateAsync(
        HttpContext context,
        string connectionId,
        string desiredState,
        AgentConnectionStore connections,
        CancellationToken ct)
    {
        var projectId = context.GetResolvedProject().Id;
        var connection = await connections.GetAsync(projectId, connectionId, ct);
        if (connection is null) return ApiResults.NotFound("Slack Connection was not found.");
        if (connection.DesiredState == desiredState) return ApiResults.Ok(connection);
        var updated = await connections.UpdateAsync(
            projectId,
            connectionId,
            new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
            desiredState: desiredState,
            ct: ct);
        return updated is null ? ApiResults.NotFound("Slack Connection was not found.") : ApiResults.Ok(updated);
    }

    private static IResult OperationResult(ManagedSlackAgentAppOperationResult result) =>
        result.Status == ManagedSlackAgentAppOperationStatus.NotFound
            ? ApiResults.NotFound("The managed Agent App was not found.")
            : ApiResults.Ok(result);

    private static object PublicInstallProgress(SlackInstallAgentProgress progress) => new
    {
        connection = new
        {
            progress.Connection.Id,
            progress.Connection.ProjectId,
            progress.Connection.AgentId,
            progress.Connection.SetupProgress,
            progress.Connection.ConnectionHealth,
            progress.Connection.HealthReason,
        },
        agentApp = new
        {
            progress.AgentApp.AppLifecycle,
            progress.AgentApp.Authorization,
            progress.AgentApp.RuntimeCredentialValidationState,
            progress.AgentApp.BindingState,
            progress.AgentApp.ManifestState,
            progress.AgentApp.TransportReadiness,
            NextAction = PublicInstallNextAction(progress.AgentApp.NextAction),
            InstallUrl = progress.InstallUrl,
            progress.AgentApp.UnknownOutcome,
            progress.AgentApp.ErrorClass,
        },
        NextAction = PublicInstallNextAction(progress.NextAction),
        progress.ErrorClass,
    };

    private static string PublicInstallNextAction(string nextAction) =>
        nextAction == SlackAgentAppNextAction.AuthorizeAgentApp
            ? "approve_install"
            : nextAction;

    private static object PublicManagedApp(SlackManagerAppProjection app) => new
    {
        app.AppLifecycle,
        app.Authorization,
        app.ManifestState,
        app.TransportKind,
        app.TransportReadiness,
        NextAction = PublicInstallNextAction(app.NextAction),
        app.BindingState,
        app.InstallUrl,
        app.UnknownOutcome,
        app.ErrorClass,
        app.DeletedAt,
    };

    private static IResult? RequireLoopback(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } remoteAddress
            || !IPAddress.IsLoopback(remoteAddress))
            return ApiResults.Fail(
                "Slack control-plane secret operations are only available over loopback.",
                403, "loopback_required");
        return null;
    }

    private static bool HasCredentialAddressOverride(IReadOnlyDictionary<string, JsonElement>? extensionData) =>
        extensionData?.Keys.Any(key =>
            key.Equals("projectId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("connectionId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("enrollmentId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("agentAppId", StringComparison.OrdinalIgnoreCase)
            || key.Equals("managerCredentialRef", StringComparison.OrdinalIgnoreCase)
            || key.Equals("secretAddress", StringComparison.OrdinalIgnoreCase)
            || key.Equals("secretKind", StringComparison.OrdinalIgnoreCase)) == true;

    private static IResult? RejectClientIdentity(
        HttpContext context,
        IReadOnlyDictionary<string, JsonElement>? extensionData)
    {
        if (context.Request.Headers.ContainsKey("X-Mohist-Manager-Id")
            || extensionData?.Keys.Any(IsClientIdentityField) == true)
            return ApiResults.BadRequest(
                "Client identity fields are not supported by the Manager API.",
                "client_identity_not_supported");
        return null;
    }

    private static bool IsClientIdentityField(string name) =>
        name.Equals("managerExternalId", StringComparison.OrdinalIgnoreCase)
        || name.Equals("actor", StringComparison.OrdinalIgnoreCase);
}

public sealed class PermanentDeleteBody
{
    public string Confirmation { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class SlackControlInstallAgentBody
{
    public string AgentId { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class SlackControlInstallAgentCredentialsBody
{
    public string AgentId { get; init; } = string.Empty;
    public string BotToken { get; init; } = string.Empty;
    public string AppLevelToken { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
