using System.Text.Json;
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
///
/// <c>auth.test</c> does not return <c>app_id</c>; when it is missing the
/// adapter resolves it via <c>bots.info</c> using the confirmed <c>bot_id</c>.
/// An app identity that cannot be resolved fails closed.
/// </summary>
public sealed class SlackBotIdentityVerificationPortAdapter(
    SlackApiTransport transport) : ISlackBotIdentityVerificationPort
{
    public const string AuthTestEndpoint = "auth.test";
    public const string BotsInfoEndpoint = "bots.info";

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
                return await ParseVerifiedAsync(response.Body, response.GrantedScopesHeader, request.BotToken, ct).ConfigureAwait(false);
            case SlackApiCallOutcome.Rejected:
                return new(false, ErrorClass: response.Error ?? "auth_rejected");
            case SlackApiCallOutcome.Unparseable:
                return new(false, ErrorClass: "unparseable_response");
            default:
                return new(false, ErrorClass: "transport_error");
        }
    }

    private async Task<SlackBotIdentityVerificationResult> ParseVerifiedAsync(
        JsonDocument? body,
        string? grantedScopesHeader,
        string botToken,
        CancellationToken ct)
    {
        if (body is null)
            return new(false, ErrorClass: "unparseable_response");
        using (body)
        {
            var root = body.RootElement;
            var teamId = ReadString(root, "team_id");
            var botUserId = ReadString(root, "user_id");
            var appId = ReadString(root, "app_id");
            var botId = ReadString(root, "bot_id");
            if (teamId is null || botUserId is null)
                return new(false, ErrorClass: "invalid_identity_response");
            if (appId is null)
            {
                if (string.IsNullOrWhiteSpace(botId))
                    return new(false, ErrorClass: "invalid_identity_response");
                appId = await ResolveAppIdAsync(botId, botToken, ct).ConfigureAwait(false);
                if (appId is null)
                    return new(false, ErrorClass: "app_id_unresolved");
            }
            return new(true, teamId, botUserId, appId, GrantedScopes: ParseGrantedScopes(grantedScopesHeader));
        }
    }

    private async Task<string?> ResolveAppIdAsync(string botId, string botToken, CancellationToken ct)
    {
        var response = await transport.PostFormAsync(
            BotsInfoEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["bot"] = botId },
            botToken,
            ct).ConfigureAwait(false);
        if (response.Outcome != SlackApiCallOutcome.Ok || response.Body is null)
            return null;
        using (response.Body)
        {
            var root = response.Body.RootElement;
            if (!root.TryGetProperty("bot", out var bot) || bot.ValueKind != JsonValueKind.Object)
                return null;
            return ReadString(bot, "app_id");
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
