using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Xunit;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Tests.Agent;

[Trait("level", "L0")]
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
    public void BuiltInMohistSlackWithoutSelectedModel_IsUnknownAndAdmitted()
    {
        var result = AgentReadinessService.Evaluate(
            BuiltInAgentCatalog.Resolve(BuiltInAgentCatalog.MohistSlackName),
            null);

        Assert.Equal(AgentExecutabilityStates.Unknown, result.State);
        Assert.Empty(result.Gaps);
        Assert.True(AgentConnectionDispatchDecision.For(result.State).Accepted);
    }

    [Fact]
    public void UnsetModelWithoutExecutionHistory_IsUnknownAndAdmitted()
    {
        // An Agent with an unset Model is not a configuration gap: the
        // Runtime chooses the model at dispatch.
        var result = AgentReadinessService.Evaluate(Agent() with { AgentConfig = null }, null);

        Assert.Equal(AgentExecutabilityStates.Unknown, result.State);
        Assert.Empty(result.Gaps);
        Assert.True(AgentConnectionDispatchDecision.For(result.State).Accepted);
    }

    [Fact]
    public void MissingInstructions_IsNotConfigured()
    {
        var result = AgentReadinessService.Evaluate(Agent(instructions: ""), null);

        Assert.Equal(AgentExecutabilityStates.NotConfigured, result.State);
        Assert.Equal("/agents/agent-1", Assert.Single(result.Gaps).FixEntryPoint.Path);
        Assert.Equal("instructions-missing", Assert.Single(result.Gaps).Code);
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
    public void VariantOrEffortWithoutModel_IsNotAGap()
    {
        // Both apply to the Runtime-chosen model, so neither is a structural
        // gap on its own.
        var variant = AgentReadinessService.Evaluate(Agent(config: "{\"variant\":\"fast\"}"), null);
        var effort = AgentReadinessService.Evaluate(Agent(config: "{\"reasoningEffort\":\"high\"}"), null);

        Assert.Equal(AgentExecutabilityStates.Unknown, variant.State);
        Assert.Empty(variant.Gaps);
        Assert.Equal(AgentExecutabilityStates.Unknown, effort.State);
        Assert.Empty(effort.Gaps);
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
            History(AgentJobStatus.Completed, model: "provider/old-model", config: "{\"model\":\"provider/old-model\"}"));

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

    [Fact]
    public void CompletedExecutionWithUnsetModel_IsExecutable()
    {
        // A completed execution with a Runtime-chosen model (null on both
        // sides) confirms the current definition.
        var agent = Agent() with { AgentConfig = null };
        var history = History(AgentJobStatus.Completed, model: null, config: null);

        var result = AgentReadinessService.Evaluate(agent, history);

        Assert.Equal(AgentExecutabilityStates.Executable, result.State);
    }

    [Fact]
    public void CompletedExecutionWithChoosenModel_DoesNotMatchAnUnsetDefinition()
    {
        // The Runtime chose a model at dispatch; the definition never
        // carried one, so the completed execution still matches the
        // definition's resolved (null) model.
        var agent = Agent() with { AgentConfig = null };
        var history = History(AgentJobStatus.Completed, model: "runtime/choice", config: null);

        var result = AgentReadinessService.Evaluate(agent, history);

        Assert.Equal(AgentExecutabilityStates.Executable, result.State);
    }

    [Fact]
    public void ExplicitRuntimeDefault_FillsADefinitionWithoutRuntime()
    {
        var result = AgentReadinessService.Evaluate(Agent(config: "{\"model\":\"a/one\"}"), null);

        Assert.Empty(result.Gaps);
    }

    private static AgentInfo Agent(
        string config = "{\"model\":\"provider/model\"}",
        string instructions = "Do the work") => new(
        "agent-1",
        "project-1",
        "Agent",
        "",
        instructions,
        JsonDocument.Parse(config).RootElement,
        [],
        null,
        "active",
        "2026-01-01T00:00:00Z",
        "2026-01-01T00:00:00Z");

    private static AgentExecutionHistory History(
        AgentJobStatus status,
        string? category = null,
        string? model = "provider/model",
        string? config = "{\"model\":\"provider/model\"}") => new(
        status,
        category,
        new AgentJobInput(
            Prompt: "prompt",
            Model: model,
            AgentId: "agent-1",
            AgentInstructions: "Do the work",
            AgentConfig: config is null ? null : JsonDocument.Parse(config).RootElement,
            Runtime: "opencode",
            Skills: []),
        DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
}
