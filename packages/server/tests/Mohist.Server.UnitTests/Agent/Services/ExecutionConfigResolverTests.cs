using System.Text.Json;
using Mohist.Server.Infrastructure;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

/// <summary>
/// Precedence matrix for the single execution-field resolution rule
/// (issue-560 T-001): caller hint → Agent definition → Project default,
/// runtime defaulting to opencode, explicit malformed values never masked
/// by a lower-precedence source.
/// </summary>
public sealed class ExecutionConfigResolverTests
{
    private static readonly ExecutionConfigHint Hint = new("pi", "c/three", "turbo");
    private static readonly ExecutionConfigHint Definition = new("pi", "a/one", "high");
    private static readonly ExecutionConfigHint ProjectDefault = new("pi", "b/two", "low");

    [Fact]
    public void Hint_WinsOverDefinitionAndDefault_EveryField()
    {
        var resolved = ExecutionConfigResolver.Resolve(Hint, Definition, ProjectDefault);

        Assert.Equal("pi", resolved.Runtime);
        Assert.Equal("c/three", resolved.Model);
        Assert.Equal("turbo", resolved.Variant);
    }

    [Fact]
    public void Hint_WinsPerField_NotWholeBundle()
    {
        var hint = new ExecutionConfigHint(Model: "c/three");
        var resolved = ExecutionConfigResolver.Resolve(hint, Definition, ProjectDefault);

        // The hint overrides only the model; definition values stand for the
        // fields the hint omits.
        Assert.Equal("pi", resolved.Runtime);
        Assert.Equal("c/three", resolved.Model);
        Assert.Equal("high", resolved.Variant);
    }

    [Fact]
    public void Definition_WinsOverDefault()
    {
        var resolved = ExecutionConfigResolver.Resolve(null, Definition, ProjectDefault);

        Assert.Equal("pi", resolved.Runtime);
        Assert.Equal("a/one", resolved.Model);
        Assert.Equal("high", resolved.Variant);
    }

    [Fact]
    public void Default_FillsADefinitionGap_PerField()
    {
        var definition = new ExecutionConfigHint(Model: "a/one");
        var resolved = ExecutionConfigResolver.Resolve(null, definition, ProjectDefault);

        Assert.Equal("pi", resolved.Runtime);
        Assert.Equal("a/one", resolved.Model);
        Assert.Equal("low", resolved.Variant);
    }

    [Fact]
    public void Default_ResolvesAnEmptyDefinition()
    {
        var resolved = ExecutionConfigResolver.Resolve(null, null, ProjectDefault);

        Assert.Equal("pi", resolved.Runtime);
        Assert.Equal("b/two", resolved.Model);
        Assert.Equal("low", resolved.Variant);
    }

    [Fact]
    public void Runtime_DefaultsToOpenCode_WhenNoSourceSuppliesOne()
    {
        var resolved = ExecutionConfigResolver.Resolve(
            null,
            new ExecutionConfigHint(Model: "a/one"),
            new ExecutionConfigHint(Model: "b/two"));

        Assert.Equal(AgentConfigSchema.OpenCodeRuntime, resolved.Runtime);
        Assert.Equal("a/one", resolved.Model);
        Assert.Null(resolved.Variant);
    }

    [Fact]
    public void EmptySources_ResolveToOpenCodeWithNoModel()
    {
        var resolved = ExecutionConfigResolver.Resolve(null, null, null);

        Assert.Equal(AgentConfigSchema.OpenCodeRuntime, resolved.Runtime);
        Assert.Null(resolved.Model);
        Assert.Null(resolved.Variant);
    }

    [Fact]
    public void MalformedDefinitionModel_IsNeverMaskedByDefault()
    {
        var definition = new ExecutionConfigHint(Model: "gpt");
        var resolved = ExecutionConfigResolver.Resolve(null, definition, ProjectDefault);

        Assert.Equal("gpt", resolved.Model);
    }

    [Fact]
    public void MalformedDefinitionRuntime_IsNeverMaskedByDefault()
    {
        var definition = new ExecutionConfigHint(Runtime: "fast", Model: "a/one");
        var resolved = ExecutionConfigResolver.Resolve(null, definition, ProjectDefault);

        Assert.Equal("fast", resolved.Runtime);
    }

    [Fact]
    public void MalformedHint_IsNeverMaskedByDefinitionOrDefault()
    {
        var hint = new ExecutionConfigHint(Runtime: "fast", Model: "gpt", Variant: string.Empty);
        var resolved = ExecutionConfigResolver.Resolve(hint, Definition, ProjectDefault);

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
        var resolved = ExecutionConfigResolver.Resolve(null, definition, ProjectDefault);

        Assert.Equal("pi", resolved.Runtime);
        Assert.Equal("b/two", resolved.Model);
        Assert.Equal("low", resolved.Variant);
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
    public void FromAgentConfig_NullOrNonObject_IsNoDefinition()
    {
        Assert.Null(ExecutionConfigResolver.FromAgentConfig(null));
        Assert.Null(ExecutionConfigResolver.FromAgentConfig(JsonDocument.Parse("null").RootElement));
        Assert.Null(ExecutionConfigResolver.FromAgentConfig(JsonDocument.Parse("\"config\"").RootElement));
        Assert.Null(ExecutionConfigResolver.FromAgentConfig(JsonDocument.Parse("{}").RootElement));
    }
}

/// <summary>
/// Storage codec for the persisted execution-config selections (the
/// Project default column): round-trips the supplied fields, reads absent
/// or malformed storage as unset.
/// </summary>
public sealed class ExecutionConfigJsonTests
{
    [Fact]
    public void RoundTrips_SuppliedFields()
    {
        var config = new ExecutionConfigHint("pi", "openai/gpt-5.6", "high");

        var parsed = ExecutionConfigJson.Deserialize(ExecutionConfigJson.Serialize(config));

        Assert.Equal(config, parsed);
    }

    [Fact]
    public void RoundTrips_WithoutVariant()
    {
        var config = new ExecutionConfigHint("opencode", "openai/gpt-5.6");

        var parsed = ExecutionConfigJson.Deserialize(ExecutionConfigJson.Serialize(config));

        Assert.Equal(config, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentStorage_DeserializesToUnset(string? json)
    {
        Assert.Null(ExecutionConfigJson.Deserialize(json));
    }

    [Fact]
    public void MalformedStorage_DeserializesToUnset()
    {
        Assert.Null(ExecutionConfigJson.Deserialize("not json"));
        Assert.Null(ExecutionConfigJson.Deserialize("[1,2]"));
    }
}
