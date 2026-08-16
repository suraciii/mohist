using Mohist.Server.Infrastructure;
using Mohist.Server.Agent.Services;
using Mohist.Server.Agent.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class AgentLaunchExecutionResolverTests
{
    private static AgentExecutionDefinition Saved() => new(
        Instructions: "instructions",
        Runtime: AgentConfigSchema.PiRuntime,
        Model: "openai/gpt-5.6",
        Variant: "balanced",
        Skills: ["coding"],
        ReasoningEffort: ReasoningEfforts.Medium);

    [Fact]
    public void OmittedFieldsInheritSavedDefinition()
    {
        var result = AgentLaunchExecutionResolver.Resolve(Saved(), null);

        Assert.Equal("pi", result.Definition.Runtime);
        Assert.Equal("openai/gpt-5.6", result.Definition.Model);
        Assert.Equal("balanced", result.Definition.Variant);
        Assert.Equal(ReasoningEfforts.Medium, result.Definition.ReasoningEffort);
        Assert.False(result.HasOverride);
        Assert.True(result.MatchesSavedDefinition);
        Assert.Equal("configured", result.CapabilityState);
        Assert.All(result.Sources.Values, value => Assert.Equal("agent", value));
    }

    [Fact]
    public void ExplicitOverrideChangesTupleWithoutFallback()
    {
        var result = AgentLaunchExecutionResolver.Resolve(
            Saved(),
            new AgentLaunchExecutionOverride(
                RuntimeSpecified: true,
                Runtime: AgentConfigSchema.OpenCodeRuntime,
                ModelSpecified: false,
                Model: null,
                VariantSpecified: false,
                Variant: null,
                ReasoningEffortSpecified: true,
                ReasoningEffort: ReasoningEfforts.High,
                CanonicalJson: "{\"reasoningEffort\":\"high\",\"runtime\":\"opencode\"}"));

        Assert.Equal("opencode", result.Definition.Runtime);
        Assert.Equal(ReasoningEfforts.High, result.Definition.ReasoningEffort);
        Assert.Equal("override", result.Sources["runtime"]);
        Assert.Equal("override", result.Sources["reasoningEffort"]);
        Assert.Equal("agent", result.Sources["model"]);
        Assert.True(result.HasOverride);
        Assert.False(result.MatchesSavedDefinition);
        Assert.Equal("unknown", result.CapabilityState);
    }

    [Fact]
    public void InvalidRuntimeIsRejectedDeterministically()
    {
        var exception = Assert.Throws<AgentLaunchExecutionValidationException>(() =>
            AgentLaunchExecutionResolver.Resolve(
                Saved(),
                new AgentLaunchExecutionOverride(
                    true, "mystery", false, null, false, null, false, null, "{}")));

        Assert.Equal("invalid_execution_override", exception.ErrorCode);
        Assert.Contains("execution.runtime", exception.Message);
    }

    [Fact]
    public void VariantCannotSurviveClearedModel()
    {
        var exception = Assert.Throws<AgentLaunchExecutionValidationException>(() =>
            AgentLaunchExecutionResolver.Resolve(
                Saved(),
                new AgentLaunchExecutionOverride(
                    false, null, true, null, false, null, false, null, "{\"model\":null}")));

        Assert.Contains("execution.variant", exception.Message);
    }

    [Fact]
    public void FingerprintDistinguishesOmittedAndExplicitNullOverride()
    {
        var omitted = new AgentLaunchCoordinatorRequest(
            "task", "agent", null, null, null, null, null, null);
        var explicitNull = omitted with { ExecutionOverrideJson = "{\"model\":null}" };

        Assert.NotEqual(
            AgentLaunchCoordinatorCodec.Fingerprint(omitted),
            AgentLaunchCoordinatorCodec.Fingerprint(explicitNull));
    }
}
