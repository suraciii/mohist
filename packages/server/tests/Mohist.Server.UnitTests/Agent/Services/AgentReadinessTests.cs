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
        Assert.Equal(executability, decision.Executability!.State);
        if (executability == AgentExecutabilityStates.NotConfigured)
            Assert.Contains("definition", decision.Reason, StringComparison.OrdinalIgnoreCase);
        if (executability == AgentExecutabilityStates.Unknown)
            Assert.Contains("Runner", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionAvailabilityIsIndependentAndTakesPrecedence()
    {
        var setupIncomplete = new AgentConnection
        {
            SetupProgress = SetupProgressKind.FixSlackSetup,
            ConnectionHealth = ConnectionHealthKind.Healthy,
        };
        var unhealthy = new AgentConnection
        {
            SetupProgress = SetupProgressKind.Complete,
            ConnectionHealth = ConnectionHealthKind.Unhealthy,
            HealthReason = "service offline",
        };
        var offlineGap = new AgentConnection
        {
            SetupProgress = SetupProgressKind.Complete,
            ConnectionHealth = ConnectionHealthKind.Healthy,
            OfflineGapAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        };
        var backpressured = new AgentConnection
        {
            SetupProgress = SetupProgressKind.Complete,
            ConnectionHealth = ConnectionHealthKind.Degraded,
            HealthReason = SlackConnectionBackpressureReasons.InboxOverflow,
        };

        Assert.Equal("connection_unavailable", AgentConnectionDispatchDecision.ForConnection(setupIncomplete).Kind);
        Assert.Equal("connection_unavailable", AgentConnectionDispatchDecision.ForConnection(unhealthy).Kind);
        Assert.Equal("connection_unavailable", AgentConnectionDispatchDecision.ForConnection(offlineGap).Kind);
        Assert.Equal("backpressured", AgentConnectionDispatchDecision.ForConnection(backpressured).Kind);
        Assert.True(AgentConnectionDispatchDecision.ForConnection(backpressured).ConnectionUnavailable);
    }

    [Fact]
    public void DisabledConnectionIsNotUnavailable()
    {
        var decision = AgentConnectionDispatchDecision.ForConnection(new AgentConnection
        {
            DesiredState = DesiredStateKind.Disabled,
            SetupProgress = SetupProgressKind.FixSlackSetup,
            ConnectionHealth = ConnectionHealthKind.Unhealthy,
            OfflineGapAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        });

        Assert.True(decision.Accepted);
        Assert.Equal("accepted", decision.Kind);
        Assert.False(decision.ConnectionUnavailable);
    }

    private static string Derive(string json) =>
        AgentReadinessDeriver.Derive(JsonDocument.Parse(json).RootElement.Clone());
}
