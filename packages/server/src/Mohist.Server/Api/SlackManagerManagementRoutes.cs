using System.Text.Json;
using Mohist.Server.Slack.Services;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Api;

public static class SlackManagerManagementRoutes
{
    public static WebApplication MapSlackManagerManagementRoutes(this WebApplication app)
    {
        app.MapPost("/api/slack-manager/management", async (
            HttpContext context,
            ManagerManagementBridge bridge,
            CancellationToken ct) =>
        {
            if (context.Items[ManagerExecutionCredentialContext.HttpContextItemKey]
                is not ManagerExecutionCredentialContext credential
                || credential.Kind != ManagerExecutionLeaseKind.Management)
                return ApiResults.Fail(
                    "Manager management requires a Manager management credential.",
                    StatusCodes.Status403Forbidden,
                    "manager_management_credential_required");

            JsonElement request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<JsonElement>(
                    context.Request.Body, JSON.Options, ct);
            }
            catch (JsonException)
            {
                return ApiResults.BadRequest(
                    "The management request must be valid JSON.",
                    "manager_request_invalid");
            }

            var result = await bridge.ExecuteAsync(request, credential, ct);
            return result.Outcome switch
            {
                "confirmed_state" or "idempotent" => ApiResults.Ok(result),
                "validation_error" => ApiResults.BadRequest(result.Message, result.Code, result),
                "conflict" => ApiResults.Conflict(result.Message, result.Code, result),
                "not_found" => ApiResults.NotFound(result.Message),
                _ => ApiResults.Fail(result.Message, StatusCodes.Status403Forbidden, result.Code, result),
            };
        });
        return app;
    }
}
