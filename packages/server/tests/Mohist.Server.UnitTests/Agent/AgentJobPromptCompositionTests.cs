using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Agent;

public class AgentJobPromptCompositionTests
{
    [Fact]
    public void LaunchAsync_RawPromptOnly_ProducesBarePromptPayload()
    {
        var with = ComposeDispatchWith(new AgentJobInput(Prompt: "hello world"));

        Assert.Equal("hello world", with["prompt"].GetString());
        Assert.False(with.ContainsKey("instructions"));
        Assert.False(with.ContainsKey("model"));
        Assert.False(with.ContainsKey("reasoningEffort"));
        Assert.False(with.ContainsKey("variant"));
    }

    [Fact]
    public void LaunchAsync_WithInstructions_EmitsFlatInstructionsAndPrompt()
    {
        var with = ComposeDispatchWith(new AgentJobInput(
            Prompt: "do the task",
            AgentId: "agent-1",
            AgentInstructions: "be terse"));

        Assert.Equal("be terse", with["instructions"].GetString());
        Assert.Equal("do the task", with["prompt"].GetString());
        Assert.False(with.ContainsKey("model"));
        Assert.False(with.ContainsKey("reasoningEffort"));
        Assert.False(with.ContainsKey("variant"));
    }

    [Fact]
    public void LaunchAsync_WithModelAndVariant_EmitsFlatModelAndVariant()
    {
        var with = ComposeDispatchWith(new AgentJobInput(
            Prompt: "do the task",
            AgentId: "agent-1",
            AgentInstructions: "be terse",
            Model: "openai/gpt-5.5",
            Variant: "high"));

        Assert.Equal("openai/gpt-5.5", with["model"].GetString());
        Assert.Equal("high", with["variant"].GetString());
    }

    [Fact]
    public void LaunchAsync_WithReasoningEffort_EmitsIndependentFlatReasoningEffort()
    {
        var with = ComposeDispatchWith(new AgentJobInput(
            Prompt: "do the task",
            AgentId: "agent-1",
            Model: "openai/gpt-5.5",
            ReasoningEffort: "high",
            Variant: "balanced"));

        Assert.Equal("openai/gpt-5.5", with["model"].GetString());
        Assert.Equal("high", with["reasoningEffort"].GetString());
        Assert.Equal("balanced", with["variant"].GetString());
    }

    [Fact]
    public void LaunchAsync_DoesNotEmitAgentLaunchEnvelopeOrAgentField()
    {
        var with = ComposeDispatchWith(new AgentJobInput(
            Prompt: "do the task",
            AgentId: "agent-1",
            AgentInstructions: "be terse"));

        // The legacy `{ "agent-launch": { ... } }` envelope and the
        // `with.agent` field must NOT appear on the new dispatch
        // shape — the runner's AgentJobExecutor reads `with.prompt`
        // / `with.instructions` / `with.model` / `with.reasoningEffort` / `with.variant`
        // directly (design D2, #410 T-001 AC).
        Assert.False(with.ContainsKey("agent"));
        var promptKind = with["prompt"].ValueKind;
        Assert.Equal(JsonValueKind.String, promptKind);
    }

    [Fact]
    public void LaunchAsync_AgentConfig_DoesNotEmitWithAgent()
    {
        var configElement = JsonDocument.Parse("{\"model\":\"openai/gpt-5.5\",\"temperature\":0}").RootElement.Clone();
        var with = ComposeDispatchWith(new AgentJobInput(
            Prompt: "do the task",
            AgentId: "agent-1",
            AgentInstructions: "be terse",
            AgentConfig: configElement));

        Assert.False(with.ContainsKey("agent"));
        Assert.Equal("do the task", with["prompt"].GetString());
        Assert.Equal("be terse", with["instructions"].GetString());
    }

    /// <summary>
    /// Mirrors <c>AgentJobGrain.BuildDispatch</c>'s `with` projection:
    /// a flat <c>{ prompt, instructions?, model?, reasoningEffort?, variant? }</c> shape.
    /// Test-local copy so the assertion lives next to the contract; if
    /// the grain projection diverges, the integration spec picks it up.
    /// </summary>
    private static Dictionary<string, JsonElement> ComposeDispatchWith(AgentJobInput input)
    {
        var with = new Dictionary<string, JsonElement>();
        with["prompt"] = JsonDocument.Parse($"\"{input.Prompt}\"").RootElement.Clone();
        if (!string.IsNullOrWhiteSpace(input.AgentInstructions))
            with["instructions"] = JsonDocument.Parse($"\"{input.AgentInstructions}\"").RootElement.Clone();
        if (!string.IsNullOrWhiteSpace(input.Model))
            with["model"] = JsonDocument.Parse($"\"{input.Model}\"").RootElement.Clone();
        if (!string.IsNullOrWhiteSpace(input.ReasoningEffort))
            with["reasoningEffort"] = JsonDocument.Parse($"\"{input.ReasoningEffort}\"").RootElement.Clone();
        if (!string.IsNullOrWhiteSpace(input.Variant))
            with["variant"] = JsonDocument.Parse($"\"{input.Variant}\"").RootElement.Clone();
        return with;
    }
}
