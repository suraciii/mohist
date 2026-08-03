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
        ArgumentNullException.ThrowIfNull(input.Identity);
        if (input.ManifestVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(input.ManifestVersion));
        if (input.Transport == SlackManifestTransport.Https && !IsHttps(input.PublicIngressBaseUrl))
            throw new ArgumentException("HTTPS manifests require an HTTPS public ingress base URL.", nameof(input));
        if (input.InteractivityRequestUrl is not null && !IsHttps(input.InteractivityRequestUrl))
            throw new ArgumentException("Interactivity request URLs must use HTTPS.", nameof(input));

        var botScopes = (input.BotScopes ?? Array.Empty<string>())
            .Concat(SlackManifestScopes.RequiredBotScopes)
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var userScopes = (input.UserScopes ?? Array.Empty<string>())
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var botEvents = (input.BotEvents ?? ["app_mention", "message.im"])
            .Where(eventType => !string.IsNullOrWhiteSpace(eventType))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (botEvents.Length == 0)
            throw new ArgumentException("At least one bot event is required.", nameof(input));

        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["display_information"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = input.Name,
            },
            ["features"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["app_home"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["home_tab_enabled"] = false,
                    ["messages_tab_enabled"] = true,
                    ["messages_tab_read_only_enabled"] = false,
                },
                ["bot_user"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["always_online"] = false,
                    ["display_name"] = input.Name,
                },
            },
            ["oauth_config"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["scopes"] = BuildScopes(botScopes, userScopes),
            },
            ["settings"] = BuildSettings(input, botEvents),
        };
        var canonical = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
        var fingerprint = JsonSerializer.Serialize(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["manifest_version"] = input.ManifestVersion,
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

    private static SortedDictionary<string, object?> BuildScopes(
        IReadOnlyCollection<string> botScopes,
        IReadOnlyCollection<string> userScopes)
    {
        var scopes = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bot"] = botScopes.ToArray(),
        };
        if (userScopes.Count > 0)
            scopes["user"] = userScopes.ToArray();
        return scopes;
    }

    private static SortedDictionary<string, object?> BuildSettings(SlackManifestInput input, IReadOnlyCollection<string> botEvents)
    {
        var eventSubscriptions = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bot_events"] = botEvents.ToArray(),
        };
        if (input.Transport == SlackManifestTransport.Https)
            eventSubscriptions["request_url"] = $"{input.PublicIngressBaseUrl!.TrimEnd('/')}/slack/events";

        var settings = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event_subscriptions"] = eventSubscriptions,
            ["socket_mode_enabled"] = input.Transport == SlackManifestTransport.Socket,
            ["token_rotation_enabled"] = false,
        };
        if (input.InteractivityRequestUrl is not null)
        {
            settings["interactivity"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["is_enabled"] = true,
                ["request_url"] = input.InteractivityRequestUrl,
            };
        }
        return settings;
    }

    private static bool IsHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(uri.Host);
}

public static class SlackManifestScopes
{
    public static readonly string[] RequiredBotScopes = [
        "channels:history", "chat:write", "groups:history", "im:history", "mpim:history", "reactions:read", "reactions:write", "users:read",
    ];
}

public sealed record SlackManifestInput(
    string Name,
    string Description,
    IReadOnlyCollection<string> BotScopes,
    SlackManifestTransport Transport,
    string? PublicIngressBaseUrl,
    string ProductCapabilityVersion,
    SlackManifestIdentitySnapshot Identity,
    int ManifestVersion = 2,
    IReadOnlyCollection<string>? BotEvents = null,
    IReadOnlyCollection<string>? UserScopes = null,
    string? InteractivityRequestUrl = null);

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
