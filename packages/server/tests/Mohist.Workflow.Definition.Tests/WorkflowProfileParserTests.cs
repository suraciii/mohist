using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public sealed class WorkflowProfileParserTests
{
    [Fact]
    public void Parse_MaterializesCompleteUsesExpressions()
    {
        var result = WorkflowProfileParser.Parse("""
            id: delivery/review
            name: Delivery Review
            agentAction: mohist/pi
            approval:
              feedback:
                tasks:
                  - id: address-feedback
                    uses: ${{ profile.agentAction }}
            stages:
              - stage: implement
                requiresApproval: true
                tasks:
                  - id: implement
                    uses: ${{ profile.agentAction }}
            """, "fallback");

        Assert.True(result.IsValid, FormatErrors(result.Errors));
        Assert.Equal("delivery/review", result.Profile!.Id);
        Assert.Equal("mohist/pi", result.Profile.AgentAction);
        Assert.Equal("mohist/pi", result.Profile.Definition.Stages[0].Tasks[0].Uses);
        Assert.Equal("mohist/pi", result.Profile.Definition.Approval!.Feedback!.Tasks![0].Uses);
    }

    [Fact]
    public void Parse_RejectsProfileExpressionOutsideCompleteUsesValue()
    {
        var result = WorkflowProfileParser.Parse("""
            agentAction: mohist/pi
            stages:
              - stage: implement
                tasks:
                  - id: implement
                    uses: prefix-${{ profile.agentAction }}
            """, "profile");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Path == "stages[0].tasks[0].uses"
            && error.Message.Contains("complete value of uses", StringComparison.Ordinal));
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
