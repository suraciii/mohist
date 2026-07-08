using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Agent;

public class AgentJobPromptCompositionTests
{
    [Fact]
    public void ComposePromptWithEntry_RawPromptOnly_ProducesBareString()
    {
        var with = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var input = new AgentJobInput(Prompt: "hello world");

        AgentJobGrain.ComposePromptWithEntry(with, input);

        Assert.True(with.ContainsKey("prompt"));
        var prompt = with["prompt"]!.Value;
        Assert.Equal(JsonValueKind.String, prompt.ValueKind);
        Assert.Equal("hello world", prompt.GetString());
    }

    [Fact]
    public void ComposePromptWithEntry_WithInstructions_ComposesStructuredPrompt()
    {
        var with = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var input = new AgentJobInput(
            Prompt: "do the task",
            AgentId: "agent-1",
            AgentInstructions: "be terse");

        AgentJobGrain.ComposePromptWithEntry(with, input);

        var prompt = with["prompt"]!.Value;
        Assert.Equal(JsonValueKind.Object, prompt.ValueKind);
        var agentLaunch = prompt.GetProperty("agent-launch");
        Assert.Equal("be terse", agentLaunch.GetProperty("instructions").GetString());
        Assert.Equal("do the task", agentLaunch.GetProperty("prompt").GetString());
        Assert.False(agentLaunch.TryGetProperty("config", out _));
    }

    [Fact]
    public void ComposePromptWithEntry_WithInstructionsAndConfig_ComposesBothIntoStructuredPrompt()
    {
        var with = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var configElement = JsonDocument.Parse("{\"model\":\"openai/gpt-5.5\",\"temperature\":0}").RootElement.Clone();
        var input = new AgentJobInput(
            Prompt: "do the task",
            AgentId: "agent-1",
            AgentInstructions: "be terse",
            AgentConfig: configElement);

        AgentJobGrain.ComposePromptWithEntry(with, input);

        var prompt = with["prompt"]!.Value;
        Assert.Equal(JsonValueKind.Object, prompt.ValueKind);
        var agentLaunch = prompt.GetProperty("agent-launch");
        Assert.Equal("be terse", agentLaunch.GetProperty("instructions").GetString());
        Assert.Equal("do the task", agentLaunch.GetProperty("prompt").GetString());

        var config = agentLaunch.GetProperty("config");
        Assert.Equal(JsonValueKind.Object, config.ValueKind);
        Assert.Equal("openai/gpt-5.5", config.GetProperty("model").GetString());
        Assert.Equal(0, config.GetProperty("temperature").GetInt32());
    }

    [Fact]
    public void ComposePromptWithEntry_WithAgentIdOnly_StillComposesStructuredPrompt()
    {
        var with = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        var input = new AgentJobInput(
            Prompt: "just an id",
            AgentId: "agent-7");

        AgentJobGrain.ComposePromptWithEntry(with, input);

        var prompt = with["prompt"]!.Value;
        Assert.Equal(JsonValueKind.Object, prompt.ValueKind);
        var agentLaunch = prompt.GetProperty("agent-launch");
        Assert.False(agentLaunch.TryGetProperty("instructions", out _));
        Assert.False(agentLaunch.TryGetProperty("config", out _));
        Assert.Equal("just an id", agentLaunch.GetProperty("prompt").GetString());
    }
}
