using System.Text.Json;
using Mohist.Server.Infrastructure;
using Xunit;

namespace Mohist.Server.Tests.Agent.Services;

/// <summary>
/// Precedence matrix for the single execution-field resolution rule:
/// caller hint → Agent definition, runtime defaulting to Pi, explicit
/// malformed values never masked by a lower-precedence source, and an
/// unset Model left null for the Runtime to choose at dispatch.
/// </summary>
[Trait("level", "L0")]
public sealed class ExecutionConfigResolverTests
{
    private static readonly ExecutionConfigHint Hint = new("pi", "c/three", "turbo");
    private static readonly ExecutionConfigHint Definition = new("pi", "a/one", "high");

    [Fact]
    public void Hint_WinsOverDefinition_EveryField()
    {
        var resolved = ExecutionConfigResolver.Resolve(Hint, Definition);

        Assert.Equal("pi", resolved.Runtime);
        Assert.Equal("c/three", resolved.Model);
        Assert.Equal("turbo", resolved.Variant);
    }

    [Fact]
    public void Hint_WinsPerField_NotWholeBundle()
    {
        var hint = new ExecutionConfigHint(Model: "c/three");
        var resolved = ExecutionConfigResolver.Resolve(hint, Definition);

        // The hint overrides only the model; definition values stand for the
        // fields the hint omits.
        Assert.Equal("pi", resolved.Runtime);
        Assert.Equal("c/three", resolved.Model);
        Assert.Equal("high", resolved.Variant);
    }

    [Fact]
    public void Definition_ResolvesWhenNoHintIsSupplied()
    {
        var resolved = ExecutionConfigResolver.Resolve(null, Definition);

        Assert.Equal("pi", resolved.Runtime);
        Assert.Equal("a/one", resolved.Model);
        Assert.Equal("high", resolved.Variant);
    }

    [Fact]
    public void UnsetModelAndVariant_StayNullForRuntimeBehavior()
    {
        var resolved = ExecutionConfigResolver.Resolve(null, null);

        Assert.Equal(AgentConfigSchema.DefaultRuntime, resolved.Runtime);
        Assert.Null(resolved.Model);
        Assert.Null(resolved.Variant);
    }

    [Fact]
    public void HintModel_ResolvesWithoutADefinition()
    {
        var resolved = ExecutionConfigResolver.Resolve(new ExecutionConfigHint(Model: "a/one"), null);

        Assert.Equal(AgentConfigSchema.DefaultRuntime, resolved.Runtime);
        Assert.Equal("a/one", resolved.Model);
        Assert.Null(resolved.Variant);
    }

    [Fact]
    public void Runtime_DefaultsToPi_WhenNoSourceSuppliesOne()
    {
        var resolved = ExecutionConfigResolver.Resolve(
            null,
            new ExecutionConfigHint(Model: "a/one"));

        Assert.Equal(AgentConfigSchema.DefaultRuntime, resolved.Runtime);
        Assert.Equal("a/one", resolved.Model);
        Assert.Null(resolved.Variant);
    }

    [Fact]
    public void MalformedDefinitionModel_IsPreserved()
    {
        var resolved = ExecutionConfigResolver.Resolve(null, new ExecutionConfigHint(Model: "gpt"));

        Assert.Equal("gpt", resolved.Model);
    }

    [Fact]
    public void MalformedDefinitionRuntime_IsPreserved()
    {
        var resolved = ExecutionConfigResolver.Resolve(
            null,
            new ExecutionConfigHint(Runtime: "fast", Model: "a/one"));

        Assert.Equal("fast", resolved.Runtime);
    }

    [Fact]
    public void MalformedHint_IsNeverMaskedByDefinition()
    {
        var hint = new ExecutionConfigHint(Runtime: "fast", Model: "gpt", Variant: string.Empty);
        var resolved = ExecutionConfigResolver.Resolve(hint, Definition);

        Assert.Equal("fast", resolved.Runtime);
        Assert.Equal("gpt", resolved.Model);
        // A whitespace variant counts as absent, so the definition's value
        // fills the field.
        Assert.Equal("high", resolved.Variant);
    }

    [Fact]
    public void WhitespaceValues_AreTreatedAsAbsent()
    {
        var definition = new ExecutionConfigHint(Runtime: "  ", Model: " ", Variant: "\t");
        var resolved = ExecutionConfigResolver.Resolve(null, definition);

        Assert.Equal("pi", resolved.Runtime);
        Assert.Null(resolved.Model);
        Assert.Null(resolved.Variant);
    }

    [Fact]
    public void FromAgentConfig_ReadsRawFields_AndPreservesVariantWithoutModel()
    {
        var hint = ExecutionConfigResolver.FromAgentConfig(
            JsonDocument.Parse("{\"variant\":\"fast\"}").RootElement);

        Assert.NotNull(hint);
        Assert.Null(hint!.Runtime);
        Assert.Null(hint.Model);
        Assert.Equal("fast", hint.Variant);
    }

    [Fact]
    public void FromAgentConfig_DoesNotFoldReasoningEffortIntoResolution()
    {
        var hint = ExecutionConfigResolver.FromAgentConfig(
            JsonDocument.Parse("{\"model\":\"a/one\",\"reasoningEffort\":\"high\"}").RootElement);

        Assert.NotNull(hint);
        Assert.Equal("a/one", hint!.Model);
        Assert.Null(hint.ReasoningEffort);
    }

    [Fact]
    public void FromAgentConfig_NullOrNonObject_IsNoDefinition()
    {
        Assert.Null(ExecutionConfigResolver.FromAgentConfig(null));
        Assert.Null(ExecutionConfigResolver.FromAgentConfig(JsonDocument.Parse("null").RootElement));
        Assert.Null(ExecutionConfigResolver.FromAgentConfig(JsonDocument.Parse("\"config\"").RootElement));
        Assert.Null(ExecutionConfigResolver.FromAgentConfig(JsonDocument.Parse("{}").RootElement));
    }
}
