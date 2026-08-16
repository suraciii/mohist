using System.Text.Json;
using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

/// <summary>
/// Issue-557 T-002: <see cref="AgentLauncher.ResolveReasoningEffort"/>
/// reads the canonical <c>reasoningEffort</c> out of the Agent config so
/// it can be frozen onto every durable execution snapshot exactly like
/// <c>variant</c>. The resolver is the single launch-side read; a null
/// result means unset (never synthesized into a default).
/// </summary>
public sealed class AgentLauncherResolveReasoningEffortTests
{
    [Fact]
    public void ResolveReasoningEffort_UsesAgentEffort()
    {
        var config = JsonDocument.Parse("""{"reasoningEffort":"high"}""").RootElement;

        Assert.Equal("high", AgentLauncher.ResolveReasoningEffort(config));
    }

    [Fact]
    public void ResolveReasoningEffort_ReturnsNull_WhenUnset()
    {
        Assert.Null(AgentLauncher.ResolveReasoningEffort(null));
        Assert.Null(AgentLauncher.ResolveReasoningEffort(JsonDocument.Parse("{}").RootElement));
        Assert.Null(AgentLauncher.ResolveReasoningEffort(JsonDocument.Parse("""{"reasoningEffort":null}""").RootElement));
        Assert.Null(AgentLauncher.ResolveReasoningEffort(JsonDocument.Parse("""{"reasoningEffort":""}""").RootElement));
        Assert.Null(AgentLauncher.ResolveReasoningEffort(JsonDocument.Parse("""{"reasoningEffort":"   "}""").RootElement));
        Assert.Null(AgentLauncher.ResolveReasoningEffort(JsonDocument.Parse("""{"reasoningEffort":42}""").RootElement));
    }

    [Fact]
    public void ResolveReasoningEffort_IsIndependentFromVariant()
    {
        // The effort is its own frozen tuple member: a config with both a
        // true variant and an effort resolves both, neither derived from
        // the other.
        var config = JsonDocument.Parse(
            """{"model":"openai/gpt-5.5","variant":"balanced","reasoningEffort":"high"}""").RootElement;

        var (model, variant) = AgentLauncher.ResolveModelAndVariant(config);
        Assert.Equal("openai/gpt-5.5", model);
        Assert.Equal("balanced", variant);
        Assert.Equal("high", AgentLauncher.ResolveReasoningEffort(config));
    }

    [Fact]
    public void ResolveExecutionDefinition_CarriesTheEffortBesideModelAndVariant()
    {
        var agent = new AgentInfo(
            Id: "agent-effort",
            ProjectId: "proj-1",
            Name: "effort-agent",
            Description: "d",
            Instructions: "be terse",
            AgentConfig: JsonDocument.Parse(
                """{"model":"openai/gpt-5.5","variant":"balanced","reasoningEffort":"high"}""").RootElement,
            Skills: [],
            MaxConcurrentRuns: null,
            Status: "active",
            CreatedAt: "2024-01-01T00:00:00Z",
            UpdatedAt: "2024-01-01T00:00:00Z");

        var definition = AgentLauncher.ResolveExecutionDefinition(agent);

        Assert.Equal("openai/gpt-5.5", definition.Model);
        Assert.Equal("balanced", definition.Variant);
        Assert.Equal("high", definition.ReasoningEffort);
    }

    [Fact]
    public void ResolveExecutionDefinition_LeavesEffortNull_WhenUnset()
    {
        var agent = new AgentInfo(
            Id: "agent-no-effort",
            ProjectId: "proj-1",
            Name: "no-effort-agent",
            Description: "d",
            Instructions: "be terse",
            AgentConfig: JsonDocument.Parse("""{"model":"openai/gpt-5.5"}""").RootElement,
            Skills: [],
            MaxConcurrentRuns: null,
            Status: "active",
            CreatedAt: "2024-01-01T00:00:00Z",
            UpdatedAt: "2024-01-01T00:00:00Z");

        var definition = AgentLauncher.ResolveExecutionDefinition(agent);

        Assert.Null(definition.ReasoningEffort);
    }
}
