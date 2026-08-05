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
        Assert.Equal(["app_mentions:read", "channels:history", "chat:write", "groups:history", "im:history", "reactions:read", "reactions:write", "users:read"], Scopes(agentJson));
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

    private static string[] Scopes(JsonDocument manifest) => manifest.RootElement
        .GetProperty("oauth_config").GetProperty("scopes").GetProperty("bot")
        .EnumerateArray().Select(scope => scope.GetString()!).ToArray();

    private static string[] Events(JsonDocument manifest) => manifest.RootElement
        .GetProperty("settings").GetProperty("event_subscriptions").GetProperty("bot_events")
        .EnumerateArray().Select(item => item.GetString()!).ToArray();
}
