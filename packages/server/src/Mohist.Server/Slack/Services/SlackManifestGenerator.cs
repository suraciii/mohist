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
        var definition = SlackManifestDefinition.For(input.Kind);

        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["display_information"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = input.Name,
                ["description"] = input.Description,
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
                ["scopes"] = BuildScopes(definition.BotScopes),
            },
            ["settings"] = BuildSettings(definition.BotEvents),
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

    private static SortedDictionary<string, object?> BuildScopes(IReadOnlyCollection<string> botScopes)
    {
        var scopes = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bot"] = botScopes.ToArray(),
        };
        return scopes;
    }

    private static SortedDictionary<string, object?> BuildSettings(IReadOnlyCollection<string> botEvents)
    {
        var eventSubscriptions = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bot_events"] = botEvents.ToArray(),
        };
        return new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["event_subscriptions"] = eventSubscriptions,
            ["interactivity"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["is_enabled"] = true,
            },
            ["socket_mode_enabled"] = true,
            ["token_rotation_enabled"] = false,
        };
    }
}

public sealed record SlackManifestDefinition(
    IReadOnlyCollection<string> BotScopes,
    IReadOnlyCollection<string> BotEvents)
{
    public static SlackManifestDefinition For(SlackManifestKind kind) => kind switch
    {
        SlackManifestKind.MohistApp => new(["chat:write", "im:history", "users:read"], ["message.im"]),
        SlackManifestKind.AgentApp => new(
            ["app_mentions:read", "channels:history", "chat:write", "groups:history", "im:history", "reactions:read", "reactions:write", "users:read"],
            ["app_mention", "message.im"]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed record SlackManifestInput(
    string Name,
    string Description,
    string ProductCapabilityVersion,
    SlackManifestIdentitySnapshot Identity,
    SlackManifestKind Kind,
    int ManifestVersion = 2);

public sealed record SlackManifestIdentitySnapshot(
    string ConnectionId,
    string AgentId,
    string WorkspaceTeamId);

public enum SlackManifestKind
{
    MohistApp,
    AgentApp,
}

public sealed record SlackManifest(int Version, string CanonicalJson, string Hash);

public static class SlackManifestDrift
{
    public static bool HasDrift(SlackManifest desired, string? exportedManifestJson)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (string.IsNullOrWhiteSpace(exportedManifestJson))
            return true;

        try
        {
            using var expected = JsonDocument.Parse(desired.CanonicalJson);
            using var actual = JsonDocument.Parse(exportedManifestJson);
            return !Equivalent(expected.RootElement, actual.RootElement);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool Equivalent(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind)
            return false;

        return expected.ValueKind switch
        {
            JsonValueKind.Object => EquivalentObject(expected, actual),
            JsonValueKind.Array => EquivalentArray(expected, actual),
            JsonValueKind.String => expected.GetString() == actual.GetString(),
            JsonValueKind.Number => expected.GetRawText() == actual.GetRawText(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false,
        };
    }

    private static bool EquivalentObject(JsonElement expected, JsonElement actual)
    {
        var actualProperties = actual.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        foreach (var property in expected.EnumerateObject())
        {
            if (actualProperties.Remove(property.Name, out var actualValue))
            {
                if (!Equivalent(property.Value, actualValue))
                    return false;
            }
            else if (property.Value.ValueKind != JsonValueKind.True)
            {
                return false;
            }
        }

        return actualProperties.Values.All(value => value.ValueKind == JsonValueKind.True);
    }

    private static bool EquivalentArray(JsonElement expected, JsonElement actual)
    {
        var expectedItems = expected.EnumerateArray().ToArray();
        var actualItems = actual.EnumerateArray().ToArray();
        return expectedItems.Length == actualItems.Length
            && expectedItems.Zip(actualItems).All(pair => Equivalent(pair.First, pair.Second));
    }
}
