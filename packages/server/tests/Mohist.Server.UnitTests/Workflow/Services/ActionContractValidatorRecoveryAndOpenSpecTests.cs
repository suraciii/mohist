using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Services;

public partial class ActionContractValidatorTests
{
    [Fact]
    public void Validate_MissingRequiredInputInTopLevelRecoveryTask_UsesTopLevelRecoveryPath()
    {
        var recoveryTask = new TaskDefinition(
            "recover",
            Uses: "mohist/pi",
            With: With(("prompt", JsonString("repair the failure"))));
        var recovery = new RecoveryDefinition(1, [new RecoveryHandlerDefinition(null, [recoveryTask], RetrySelf: false)]);
        var definition = new WorkflowDefinition(
            [],
            Recoveries: new Dictionary<string, RecoveryDefinition>(StringComparer.Ordinal)
            {
                ["build"] = recovery,
            });
        var catalog = new ActionCatalog(
            [CreateAgentAction(
                "mohist/pi",
                StringInput("prompt", required: true),
                StringInput("session", required: true))],
            []);

        var error = Assert.Single(ActionContractValidator.Validate(definition, catalog));

        Assert.Equal("recoveries.build.handlers[0].tasks[0].with.session", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("missing required input 'session'", error.Message);
    }

    [Fact]
    public void Validate_TopLevelRecoveryTasks_RecursivelyUseTaskPaths()
    {
        var nestedTask = new TaskDefinition("nested", Uses: "mohist/nested-missing");
        var nestedRecovery = new RecoveryDefinition(
            1,
            [new RecoveryHandlerDefinition(null, [nestedTask], RetrySelf: false)]);
        var directTask = new TaskDefinition("direct", Uses: "mohist/direct-missing");
        var parentTask = new TaskDefinition("parent", Uses: "mohist/opencode", Recovery: nestedRecovery);
        var definition = new WorkflowDefinition(
            [],
            Recoveries: new Dictionary<string, RecoveryDefinition>(StringComparer.Ordinal)
            {
                ["build"] = new RecoveryDefinition(
                    1,
                    [new RecoveryHandlerDefinition(null, [directTask, parentTask], RetrySelf: false)]),
            });
        var catalog = new ActionCatalog([CreateAction("mohist/opencode")], []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, error =>
            error.Path == "recoveries.build.handlers[0].tasks[0]"
            && error.Message.Contains("mohist/direct-missing", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Path == "recoveries.build.handlers[0].tasks[1].recovery.handlers[0].tasks[0]"
            && error.Message.Contains("mohist/nested-missing", StringComparison.Ordinal));
        Assert.All(errors, error => Assert.Equal(ValidationSource.Action, error.Source));
    }

    [Fact]
    public void Validate_OpenSpecGeneratedAgentTaskMissingRequiredInput_UsesNestedTaskPath()
    {
        var task = OpenSpecTask("""
            {
              "uses": "mohist/pi",
              "with": { "prompt": "implement the task" }
            }
            """);
        var catalog = OpenSpecCatalog(
            CreateAgentAction(
                "mohist/pi",
                StringInput("prompt", required: true),
                StringInput("session", required: true)));

        var error = Assert.Single(ActionContractValidator.Validate(
            new WorkflowDefinition([new StageDefinition("build", [task], [])]), catalog));

        Assert.Equal("stages[0].tasks[0].with.task.with.session", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("missing required input 'session'", error.Message);
    }

    [Fact]
    public void Validate_OpenSpecGeneratedAgentTaskInputType_UsesNestedTaskPath()
    {
        var task = OpenSpecTask("""
            {
              "uses": "mohist/pi",
              "with": { "prompt": "implement", "session": 42 }
            }
            """);
        var catalog = OpenSpecCatalog(
            CreateAgentAction(
                "mohist/pi",
                StringInput("prompt", required: true),
                StringInput("session", required: true)));

        var error = Assert.Single(ActionContractValidator.Validate(
            new WorkflowDefinition([new StageDefinition("build", [task], [])]), catalog));

        Assert.Equal("stages[0].tasks[0].with.task.with.session", error.Path);
        Assert.Contains("must be string, received number", error.Message);
    }

    [Fact]
    public void Validate_OpenSpecGeneratedTaskRequiresUses()
    {
        var task = OpenSpecTask("""{ "with": { "prompt": "implement" } }""");

        var error = Assert.Single(ActionContractValidator.Validate(
            new WorkflowDefinition([new StageDefinition("build", [task], [])]),
            OpenSpecCatalog()));

        Assert.Equal("stages[0].tasks[0].with.task.uses", error.Path);
        Assert.Contains("non-empty", error.Message);
    }

    private static TaskDefinition OpenSpecTask(string taskJson) =>
        new(
            "load-tasks",
            Uses: "mohist/openspec-tasks",
            With: With(
                ("path", JsonString("openspec/changes/example/tasks.json")),
                ("task", JsonObject(taskJson))));

    private static ActionCatalog OpenSpecCatalog(params ActionCatalogEntry[] generatedActions) =>
        new(
            [
                CreateAction(
                    "mohist/openspec-tasks",
                    StringInput("path", required: true),
                    UnionInput("task", ["object"], required: true)),
                .. generatedActions,
            ],
            []);
}
