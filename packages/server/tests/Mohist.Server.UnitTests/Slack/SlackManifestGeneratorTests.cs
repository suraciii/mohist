using System.Text.Json;
using Mohist.Server.Slack.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Slack;

public sealed class SlackManifestGeneratorTests
{
    private static readonly SlackManifestIdentitySnapshot Identity = new("connection-1", "agent-1", "T123");

    [Fact]
    public void Generate_UsesFixedSocketModeManifestPerAppKind()
    {
        var generator = new SlackManifestGenerator();
        var mohist = generator.Generate(new("Mohist", "Manages Mohist", "capability-1", Identity, SlackManifestKind.MohistApp));
        var agent = generator.Generate(new("Agent", "Handles work", "capability-1", Identity, SlackManifestKind.AgentApp));

        using var mohistJson = JsonDocument.Parse(mohist.CanonicalJson);
        using var agentJson = JsonDocument.Parse(agent.CanonicalJson);
        Assert.True(mohistJson.RootElement.GetProperty("settings").GetProperty("socket_mode_enabled").GetBoolean());
        Assert.False(mohistJson.RootElement.GetProperty("settings").GetProperty("event_subscriptions").TryGetProperty("request_url", out _));
        Assert.Equal(["chat:write", "im:history", "users:read"], Scopes(mohistJson));
        Assert.Equal(["message.im"], Events(mohistJson));
        Assert.Equal(["app_mentions:read", "channels:history", "channels:read", "chat:write", "groups:history", "groups:read", "im:history", "reactions:read", "reactions:write", "users:read"], Scopes(agentJson));
        Assert.Equal(["app_mention", "message.im"], Events(agentJson));
        Assert.DoesNotContain("mpim:history", agent.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("PublicIngressBaseUrl", agent.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_HashChangesOnlyWhenCanonicalFingerprintChanges()
    {
        var generator = new SlackManifestGenerator();
        var first = generator.Generate(new("Agent", "Handles work", "capability-1", Identity, SlackManifestKind.AgentApp));
        var same = generator.Generate(new("Agent", "Handles work", "capability-1", Identity, SlackManifestKind.AgentApp));
        var changedCapability = generator.Generate(new("Agent", "Handles work", "capability-2", Identity, SlackManifestKind.AgentApp));
        var changedIdentity = generator.Generate(new("Agent", "Handles work", "capability-1", Identity with { ConnectionId = "connection-2" }, SlackManifestKind.AgentApp));

        Assert.Equal(first.CanonicalJson, same.CanonicalJson);
        Assert.Equal(first.Hash, same.Hash);
        Assert.NotEqual(first.Hash, changedCapability.Hash);
        Assert.NotEqual(first.Hash, changedIdentity.Hash);
    }

    [Fact]
    public void HasDrift_TreatsTrueAndOmittedAsEquivalentButDetectsOtherChanges()
    {
        var expected = new SlackManifest(2, "{\"socket_mode_enabled\":true,\"token_rotation_enabled\":false}", "hash");

        Assert.False(SlackManifestDrift.HasDrift(expected, "{\"token_rotation_enabled\":false}"));
        Assert.True(SlackManifestDrift.HasDrift(expected, "{\"socket_mode_enabled\":true}"));
        Assert.True(SlackManifestDrift.HasDrift(expected, "{\"token_rotation_enabled\":true}"));
        Assert.True(SlackManifestDrift.HasDrift(expected, "not-json"));
    }

    [Fact]
    public void HasDrift_ArrayElementOrderDoesNotMatter()
    {
        var expected = new SlackManifest(2, """{"oauth_config":{"scopes":{"bot":["chat:write","im:history","users:read"]}},"settings":{"event_subscriptions":{"bot_events":["app_mention","message.im"]}}}""", "hash");

        Assert.False(SlackManifestDrift.HasDrift(expected, """{"settings":{"event_subscriptions":{"bot_events":["message.im","app_mention"]}},"oauth_config":{"scopes":{"bot":["users:read","chat:write","im:history"]}}}"""));
        Assert.False(SlackManifestDrift.HasDrift(expected, """{"oauth_config":{"scopes":{"bot":["chat:write","im:history","users:read"]}},"settings":{"event_subscriptions":{"bot_events":["app_mention","message.im"]}}}"""));
        Assert.True(SlackManifestDrift.HasDrift(expected, """{"oauth_config":{"scopes":{"bot":["chat:write","im:history","users:read","files:read"]}},"settings":{"event_subscriptions":{"bot_events":["app_mention","message.im"]}}}"""));
    }

    [Fact]
    public void HasDrift_ArrayComparisonKeepsDuplicateSensitivity()
    {
        var expected = new SlackManifest(2, """{"settings":{"event_subscriptions":{"bot_events":["message.im"]}}}""", "hash");

        Assert.True(SlackManifestDrift.HasDrift(expected, """{"settings":{"event_subscriptions":{"bot_events":["message.im","message.im"]}}}"""));
        Assert.True(SlackManifestDrift.HasDrift(expected, """{"settings":{"event_subscriptions":{"bot_events":[]}}}"""));
        Assert.True(SlackManifestDrift.HasDrift(expected, """{"settings":{"event_subscriptions":{"bot_events":["app_mention"]}}}"""));
    }

    [Fact]
    public void HasDrift_TrueOrOmittedToleranceAppliesOnlyToBooleans()
    {
        var expected = new SlackManifest(2, """{"display_information":{"name":"Agent"},"settings":{"socket_mode_enabled":true}}""", "hash");

        Assert.False(SlackManifestDrift.HasDrift(expected, """{"display_information":{"name":"Agent"},"settings":{"socket_mode_enabled":true,"token_rotation_enabled":true}}"""));
        Assert.True(SlackManifestDrift.HasDrift(expected, """{"display_information":{"name":"Agent","background_color":"#4A154B"},"settings":{"socket_mode_enabled":true}}"""));
        Assert.True(SlackManifestDrift.HasDrift(expected, """{"display_information":{"name":"Agent"},"oauth_config":{"redirect_urls":[]},"settings":{"socket_mode_enabled":true}}"""));
        Assert.True(SlackManifestDrift.HasDrift(expected, """{"display_information":{"name":"Agent"},"settings":{"socket_mode_enabled":true,"token_rotation_enabled":false}}"""));
    }

    [Fact]
    public void HasDrift_ObjectFieldOrderDoesNotMatter()
    {
        var expected = new SlackManifest(2, """{"features":{"bot_user":{"display_name":"Agent","always_online":false}},"settings":{"socket_mode_enabled":true}}""", "hash");

        Assert.False(SlackManifestDrift.HasDrift(expected, """{"settings":{"socket_mode_enabled":true},"features":{"bot_user":{"always_online":false,"display_name":"Agent"}}}"""));
    }

    [Fact]
    public void HasDrift_PlatformRoundTripWithReorderedSetsAndOmittedTrueIsNotDrift()
    {
        var generator = new SlackManifestGenerator();
        var manifest = generator.Generate(new("Agent", "Handles work", "capability-1", Identity, SlackManifestKind.AgentApp));

        var export = """
            {
              "display_information": { "description": "Handles work", "name": "Agent" },
              "features": {
                "app_home": { "messages_tab_enabled": true, "home_tab_enabled": false, "messages_tab_read_only_enabled": false },
                "bot_user": { "always_online": false, "display_name": "Agent" }
              },
              "oauth_config": { "scopes": { "bot": ["users:read", "reactions:write", "reactions:read", "im:history", "groups:read", "groups:history", "chat:write", "channels:read", "channels:history", "app_mentions:read"] } },
              "settings": {
                "socket_mode_enabled": true,
                "token_rotation_enabled": false,
                "interactivity": { "is_enabled": true },
                "event_subscriptions": { "bot_events": ["message.im", "app_mention"] }
              }
            }
            """;
        Assert.False(SlackManifestDrift.HasDrift(manifest, export));
    }

    private static string[] Scopes(JsonDocument manifest) => manifest.RootElement
        .GetProperty("oauth_config").GetProperty("scopes").GetProperty("bot")
        .EnumerateArray().Select(scope => scope.GetString()!).ToArray();

    private static string[] Events(JsonDocument manifest) => manifest.RootElement
        .GetProperty("settings").GetProperty("event_subscriptions").GetProperty("bot_events")
        .EnumerateArray().Select(item => item.GetString()!).ToArray();
}
