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

        manager.MapPost("/apps", async (
            HttpContext context,
            SlackManagerCreateBody body,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.AgentId)
                || string.IsNullOrWhiteSpace(body.WorkspaceTeamId))
                return ApiResults.BadRequest("agentId and workspaceTeamId are required.");

            var identityError = RejectClientIdentity(context, body.ExtensionData);
            if (identityError is not null) return identityError;
            try
            {
                var result = await service.CreateAsync(new SlackManagerCreateRequest(
                    context.GetResolvedProject().Id,
                    body.AgentId,
                    body.WorkspaceTeamId,
                    body.AccessPolicy ?? AccessPolicyKind.OwnerOnly,
                    body.OwnerSlackUserId,
                    body.BotName,
                    body.AvatarHash), ct);
                return Results.Json(new ApiResponse<object>(true, result), statusCode: result.Created ? 201 : 200);
            }
            catch (SlackManagerConflictException ex)
            {
                return ApiResults.Conflict(ex.Message, ex.Code);
            }
            catch (SlackManagerValidationException ex)
            {
                return ApiResults.BadRequest(ex.Message, ex.Code);
            }
            catch (AgentConnectionDuplicateException ex)
            {
                return ApiResults.Conflict(ex.Message, "connection_duplicate");
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
                : ApiResults.Ok(result);
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

        manager.MapPost("/connections/{connectionId}/begin-authorization", async (
            HttpContext context,
            string connectionId,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
            ApiResults.Ok(await service.BeginAuthorizationAsync(
                context.GetResolvedProject().Id, connectionId, ct)));

        manager.MapPost("/connections/{connectionId}/authorization-progress", async (
            HttpContext context,
            string connectionId,
            SlackAuthorizationProgressBody body,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Authorization))
                return ApiResults.BadRequest("authorization is required.");
            try
            {
                return ApiResults.Ok(await service.RecordAuthorizationProgressAsync(
                    context.GetResolvedProject().Id, connectionId, body.Authorization, ct));
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_authorization");
            }
        });

        manager.MapPost("/connections/{connectionId}/authorize", async (
            HttpContext context,
            string connectionId,
            SlackAuthorizeBody body,
            SlackManagerApplicationService service,
            CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.State)
                || string.IsNullOrWhiteSpace(body.BotUserId)
                || string.IsNullOrWhiteSpace(body.BotToken))
                return ApiResults.BadRequest("state, botUserId, and botToken are required.");
            return ApiResults.Ok(await service.AuthorizeAsync(
                context.GetResolvedProject().Id,
                connectionId,
                body.State,
                body.BotUserId,
                body.BotToken,
                ct));
        });

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

public sealed class SlackManagerCreateBody
{
    public string AgentId { get; init; } = string.Empty;
    public string WorkspaceTeamId { get; init; } = string.Empty;
    public string? AccessPolicy { get; init; }
    public string? OwnerSlackUserId { get; init; }
    public string? BotName { get; init; }
    public string? AvatarHash { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class SlackAuthorizationProgressBody
{
    public string Authorization { get; init; } = string.Empty;
}

public sealed class SlackAuthorizeBody
{
    public string State { get; init; } = string.Empty;
    public string BotUserId { get; init; } = string.Empty;
    public string BotToken { get; init; } = string.Empty;
}

public sealed class PermanentDeleteBody
{
    public string Confirmation { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
