using Mohist.Server.Slack.Services;

namespace Mohist.Server.Infrastructure.Slack.Ports;

/// <summary>
/// Production <see cref="ISlackBotIdentityVerificationPort"/>: verifies a
/// candidate Bot token against <c>auth.test</c> and returns the provider
/// confirmed team / Bot / App facts. Slack's <c>auth.test</c> does not expose
/// granted scopes and no bot-token-authenticated endpoint does, so
/// <see cref="SlackBotIdentityVerificationResult.GrantedScopes"/> stays null —
/// the adapter never fabricates scope facts it cannot verify.
/// </summary>
public sealed class SlackBotIdentityVerificationPortAdapter(
    SlackApiTransport transport) : ISlackBotIdentityVerificationPort
{
    public const string AuthTestEndpoint = "auth.test";

    public async Task<SlackBotIdentityVerificationResult> VerifyAsync(
        SlackBotIdentityVerificationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BotToken);

        var response = await transport.PostFormAsync(
            AuthTestEndpoint,
            form: null,
            request.BotToken,
            ct).ConfigureAwait(false);

        switch (response.Outcome)
        {
            case SlackApiCallOutcome.Ok:
                return ParseVerified(response.Body);
            case SlackApiCallOutcome.Rejected:
                return new(false, ErrorClass: response.Error ?? "auth_rejected");
            case SlackApiCallOutcome.Unparseable:
                return new(false, ErrorClass: "unparseable_response");
            default:
                return new(false, ErrorClass: "transport_error");
        }
    }

    private static SlackBotIdentityVerificationResult ParseVerified(System.Text.Json.JsonDocument? body)
    {
        if (body is null)
            return new(false, ErrorClass: "unparseable_response");
        using (body)
        {
            var root = body.RootElement;
            var teamId = ReadString(root, "team_id");
            var botUserId = ReadString(root, "user_id");
            var appId = ReadString(root, "app_id");
            if (teamId is null || botUserId is null)
                return new(false, ErrorClass: "invalid_identity_response");
            return new(true, teamId, botUserId, appId, GrantedScopes: null);
        }
    }

    private static string? ReadString(System.Text.Json.JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == System.Text.Json.JsonValueKind.String
            ? element.GetString()
            : null;
}
