using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class AgentReadinessTests
{
    [Fact]
    public void DerivesNeedsSetupWhenConfigurationIsMissing()
    {
        Assert.Equal(AgentReadinessKind.NeedsSetup, AgentReadinessDeriver.Derive(null));
        Assert.Equal(AgentReadinessKind.NeedsSetup, Derive("{\"model\":\"gpt\"}"));
        Assert.Equal(AgentReadinessKind.NeedsSetup, Derive("{\"runtime\":\"opencode\"}"));
    }

    [Fact]
    public void DerivesReadyWhenModelAndRuntimeArePresent()
    {
        Assert.Equal(AgentReadinessKind.Ready, Derive("{\"model\":\"gpt\",\"runtime\":\"opencode\"}"));
    }

    [Fact]
    public void KeepsIndeterminateConfigurationUnknown()
    {
        Assert.Equal(AgentReadinessKind.Unknown, Derive("[]"));
        Assert.Equal(AgentReadinessKind.Unknown, Derive("{\"model\":true,\"runtime\":\"opencode\"}"));
    }

    [Theory]
    [InlineData(AgentReadinessKind.NeedsSetup, false, "rejected")]
    [InlineData(AgentReadinessKind.Unknown, true, "accepted")]
    [InlineData(AgentReadinessKind.Ready, true, "accepted")]
    public void DispatchPolicyMakesReadinessDecision(string readiness, bool accepted, string kind)
    {
        var decision = AgentConnectionDispatchDecision.For(readiness);

        Assert.Equal(accepted, decision.Accepted);
        Assert.Equal(kind, decision.Kind);
        if (readiness == AgentReadinessKind.NeedsSetup)
            Assert.Contains("model", decision.Reason, StringComparison.OrdinalIgnoreCase);
        if (readiness == AgentReadinessKind.Unknown)
            Assert.Contains("Runner", decision.Reason, StringComparison.Ordinal);
    }

    private static string Derive(string json) =>
        AgentReadinessDeriver.Derive(JsonDocument.Parse(json).RootElement.Clone());
}
