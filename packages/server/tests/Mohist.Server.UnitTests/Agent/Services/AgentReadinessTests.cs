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
        Assert.Equal(AgentReadinessKind.NeedsSetup, Derive("{\"runtime\":\"opencode\"}"));
    }

    [Fact]
    public void DerivesReadyWhenRuntimeIsOmittedBecauseOpenCodeIsCanonicalDefault()
    {
        Assert.Equal(AgentReadinessKind.Ready, Derive("{\"model\":\"gpt\"}"));
    }

    [Fact]
    public void DerivesReadyWhenModelAndRuntimeArePresent()
    {
        Assert.Equal(AgentReadinessKind.Ready, Derive("{\"model\":\"gpt\",\"runtime\":\"opencode\"}"));
    }

    [Fact]
    public void DerivesNeedsSetupWhenRuntimeIsInvalid()
    {
        Assert.Equal(AgentReadinessKind.NeedsSetup, Derive("{\"model\":\"gpt\",\"runtime\":\"unsupported\"}"));
    }

    [Fact]
    public void KeepsIndeterminateConfigurationUnknown()
    {
        Assert.Equal(AgentReadinessKind.Unknown, Derive("[]"));
        Assert.Equal(AgentReadinessKind.Unknown, Derive("{\"model\":true,\"runtime\":\"opencode\"}"));
    }

    [Theory]
    [InlineData(AgentExecutabilityStates.NotConfigured, false, "agent_not_configured")]
    [InlineData(AgentExecutabilityStates.NotExecutable, false, "agent_not_executable")]
    [InlineData(AgentExecutabilityStates.Unknown, true, "accepted")]
    [InlineData(AgentExecutabilityStates.Executable, true, "accepted")]
    public void DispatchPolicyMakesExecutabilityDecision(string executability, bool accepted, string kind)
    {
        var decision = AgentConnectionDispatchDecision.For(executability);

        Assert.Equal(accepted, decision.Accepted);
        Assert.Equal(kind, decision.Kind);
        if (executability == AgentExecutabilityStates.NotConfigured)
            Assert.Contains("definition", decision.Reason, StringComparison.OrdinalIgnoreCase);
        if (executability == AgentExecutabilityStates.Unknown)
            Assert.Contains("Runner", decision.Reason, StringComparison.Ordinal);
    }

    private static string Derive(string json) =>
        AgentReadinessDeriver.Derive(JsonDocument.Parse(json).RootElement.Clone());
}
