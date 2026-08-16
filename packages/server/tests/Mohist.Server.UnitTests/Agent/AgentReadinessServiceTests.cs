using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
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

    // -------------------------------------------------------------------
    // Project default execution configuration matrix (issue-560 T-001)
    // -------------------------------------------------------------------

    private static readonly ExecutionConfigHint Default = new("pi", "b/two", null);

    [Fact]
    public void ProjectDefault_ResolvesMissingModel_ToUnknown()
    {
        var result = AgentReadinessService.Evaluate(
            Agent() with { AgentConfig = null },
            null,
            Default);

        Assert.Equal(AgentExecutabilityStates.Unknown, result.State);
        Assert.DoesNotContain(result.Gaps, gap => gap.Code == "model-missing");
    }

    [Fact]
    public void ProjectDefault_ResolvesVariantWithoutModel()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"variant\":\"fast\"}"),
            null,
            Default);

        Assert.DoesNotContain(result.Gaps, gap => gap.Code == "variant-without-model");
        Assert.DoesNotContain(result.Gaps, gap => gap.Code == "model-missing");
    }

    [Fact]
    public void WithoutDefault_TheGapRemainsNeedsSetup()
    {
        var result = AgentReadinessService.Evaluate(Agent() with { AgentConfig = null }, null, null);

        Assert.Equal(AgentExecutabilityStates.NotConfigured, result.State);
        var gap = Assert.Single(result.Gaps, g => g.Code == "model-missing");
        Assert.Equal("Set a model in Agent settings.", gap.NextAction);
        Assert.Equal("/agents/agent-1", gap.FixEntryPoint.Path);
    }

    [Fact]
    public void DefinitionModel_WinsOverProjectDefault()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"model\":\"a/one\"}"),
            History(AgentJobStatus.Completed, model: "a/one", config: "{\"model\":\"a/one\"}"),
            Default);

        // The completed execution matches the definition-resolved model, so
        // the Project default (b/two) neither changes the resolution nor the
        // conclusion.
        Assert.Equal(AgentExecutabilityStates.Executable, result.State);
    }

    [Fact]
    public void MalformedDefinitionModel_IsNotMaskedByDefault()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"model\":\"gpt\"}"),
            null,
            Default);

        Assert.Equal(AgentExecutabilityStates.NotConfigured, result.State);
        Assert.Contains(result.Gaps, gap => gap.Code == "model-reference-malformed");
        Assert.DoesNotContain(result.Gaps, gap => gap.Code == "model-missing");
    }

    [Fact]
    public void InvalidDefinitionRuntime_IsNotMaskedByDefault()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"model\":\"a/one\",\"runtime\":\"fast\"}"),
            null,
            Default);

        Assert.Equal(AgentExecutabilityStates.NotConfigured, result.State);
        Assert.Contains(result.Gaps, gap => gap.Code == "runtime-invalid");
    }

    [Fact]
    public void DefaultResolvesModel_CompletedExecutionIsReady()
    {
        var agent = Agent() with { AgentConfig = null };
        var history = History(AgentJobStatus.Completed, model: "b/two", config: null);

        var result = AgentReadinessService.Evaluate(agent, history, Default);

        Assert.Equal(AgentExecutabilityStates.Executable, result.State);
    }

    [Fact]
    public void DefaultChange_DoesNotFlipACompletedExecution()
    {
        // The Agent definition carries no model; the execution ran with the
        // default-resolved model b/two. Changing the Project default must not
        // flip the completed execution's conclusion: both sides of the
        // history match resolve under the same (current) default.
        var agent = Agent() with { AgentConfig = null };
        var history = History(AgentJobStatus.Completed, model: "b/two", config: null);
        var changedDefault = new ExecutionConfigHint("pi", "c/three", null);

        var result = AgentReadinessService.Evaluate(agent, history, changedDefault);

        Assert.Equal(AgentExecutabilityStates.Executable, result.State);
    }

    [Fact]
    public void DefinitionEdit_StillFlipsACompletedExecutionToUnknown()
    {
        var agent = Agent(config: "{\"model\":\"provider/new-model\"}");
        var history = History(AgentJobStatus.Completed, model: "provider/old-model", config: "{\"model\":\"provider/old-model\"}");

        var result = AgentReadinessService.Evaluate(agent, history, Default);

        Assert.Equal(AgentExecutabilityStates.Unknown, result.State);
    }

    [Fact]
    public void LegacyHistoryWithoutConfig_UsesPersistedDispatchForDefinitionFields()
    {
        var agent = Agent(config: "{\"model\":\"provider/model\"}");
        var history = History(AgentJobStatus.Completed, model: "provider/model", config: null);

        var result = AgentReadinessService.Evaluate(agent, history);

        Assert.Equal(AgentExecutabilityStates.Executable, result.State);
    }

    [Fact]
    public void DefaultRuntime_FillsADefinitionWithoutRuntime()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"model\":\"a/one\"}"),
            null,
            new ExecutionConfigHint("pi", "b/two", null));

        Assert.Empty(result.Gaps);
    }

    [Fact]
    public void DefaultVariant_FillsADefinitionModelGap()
    {
        // Per-field precedence: a definition model stands while the default
        // supplies the missing variant.
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"model\":\"a/one\"}"),
            null,
            new ExecutionConfigHint("pi", "b/two", "high"));

        Assert.Empty(result.Gaps);
    }

    // -------------------------------------------------------------------
    // Project default execution configuration matrix (issue-560 T-001)
    // -------------------------------------------------------------------

    private static readonly ExecutionConfigHint Default = new("pi", "b/two", null);

    [Fact]
    public void ProjectDefault_ResolvesMissingModel_ToNotNeedsSetup()
    {
        var result = AgentReadinessService.Evaluate(
            Agent() with { AgentConfig = null },
            null,
            Default);

        Assert.Equal(AgentReadinessConclusions.Unknown, result.Conclusion);
        Assert.DoesNotContain(result.Gaps, gap => gap.Code == "model-missing");
        Assert.Null(result.Setup);
    }

    [Fact]
    public void ProjectDefault_ResolvesVariantWithoutModel()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"variant\":\"fast\"}"),
            null,
            Default);

        Assert.DoesNotContain(result.Gaps, gap => gap.Code == "variant-without-model");
        Assert.DoesNotContain(result.Gaps, gap => gap.Code == "model-missing");
    }

    [Fact]
    public void WithoutDefault_TheGapRemainsNeedsSetup()
    {
        var result = AgentReadinessService.Evaluate(Agent() with { AgentConfig = null }, null, null);

        Assert.Equal(AgentReadinessConclusions.NeedsSetup, result.Conclusion);
        var gap = Assert.Single(result.Gaps, g => g.Code == "model-missing");
        Assert.Equal("Set a model in Agent settings.", gap.Action);
        Assert.NotNull(result.Setup);
    }

    [Fact]
    public void DefinitionModel_WinsOverProjectDefault()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"model\":\"a/one\"}"),
            History(AgentJobStatus.Completed, model: "a/one", config: "{\"model\":\"a/one\"}"),
            Default);

        // The completed execution matches the definition-resolved model, so
        // the Project default (b/two) neither changes the resolution nor the
        // conclusion.
        Assert.Equal(AgentReadinessConclusions.Ready, result.Conclusion);
    }

    [Fact]
    public void MalformedDefinitionModel_IsNotMaskedByDefault()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"model\":\"gpt\"}"),
            null,
            Default);

        Assert.Equal(AgentReadinessConclusions.NeedsSetup, result.Conclusion);
        Assert.Contains(result.Gaps, gap => gap.Code == "model-reference-malformed");
        Assert.DoesNotContain(result.Gaps, gap => gap.Code == "model-missing");
    }

    [Fact]
    public void InvalidDefinitionRuntime_IsNotMaskedByDefault()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"model\":\"a/one\",\"runtime\":\"fast\"}"),
            null,
            Default);

        Assert.Equal(AgentReadinessConclusions.NeedsSetup, result.Conclusion);
        Assert.Contains(result.Gaps, gap => gap.Code == "runtime-invalid");
    }

    [Fact]
    public void DefaultResolvesModel_CompletedExecutionIsReady()
    {
        var agent = Agent() with { AgentConfig = null };
        var history = History(AgentJobStatus.Completed, model: "b/two", config: null);

        var result = AgentReadinessService.Evaluate(agent, history, Default);

        Assert.Equal(AgentReadinessConclusions.Ready, result.Conclusion);
    }

    [Fact]
    public void DefaultChange_DoesNotFlipACompletedExecution()
    {
        // The Agent definition carries no model; the execution ran with the
        // default-resolved model b/two. Changing the Project default must not
        // flip the completed execution's conclusion: both sides of the
        // history match resolve under the same (current) default.
        var agent = Agent() with { AgentConfig = null };
        var history = History(AgentJobStatus.Completed, model: "b/two", config: null);
        var changedDefault = new ExecutionConfigHint("pi", "c/three", null);

        var result = AgentReadinessService.Evaluate(agent, history, changedDefault);

        Assert.Equal(AgentReadinessConclusions.Ready, result.Conclusion);
    }

    [Fact]
    public void DefinitionEdit_StillFlipsACompletedExecutionToUnknown()
    {
        var agent = Agent(config: "{\"model\":\"provider/new-model\"}");
        var history = History(AgentJobStatus.Completed, model: "provider/old-model", config: "{\"model\":\"provider/old-model\"}");

        var result = AgentReadinessService.Evaluate(agent, history, Default);

        Assert.Equal(AgentReadinessConclusions.Unknown, result.Conclusion);
    }

    [Fact]
    public void LegacyHistoryWithoutConfig_UsesPersistedDispatchForDefinitionFields()
    {
        var agent = Agent(config: "{\"model\":\"provider/model\"}");
        var history = History(AgentJobStatus.Completed, model: "provider/model", config: null);

        var result = AgentReadinessService.Evaluate(agent, history);

        Assert.Equal(AgentReadinessConclusions.Ready, result.Conclusion);
    }

    [Fact]
    public void DefaultRuntime_FillsADefinitionWithoutRuntime()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"model\":\"a/one\"}"),
            null,
            new ExecutionConfigHint("pi", "b/two", null));

        Assert.Empty(result.Gaps);
    }

    [Fact]
    public void DefaultVariant_FillsADefinitionModelGap()
    {
        // Per-field precedence: a definition model stands while the default
        // supplies the missing variant.
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"model\":\"a/one\"}"),
            null,
            new ExecutionConfigHint("pi", "b/two", "high"));

        Assert.Empty(result.Gaps);
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
        string model = "provider/model",
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
