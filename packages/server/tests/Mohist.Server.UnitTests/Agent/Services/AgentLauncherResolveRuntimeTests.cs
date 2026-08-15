using System.Text.Json;
using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Agent.Services;

public sealed class AgentLauncherResolveRuntimeTests
{
    [Fact]
    public void ResolveRuntime_UsesAgentRuntime()
    {
        var config = JsonDocument.Parse("{\"runtime\":\"pi\"}").RootElement;

        Assert.Equal("pi", AgentLauncher.ResolveRuntime(config));
    }

    [Fact]
    public void ResolveRuntime_DefaultsToOpenCode()
    {
        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(null));
        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(JsonDocument.Parse("{\"runtime\":\"mystery\"}").RootElement));
        Assert.Equal("opencode", AgentLauncher.ResolveRuntime(JsonDocument.Parse("{\"runtime\":42}").RootElement));
    }

    [Fact]
    public void ResolveExecutionDefinition_PreservesReasoningEffortIndependentlyFromVariant()
    {
        var agent = new AgentInfo(
            Id: "agent-1",
            ProjectId: "project-1",
            Name: "Reviewer",
            Description: string.Empty,
            Instructions: "Review the change",
            AgentConfig: JsonDocument.Parse("{\"runtime\":\"pi\",\"model\":\"openai/gpt-5.5\",\"reasoningEffort\":\"high\",\"variant\":\"balanced\"}").RootElement,
            Skills: [],
            MaxConcurrentRuns: null,
            Status: "active",
            CreatedAt: "2026-08-15T00:00:00Z",
            UpdatedAt: "2026-08-15T00:00:00Z");

        var definition = AgentLauncher.ResolveExecutionDefinition(agent);

        Assert.Equal("pi", definition.Runtime);
        Assert.Equal("openai/gpt-5.5", definition.Model);
        Assert.Equal("high", definition.ReasoningEffort);
        Assert.Equal("balanced", definition.Variant);
    }
}
