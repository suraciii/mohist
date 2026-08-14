using Mohist.Server.Workflow.Services;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Services;

public sealed class WorkflowProfileYamlParserTests
{
    [Fact]
    public void Parse_MaterializesAgentActionBeforeDefinitionParsing()
    {
        var profile = WorkflowProfileYamlParser.Parse(BoundProfileYaml, "fallback", agentActionOverride: "mohist/pi");

        Assert.Equal("mohist/pi", profile.AgentAction);
        Assert.Equal("mohist/pi", profile.Definition.Stages[0].Tasks[0].Uses);
        Assert.Equal("mohist/pi", profile.Definition.Approval!.Feedback!.Tasks![0].Uses);
    }

    [Fact]
    public void Parse_RejectsProfileExpressionOutsideCompleteUsesValue()
    {
        var exception = Assert.Throws<WorkflowDefinitionValidationException>(() =>
            WorkflowProfileYamlParser.Parse("""
                agentAction: mohist/opencode
                stages:
                  - stage: build
                    tasks:
                      - id: task
                        uses: core/script
                        with:
                          run: "${{ profile.agentAction }} --version"
                """, "profile"));

        Assert.Contains(exception.Errors, error =>
            error.Path == "stages[0].tasks[0].with.run"
            && error.Message.Contains("complete value of uses", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsReferenceWithoutAgentAction()
    {
        var exception = Assert.Throws<WorkflowDefinitionValidationException>(() =>
            WorkflowProfileYamlParser.Parse("""
                stages:
                  - stage: build
                    tasks:
                      - id: task
                        uses: ${{ profile.agentAction }}
                """, "profile"));

        Assert.Contains(exception.Errors, error =>
            error.Path == "stages[0].tasks[0].uses"
            && error.Message.Contains("requires a non-empty agentAction", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsUnusedAgentAction()
    {
        var exception = Assert.Throws<WorkflowDefinitionValidationException>(() =>
            WorkflowProfileYamlParser.Parse("""
                agentAction: mohist/opencode
                stages:
                  - stage: build
                    tasks:
                      - id: task
                        uses: core/script
                """, "profile"));

        Assert.Contains(exception.Errors, error => error.Path == "agentAction");
    }

    [Fact]
    public void Parse_RequiresSelectedActionToDeclareAgentTurn()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/opencode", [], [], [], Capabilities: [])],
            []);

        var exception = Assert.Throws<WorkflowDefinitionValidationException>(() =>
            WorkflowProfileYamlParser.Parse(BoundProfileYaml, "profile", catalog));

        Assert.Contains(exception.Errors, error =>
            error.Path == "agentAction"
            && error.Message.Contains("agent-turn", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsLiteralAgentActionMixedWithBinding()
    {
        var catalog = new ActionCatalog(
            [
                new ActionCatalogEntry("mohist/opencode", [], [], [], Capabilities: ["agent-turn"]),
                new ActionCatalogEntry("mohist/pi", [], [], [], Capabilities: ["agent-turn"]),
            ],
            []);
        var yaml = """
            agentAction: mohist/opencode
            stages:
              - stage: build
                tasks:
                  - id: selected
                    uses: ${{ profile.agentAction }}
                  - id: literal
                    uses: mohist/pi
            """;

        var exception = Assert.Throws<WorkflowDefinitionValidationException>(() =>
            WorkflowProfileYamlParser.Parse(yaml, "profile", catalog));

        Assert.Contains(exception.Errors, error =>
            error.Path == "stages[0].tasks[1].uses"
            && error.Message.Contains("cannot be mixed", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsOverrideForProfileWithoutBinding()
    {
        var exception = Assert.Throws<WorkflowDefinitionValidationException>(() =>
            WorkflowProfileYamlParser.Parse("""
                stages:
                  - stage: build
                """, "profile", agentActionOverride: "mohist/pi"));

        Assert.Contains(exception.Errors, error => error.Path == "agentAction");
    }

    private const string BoundProfileYaml = """
        agentAction: mohist/opencode
        approval:
          feedback:
            tasks:
              - id: feedback
                uses: ${{ profile.agentAction }}
        stages:
          - stage: build
            requiresApproval: true
            tasks:
              - id: task
                uses: ${{ profile.agentAction }}
        """;
}
