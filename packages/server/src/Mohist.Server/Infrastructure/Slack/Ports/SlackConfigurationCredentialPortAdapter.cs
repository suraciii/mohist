using System.Text.Json;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Infrastructure.Slack.Ports;

/// <summary>
/// Production <see cref="ISlackConfigurationCredentialPort"/>: rotates a
/// Configuration access/refresh token pair through the Slack tooling token
/// rotation endpoint. The returned pair, provider-confirmed workspace and
/// expiry are the only facts the port exposes; no app management here.
/// </summary>
public sealed class SlackConfigurationCredentialPortAdapter(
    SlackApiTransport transport) : ISlackConfigurationCredentialPort
{
    public const string RotationEndpoint = "tooling.tokens.rotate";

    public async Task<SlackConfigurationCredentialRotationResult> RotateAsync(
        SlackConfigurationCredentialPair credentials,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        credentials.Validate();

        var response = await transport.PostFormAsync(
            RotationEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["refresh_token"] = credentials.RefreshToken,
            },
            bearerToken: null,
            ct).ConfigureAwait(false);

        switch (response.Outcome)
        {
            case SlackApiCallOutcome.Ok:
                return ParseRotation(response.Body);
            case SlackApiCallOutcome.Rejected:
                return new(SlackConfigurationCredentialRotationOutcome.DefiniteFailure,
                    ErrorClass: response.Error ?? "rotation_rejected");
            case SlackApiCallOutcome.Unparseable:
                return new(SlackConfigurationCredentialRotationOutcome.Unknown, ErrorClass: "unparseable_response");
            default:
                return new(SlackConfigurationCredentialRotationOutcome.Unknown, ErrorClass: "transport_error");
        }
    }

    private static SlackConfigurationCredentialRotationResult ParseRotation(JsonDocument? body)
    {
        if (body is null)
            return new(SlackConfigurationCredentialRotationOutcome.Unknown, ErrorClass: "unparseable_response");
        using (body)
        {
            var root = body.RootElement;
            var accessToken = ReadString(root, "token");
            var refreshToken = ReadString(root, "refresh_token");
            var teamId = ReadString(root, "team_id");
            var expiresAt = root.TryGetProperty("exp", out var expiresElement)
                && expiresElement.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(expiresElement.GetInt64())
                : (DateTimeOffset?)null;
            if (accessToken is null || refreshToken is null || teamId is null || expiresAt is null)
                return new(SlackConfigurationCredentialRotationOutcome.DefiniteFailure,
                    ErrorClass: "invalid_rotation_result");
            return new(
                SlackConfigurationCredentialRotationOutcome.Succeeded,
                new SlackConfigurationCredentialPair(accessToken, refreshToken),
                teamId,
                expiresAt.Value);
        }
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
