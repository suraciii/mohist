using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Slack.Services;

public sealed class SlackManifestGenerator : IScopedService
{
    public SlackManifest Generate(SlackManifestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ProductCapabilityVersion);
        if (input.Transport == SlackManifestTransport.Https && !IsHttps(input.PublicIngressBaseUrl))
            throw new ArgumentException("HTTPS manifests require an HTTPS public ingress base URL.", nameof(input));

        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["display_information"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["description"] = input.Description,
                ["name"] = input.Name,
            },
            ["features"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["bot_user"] = new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["display_name"] = input.Name },
            },
            ["oauth_config"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["scopes"] = new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["bot"] = input.BotScopes.Order(StringComparer.Ordinal).ToArray() },
            },
            ["settings"] = BuildSettings(input),
        };
        var canonical = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
        var fingerprint = JsonSerializer.Serialize(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["product_capability_version"] = input.ProductCapabilityVersion,
            ["identity_snapshot"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["agent_id"] = input.Identity.AgentId,
                ["connection_id"] = input.Identity.ConnectionId,
                ["workspace_team_id"] = input.Identity.WorkspaceTeamId,
            },
        });
        var hashInput = Encoding.UTF8.GetBytes($"{canonical}\n{fingerprint}");
        var hash = Convert.ToHexString(SHA256.HashData(hashInput)).ToLowerInvariant();
        return new(input.ManifestVersion, canonical, hash);
    }

    private static SortedDictionary<string, object?> BuildSettings(SlackManifestInput input)
    {
        var settings = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["socket_mode_enabled"] = input.Transport == SlackManifestTransport.Socket,
            ["token_rotation_enabled"] = false,
        };
        if (input.Transport == SlackManifestTransport.Https)
        {
            settings["event_subscriptions"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request_url"] = $"{input.PublicIngressBaseUrl!.TrimEnd('/')}/slack/events",
            };
        }
        return settings;
    }

    private static bool IsHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(uri.Host);
}

public sealed record SlackManifestInput(
    string Name,
    string Description,
    IReadOnlyCollection<string> BotScopes,
    SlackManifestTransport Transport,
    string? PublicIngressBaseUrl,
    string ProductCapabilityVersion,
    SlackManifestIdentitySnapshot Identity,
    int ManifestVersion = 2);

public sealed record SlackManifestIdentitySnapshot(
    string ConnectionId,
    string AgentId,
    string WorkspaceTeamId);

public enum SlackManifestTransport
{
    Socket,
    Https,
}

public sealed record SlackManifest(int Version, string CanonicalJson, string Hash);
