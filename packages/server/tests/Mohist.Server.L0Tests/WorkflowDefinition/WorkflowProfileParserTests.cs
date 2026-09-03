using Xunit;

namespace Mohist.Workflow.Definition.Tests;

[Trait("level", "L0")]
public sealed class WorkflowProfileParserTests
{
    [Fact]
    public void Parse_PreservesConcreteAgentAction()
    {
        var result = WorkflowProfileParser.Parse("""
            id: delivery/review
            name: Delivery Review
            stages:
              - stage: implement
                tasks:
                  - id: implement
                    uses: mohist/agent
                    with:
                      name: mohist/builder
                      prompt: Build the change.
            """, "fallback");

        Assert.True(result.IsValid, FormatErrors(result.Errors));
        Assert.Equal("delivery/review", result.Profile!.Id);
        var task = result.Profile.Definition.Stages[0].Tasks[0];
        Assert.Equal("mohist/agent", task.Uses);
        Assert.Equal("mohist/builder", task.With!["name"]!.Value.GetString());
    }

    [Fact]
    public void Parse_RejectsRemovedProfileAgentActionMetadata()
    {
        var result = WorkflowProfileParser.Parse("""
            agentAction: mohist/pi
            stages:
              - stage: implement
            """, "profile");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Path == "agentAction");
    }

    [Fact]
    public void Parse_AllowsApprovalStageWithoutAgentFeedbackTasks()
    {
        var result = WorkflowProfileParser.Parse("""
            stages:
              - stage: check
                requiresApproval: true
              - stage: integrate
                tasks:
                  - id: finish
                    uses: spec/noop
            """, "profile");

        Assert.True(result.IsValid, FormatErrors(result.Errors));
        Assert.Null(result.Profile!.Definition.Approval);
        Assert.True(result.Profile.Definition.Stages[0].RequiresApproval);
    }

    private static string FormatErrors(IReadOnlyList<ValidationError> errors) =>
        string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}"));
}
