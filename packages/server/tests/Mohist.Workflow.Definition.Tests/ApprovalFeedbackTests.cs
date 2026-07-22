using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class ApprovalFeedbackTests
{
    [Fact]
    public void Parse_ApprovalFeedbackTasks_Parsed()
    {
        var result = WorkflowDefinitionParser.Parse("""
            approval:
              feedback:
                tasks:
                  - id: apply-feedback
                    uses: mohist/opencode
                    with:
                      session: plan
                      prompt: ${{ prompts.apply-feedback }}
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.True(result.IsValid);
        var tasks = result.Definition!.Approval!.Feedback!.Tasks!;
        Assert.Single(tasks);
        Assert.Equal("apply-feedback", tasks[0].Id);
        Assert.Equal("mohist/opencode", tasks[0].Uses);
    }

    [Fact]
    public void Parse_ApprovalFeedbackTask_MissingUses_ReturnsError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            approval:
              feedback:
                tasks:
                  - id: apply
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "approval.feedback.tasks[0].uses");
    }

    [Fact]
    public void Parse_ApprovalNotObject_ReturnsError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            approval: "oops"
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "approval"
            && e.Message == "approval must be an object");
    }

    [Fact]
    public void Parse_ApprovalFeedbackTasksNotList_ReturnsError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            approval:
              feedback:
                tasks: "nope"
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "approval.feedback.tasks");
    }

    [Fact]
    public void Parse_ApprovalFeedbackUnknownKey_ReturnsError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            approval:
              feedback:
                tasks:
                  - id: apply
                    uses: mohist/opencode
                extra: nope
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "approval.feedback.extra");
    }

    [Fact]
    public void Parse_ApprovalUnknownKey_ReturnsError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            approval:
              unexpected: true
              feedback:
                tasks:
                  - id: apply
                    uses: mohist/opencode
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "approval.unexpected");
    }
}
