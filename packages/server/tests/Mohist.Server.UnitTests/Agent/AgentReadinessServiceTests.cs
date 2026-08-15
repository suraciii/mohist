using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Agent;

public sealed class AgentReadinessServiceTests
{
    [Fact]
    public void NeverExecuted_IsUnknownAndAdmitted()
    {
        var result = AgentReadinessService.Evaluate(Agent(), null);

        Assert.Equal(AgentExecutabilityStates.Unknown, result.State);
        Assert.Empty(result.Gaps);
        Assert.NotNull(result.PendingLaunchNote);
        Assert.True(AgentConnectionDispatchDecision.For(result.State).Accepted);
    }

    [Fact]
    public void MissingAgentConfiguration_IsNotConfigured()
    {
        var result = AgentReadinessService.Evaluate(Agent() with { AgentConfig = null }, null);

        Assert.Equal(AgentExecutabilityStates.NotConfigured, result.State);
        Assert.Contains(result.Gaps, gap => gap.Code == "model-missing");
        Assert.Equal("/agents/agent-1", Assert.Single(result.Gaps).FixEntryPoint.Path);
        Assert.True(AgentExecutabilityStates.IsBlocked(result.State));
    }

    [Fact]
    public void SuccessfulExecution_IsExecutable_IndependentOfRuntimeAvailability()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(),
            History(AgentJobStatus.Completed));

        Assert.Equal(AgentExecutabilityStates.Executable, result.State);
        Assert.Null(result.PendingLaunchNote);
    }

    [Fact]
    public void StructuralGaps_AreNotConfigured()
    {
        var agent = Agent(
            config: "{\"variant\":\"fast\",\"model\":\"bad-reference\"}");

        var result = AgentReadinessService.Evaluate(agent, null);

        Assert.Equal(AgentExecutabilityStates.NotConfigured, result.State);
        Assert.Contains(result.Gaps, gap => gap.Code == "model-reference-malformed");
        Assert.DoesNotContain(result.Gaps, gap => gap.Code == "variant-without-model");
        Assert.Equal("/agents/agent-1", Assert.Single(result.Gaps).FixEntryPoint.Path);
    }

    [Fact]
    public void VariantWithoutModel_IsNotConfigured()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"variant\":\"fast\"}"),
            null);

        Assert.Equal(AgentExecutabilityStates.NotConfigured, result.State);
        Assert.Contains(result.Gaps, gap => gap.Code == "variant-without-model");
    }

    [Fact]
    public void ReasoningEffortWithoutModel_IsNotConfigured()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"reasoningEffort\":\"high\"}"),
            null);

        Assert.Equal(AgentExecutabilityStates.NotConfigured, result.State);
        Assert.Contains(result.Gaps, gap => gap.Code == "model-missing");
        Assert.Contains(result.Gaps, gap => gap.Code == "reasoning-effort-without-model");
    }

    [Fact]
    public void ExecutionConfigurationFailure_IsNotExecutable()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(),
            History(AgentJobStatus.Failed, "unauthorized"));

        Assert.Equal(AgentExecutabilityStates.NotExecutable, result.State);
        Assert.Equal("execution-config-failure", Assert.Single(result.Gaps).Code);
        Assert.True(AgentExecutabilityStates.IsBlocked(result.State));
    }

    [Theory]
    [InlineData("incompatible-runtime")]
    [InlineData("runtime-invalid")]
    [InlineData("unsupported-execution-configuration")]
    [InlineData("incompatible-execution-configuration")]
    public void DeterministicRuntimeConfigurationFailure_IsNotExecutable(string category)
    {
        var result = AgentReadinessService.Evaluate(
            Agent(),
            History(AgentJobStatus.Failed, category));

        Assert.Equal(AgentExecutabilityStates.NotExecutable, result.State);
        Assert.Equal("execution-config-failure", Assert.Single(result.Gaps).Code);
    }

    [Theory]
    [InlineData("runtime-unavailable")]
    [InlineData("unavailable-runtime")]
    [InlineData("runner-unavailable")]
    public void RuntimeUnavailable_IsUnknownUntilRunnerCanBeObserved(string category)
    {
        var result = AgentReadinessService.Evaluate(
            Agent(),
            History(AgentJobStatus.Failed, category));

        Assert.Equal(AgentExecutabilityStates.Unknown, result.State);
        Assert.Empty(result.Gaps);
        Assert.NotNull(result.PendingLaunchNote);
    }

    [Fact]
    public void GenericInvalidInput_IsUnknownAndDoesNotBlockLaunch()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(),
            History(AgentJobStatus.Failed, "invalid-input"));

        Assert.Equal(AgentExecutabilityStates.Unknown, result.State);
        Assert.Empty(result.Gaps);
        Assert.NotNull(result.PendingLaunchNote);
        Assert.True(AgentConnectionDispatchDecision.For(result.State).Accepted);
    }

    [Fact]
    public void InconclusiveExecution_IsUnknown()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(),
            History(AgentJobStatus.Unknown));

        Assert.Equal(AgentExecutabilityStates.Unknown, result.State);
    }

    [Fact]
    public void HistoryForOldDefinition_DoesNotConfirmCurrentDefinition()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"model\":\"provider/new-model\"}"),
            History(AgentJobStatus.Completed, model: "provider/old-model"));

        Assert.Equal(AgentExecutabilityStates.Unknown, result.State);
    }

    [Theory]
    [InlineData(AgentExecutabilityStates.NotConfigured)]
    [InlineData(AgentExecutabilityStates.NotExecutable)]
    public void BlockedExecutability_RejectsConnectionDispatch(string state)
    {
        var decision = AgentConnectionDispatchDecision.For(state);

        Assert.False(decision.Accepted);
        Assert.Equal(state == AgentExecutabilityStates.NotConfigured ? "agent_not_configured" : "agent_not_executable", decision.Kind);
    }

    private static AgentInfo Agent(string config = "{\"model\":\"provider/model\"}") => new(
        "agent-1",
        "project-1",
        "Agent",
        "",
        "Do the work",
        JsonDocument.Parse(config).RootElement,
        [],
        null,
        "active",
        "2026-01-01T00:00:00Z",
        "2026-01-01T00:00:00Z");

    private static AgentExecutionHistory History(
        AgentJobStatus status,
        string? category = null,
        string model = "provider/model") => new(
        status,
        category,
        new AgentJobInput(
            Prompt: "prompt",
            Model: model,
            AgentId: "agent-1",
            AgentInstructions: "Do the work",
            Runtime: "opencode",
            Skills: []),
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
}
