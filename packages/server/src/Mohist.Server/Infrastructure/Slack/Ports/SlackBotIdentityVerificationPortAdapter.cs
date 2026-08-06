using Mohist.Server.Slack.Services;

namespace Mohist.Server.Infrastructure.Slack.Ports;

/// <summary>
/// Production <see cref="ISlackBotIdentityVerificationPort"/>: verifies a
/// candidate Bot token against <c>auth.test</c> and returns the provider
/// confirmed team / Bot / App facts plus the granted Bot scopes read from
/// Slack's <c>x-oauth-scopes</c> response header. When the header is absent
/// <see cref="SlackBotIdentityVerificationResult.GrantedScopes"/> stays null
/// (unverifiable), so the adapter never substitutes the canonical desired
/// scopes for an unconfirmed grant.
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
                return ParseVerified(response.Body, response.GrantedScopesHeader);
            case SlackApiCallOutcome.Rejected:
                return new(false, ErrorClass: response.Error ?? "auth_rejected");
            case SlackApiCallOutcome.Unparseable:
                return new(false, ErrorClass: "unparseable_response");
            default:
                return new(false, ErrorClass: "transport_error");
        }
    }

    private static SlackBotIdentityVerificationResult ParseVerified(System.Text.Json.JsonDocument? body, string? grantedScopesHeader)
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
            return new(true, teamId, botUserId, appId, GrantedScopes: ParseGrantedScopes(grantedScopesHeader));
        }
    }

    private static IReadOnlySet<string>? ParseGrantedScopes(string? header) =>
        string.IsNullOrWhiteSpace(header)
            ? null
            : new HashSet<string>(
                header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);

    private static string? ReadString(System.Text.Json.JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == System.Text.Json.JsonValueKind.String
            ? element.GetString()
            : null;
}
