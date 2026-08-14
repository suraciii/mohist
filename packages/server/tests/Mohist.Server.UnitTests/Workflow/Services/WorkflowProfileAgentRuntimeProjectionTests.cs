using System.Text.Json;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Services;

public sealed class WorkflowProfileAgentRuntimeProjectionTests
{
    [Fact]
    public void Project_MapsAllWorkflowLocationsToOneRuntime()
    {
        var deep = new TaskDefinition("deep", Uses: "mohist/opencode");
        var nested = new TaskDefinition(
            "nested",
            Uses: "mohist/opencode",
            Recovery: new RecoveryDefinition(1, [new RecoveryHandlerDefinition(
                null,
                [deep],
                false)]));
        var task = new TaskDefinition(
            "task",
            Uses: "mohist/opencode",
            Recovery: new RecoveryDefinition(1, [new RecoveryHandlerDefinition(
                null,
                [nested],
                false)]));
        var definition = new WorkflowDefinition(
            [new StageDefinition(
                "build",
                [task],
                [new CheckDefinition("check", Uses: "mohist/opencode")])],
            new ApprovalConfig(new ApprovalFeedbackConfig([
                new TaskDefinition("feedback", Uses: "mohist/opencode"),
            ])),
            new Dictionary<string, RecoveryDefinition>
            {
                ["root"] = new(1, [new RecoveryHandlerDefinition(
                    null,
                    [new TaskDefinition("root-task", Uses: "mohist/opencode")],
                    false)]),
            });

        Assert.Equal("opencode", WorkflowProfileAgentRuntimeProjection.Project(definition));
    }

    [Fact]
    public void Project_MapsStaticPiOpenSpecTask()
    {
        var definition = DefinitionWithTask(new TaskDefinition(
            "load",
            Uses: "mohist/openspec-tasks",
            With: With("""
                {"uses":"mohist/pi"}
                """)));

        Assert.Equal("pi", WorkflowProfileAgentRuntimeProjection.Project(definition));
    }

    [Fact]
    public void Project_DefaultsOpenCodeForOpenSpecTaskWithoutNestedUses()
    {
        var definition = DefinitionWithTask(new TaskDefinition("load", Uses: "mohist/openspec-tasks"));

        Assert.Equal("opencode", WorkflowProfileAgentRuntimeProjection.Project(definition));
    }

    [Fact]
    public void Project_IgnoresAgentAndNonAgentActions()
    {
        var definition = new WorkflowDefinition(
            [new StageDefinition(
                "build",
                [new TaskDefinition("agent", Uses: "mohist/agent")],
                [new CheckDefinition("script", Uses: "core/script")])]);

        Assert.Null(WorkflowProfileAgentRuntimeProjection.Project(definition));
    }

    [Fact]
    public void Project_ReturnsNullForMixedRuntimes()
    {
        var definition = new WorkflowDefinition(
            [new StageDefinition(
                "build",
                [new TaskDefinition("open", Uses: "mohist/opencode")],
                [new CheckDefinition("pi", Uses: "mohist/pi")])]);

        Assert.Null(WorkflowProfileAgentRuntimeProjection.Project(definition));
    }

    [Fact]
    public void Project_ReturnsNullForDynamicRuntimeSelection()
    {
        var definition = DefinitionWithTask(new TaskDefinition(
            "load",
            Uses: "mohist/openspec-tasks",
            With: With("""
                {"uses":"${{ vars.agent }}"}
                """)));

        Assert.Null(WorkflowProfileAgentRuntimeProjection.Project(definition));
    }

    [Fact]
    public void Project_ReturnsNullForNoRuntime()
    {
        var definition = DefinitionWithTask(new TaskDefinition("script", Uses: "core/script"));

        Assert.Null(WorkflowProfileAgentRuntimeProjection.Project(definition));
    }

    private static WorkflowDefinition DefinitionWithTask(TaskDefinition task) =>
        new([new StageDefinition("build", [task], [])]);

    private static Dictionary<string, JsonElement?> With(string json) =>
        new(StringComparer.Ordinal)
        {
            ["task"] = JsonDocument.Parse(json).RootElement.Clone(),
        };
}
