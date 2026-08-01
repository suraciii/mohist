using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class AgentStartupContextFingerprintTests
{
    [Fact]
    public void StartupContext_DoesNotChangeFingerprint_WhenOnlyBackgroundVaries()
    {
        var without = new AgentLaunchCoordinatorRequest("task", "agent", null, null, null, null, null, null);
        var withShort = new AgentLaunchCoordinatorRequest(
            "task", "agent", null, null, null, null, null, null,
            StartupContext: BuildContext("short body", truncated: false));
        var withLong = new AgentLaunchCoordinatorRequest(
            "task", "agent", null, null, null, null, null, null,
            StartupContext: BuildContext(BuildLongBody(), truncated: true, marker: "10 oldest messages omitted"));
        var withDifferentMarker = new AgentLaunchCoordinatorRequest(
            "task", "agent", null, null, null, null, null, null,
            StartupContext: BuildContext(BuildLongBody(), truncated: true, marker: "20 oldest messages omitted"));

        var baseline = AgentLaunchCoordinatorCodec.Fingerprint(without);
        Assert.Equal(baseline, AgentLaunchCoordinatorCodec.Fingerprint(withShort));
        Assert.Equal(baseline, AgentLaunchCoordinatorCodec.Fingerprint(withLong));
        Assert.Equal(baseline, AgentLaunchCoordinatorCodec.Fingerprint(withDifferentMarker));
    }

    [Fact]
    public void StartupContext_Exclusion_DoesNotBreakConnectionOriginFingerprint()
    {
        var request = new AgentLaunchCoordinatorRequest(
            "task", "agent", null, null, null, null, null, null,
            StartupContext: BuildContext("body", truncated: false));
        var originA = new ConnectionLaunchOrigin("connection", "T1", "U1", "D1", "1.0");
        var originB = new ConnectionLaunchOrigin("connection", "T1", "U1", "D1", "2.0");

        Assert.NotEqual(
            AgentLaunchCoordinatorCodec.Fingerprint(request, originA),
            AgentLaunchCoordinatorCodec.Fingerprint(request, originB));
    }

    [Fact]
    public void ExistingFingerprintBehavior_Preserved_WhenStartupContextOmitted()
    {
        var request = new AgentLaunchCoordinatorRequest("task", "agent", null, null, null, null, null, null);
        var origin = new ConnectionLaunchOrigin("connection", "T1", "U1", "D1", "1.0");

        Assert.NotEqual(
            AgentLaunchCoordinatorCodec.Fingerprint(request, origin),
            AgentLaunchCoordinatorCodec.Fingerprint(request));

        var requestWithoutContext = new AgentLaunchCoordinatorRequest(
            "task", "agent", null, null, null, null, null, null);
        var requestWithNullContext = new AgentLaunchCoordinatorRequest(
            "task", "agent", null, null, null, null, null, null,
            StartupContext: null);
        Assert.Equal(
            AgentLaunchCoordinatorCodec.Fingerprint(requestWithoutContext, origin),
            AgentLaunchCoordinatorCodec.Fingerprint(requestWithNullContext, origin));
    }

    [Fact]
    public void StartupContext_AlwaysNull_ProducesSameFingerprint_AsNoField()
    {
        var none = new AgentLaunchCoordinatorRequest("task", "agent", null, null, null, null, null, null);
        var explicitNull = new AgentLaunchCoordinatorRequest(
            "task", "agent", null, null, null, null, null, null,
            StartupContext: null);

        Assert.Equal(
            AgentLaunchCoordinatorCodec.Fingerprint(none),
            AgentLaunchCoordinatorCodec.Fingerprint(explicitNull));
    }

    private static AgentStartupContext BuildContext(
        string body,
        bool truncated,
        string? marker = null) =>
        new(
            Text: body,
            Provenance: new AgentStartupContextProvenance(
                Source: "slack-thread-history",
                Truncated: truncated,
                TruncationMarker: marker,
                OmittedOldestMessageCount: truncated ? 10 : 0));

    private static string BuildLongBody()
    {
        var line = "This is one of many older messages in the thread that the caller handed to the agent as background. ";
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 100; i++)
        {
            builder.Append(line);
            builder.Append(' ');
        }
        return builder.ToString();
    }
}