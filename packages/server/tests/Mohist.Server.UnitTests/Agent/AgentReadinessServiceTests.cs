using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Agent;

public sealed class AgentReadinessServiceTests
{
    [Fact]
    public void NeverExecuted_IsUnknown()
    {
        var result = AgentReadinessService.Evaluate(Agent(), null);

        Assert.Equal(AgentReadinessConclusions.Unknown, result.Conclusion);
        Assert.Empty(result.Gaps);
        Assert.Null(result.Setup);
    }

    [Fact]
    public void MissingAgentConfiguration_RequiresSetup()
    {
        var result = AgentReadinessService.Evaluate(Agent() with { AgentConfig = null }, null);

        Assert.Equal(AgentReadinessConclusions.NeedsSetup, result.Conclusion);
        Assert.Contains(result.Gaps, gap => gap.Code == "model-missing");
        Assert.NotNull(result.Setup);
    }

    [Fact]
    public void SuccessfulExecution_IsReady_IndependentOfRuntimeAvailability()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(),
            History(AgentJobStatus.Completed));

        Assert.Equal(AgentReadinessConclusions.Ready, result.Conclusion);
    }

    [Fact]
    public void StructuralGaps_RequireSetup()
    {
        var agent = Agent(
            config: "{\"variant\":\"fast\",\"model\":\"bad-reference\"}");

        var result = AgentReadinessService.Evaluate(agent, null);

        Assert.Equal(AgentReadinessConclusions.NeedsSetup, result.Conclusion);
        Assert.Contains(result.Gaps, gap => gap.Code == "model-reference-malformed");
        Assert.DoesNotContain(result.Gaps, gap => gap.Code == "variant-without-model");
        Assert.NotNull(result.Setup);
        Assert.Equal("/agents/agent-1", result.Setup!.Path);
    }

    [Fact]
    public void VariantWithoutModel_IsAConfirmedStructuralGap()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"variant\":\"fast\"}"),
            null);

        Assert.Equal(AgentReadinessConclusions.NeedsSetup, result.Conclusion);
        Assert.Contains(result.Gaps, gap => gap.Code == "variant-without-model");
    }

    [Fact]
    public void ExecutionConfigurationFailure_RequiresSetup()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(),
            History(AgentJobStatus.Failed, "unauthorized"));

        Assert.Equal(AgentReadinessConclusions.NeedsSetup, result.Conclusion);
        Assert.Equal("execution-config-failure", Assert.Single(result.Gaps).Code);
        Assert.NotNull(result.Setup);
    }

    [Fact]
    public void InconclusiveExecution_IsUnknown()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(),
            History(AgentJobStatus.Unknown));

        Assert.Equal(AgentReadinessConclusions.Unknown, result.Conclusion);
    }

    [Fact]
    public void HistoryForOldDefinition_DoesNotConfirmCurrentDefinition()
    {
        var result = AgentReadinessService.Evaluate(
            Agent(config: "{\"model\":\"provider/new-model\"}"),
            History(AgentJobStatus.Completed, model: "provider/old-model"));

        Assert.Equal(AgentReadinessConclusions.Unknown, result.Conclusion);
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
