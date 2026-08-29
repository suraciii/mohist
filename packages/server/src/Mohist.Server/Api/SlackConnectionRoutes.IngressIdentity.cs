namespace Mohist.Server.Api;

public static partial class SlackConnectionRoutes
{
    private static IResult? ValidateIngressAppIdentity(
        Agent.Domain.AgentConnection connection,
        SlackIngressBody? body)
    {
        if (body is null)
            return ApiResults.BadRequest("apiAppId is required.", "slack_app_identity_mismatch");
        if (string.IsNullOrWhiteSpace(body.ApiAppId))
            return ApiResults.BadRequest("apiAppId is required.", "slack_app_identity_mismatch");
        if (!string.Equals(connection.AppId, body.ApiAppId, StringComparison.Ordinal))
            return ApiResults.BadRequest(
                "The Slack app does not match this Connection.",
                "slack_app_identity_mismatch");
        return null;
    }
}
