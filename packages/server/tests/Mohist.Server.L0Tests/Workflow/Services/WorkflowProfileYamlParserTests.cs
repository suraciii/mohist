using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.Services;

public sealed class WorkflowProfileYamlParserTests
{
    [Fact]
    public void Parse_AcceptsConcreteMohistAgentBinding()
    {
        var profile = WorkflowProfileYamlParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: task
                    uses: mohist/agent
                    with:
                      name: mohist/builder
                      session: build
                      prompt: Build the change.
            """, "profile", Catalog());

        var task = profile.Definition.Stages[0].Tasks[0];
        Assert.Equal("mohist/agent", task.Uses);
        Assert.Equal("mohist/builder", task.With!["name"]!.Value.GetString());
    }

    [Fact]
    public void Parse_RejectsRemovedAgentActionMetadata()
    {
        var exception = Assert.Throws<WorkflowDefinitionValidationException>(() =>
            WorkflowProfileYamlParser.Parse("""
                agentAction: mohist/opencode
                stages:
                  - stage: build
                """, "profile", Catalog()));

        Assert.Contains(exception.Errors, error => error.Path == "agentAction");
    }

    [Theory]
    [InlineData("mohist/opencode")]
    [InlineData("mohist/pi")]
    public void Parse_RejectsRemovedRuntimeActionsInTasks(string uses)
    {
        var yaml = $$"""
            stages:
              - stage: build
                tasks:
                  - id: task
                    uses: {{uses}}
                    with:
                      prompt: Build.
            """;

        var exception = Assert.Throws<WorkflowDefinitionValidationException>(() =>
            WorkflowProfileYamlParser.Parse(yaml, "profile", Catalog()));

        Assert.Contains(exception.Errors, error =>
            error.Path == "stages[0].tasks[0]"
            && error.Message.Contains("was removed", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsOptionsOnMohistAgent()
    {
        var exception = Assert.Throws<WorkflowDefinitionValidationException>(() =>
            WorkflowProfileYamlParser.Parse("""
                stages:
                  - stage: build
                    tasks:
                      - id: task
                        uses: mohist/agent
                        with:
                          name: mohist/builder
                          prompt: Build.
                          options: { model: x }
                """, "profile", Catalog()));

        Assert.Contains(exception.Errors, error => error.Path == "stages[0].tasks[0].with.options");
    }

    [Fact]
    public void Parse_RejectsNewCoreScriptResourceProfileUsage()
    {
        var exception = Assert.Throws<WorkflowDefinitionValidationException>(() =>
            WorkflowProfileYamlParser.Parse("""
                stages:
                  - stage: build
                    tasks:
                      - id: script
                        uses: core/script
                        with:
                          run: echo ok
                          resourceProfile: { limits: { cpu: 1 } }
                """, "profile", Catalog()));

        Assert.Contains(exception.Errors, error => error.Path == "stages[0].tasks[0].with.resourceProfile");
    }

    private static ActionCatalog Catalog() => new(
        [new ActionCatalogEntry(
            "core/script",
            [new ActionCatalogInput("run", ["string"], Required: true)],
            [],
            [])],
        []);
}
