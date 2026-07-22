using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class TemplateStaticValidationTests
{
    [Theory]
    [InlineData("workflow")]
    [InlineData("stage")]
    [InlineData("work")]
    [InlineData("issue")]
    [InlineData("repository")]
    [InlineData("workspace")]
    [InlineData("vars")]
    [InlineData("tasks")]
    [InlineData("prompts")]
    [InlineData("failure")]
    public void TemplateRoots_AllTenRoots_AreAllowed(string root)
    {
        Assert.True(TemplateRoots.IsAllowed(root));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("random")]
    [InlineData("env")]
    [InlineData("secrets")]
    public void TemplateRoots_OffTableRoots_AreNotAllowed(string root)
    {
        Assert.False(TemplateRoots.IsAllowed(root));
    }

    [Fact]
    public void TemplateRoots_AllCountIsTen()
    {
        Assert.Equal(10, TemplateRoots.All.Count);
    }

    [Fact]
    public void TemplateRoots_NoDuplicates()
    {
        Assert.Equal(10, TemplateRoots.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Parse_OffTableRoot_ReportedWithLocation()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      prompt: ${{ project.id }}
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e =>
            e.Path == "stages[0].tasks[0].with.prompt");
        Assert.Contains("project", error.Message);
        Assert.Contains("not in the public table", error.Message);
    }

    [Fact]
    public void Parse_OffTableRootInsideDeeplyNestedWithValue_Reported()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      deep:
                        nested:
                          prompt: ${{ secrets.token }}
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e =>
            e.Path == "stages[0].tasks[0].with.deep.nested.prompt");
        Assert.Contains("secrets", error.Message);
    }

    [Fact]
    public void Parse_AllPublicRoots_AcceptedAsOrdinaryExpression()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      a: ${{ workflow.id }}
                      b: ${{ stage.name }}
                      c: ${{ work.status }}
                      d: ${{ issue.number }}
                      e: ${{ repository.name }}
                      f: ${{ workspace.path }}
                      g: ${{ vars.foo }}
                      i: ${{ prompts.apply }}
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_TasksReferenceToUndeclaredTask_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      prompt: ${{ tasks.ghost.outputs.x }}
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e =>
            e.Path == "stages[0].tasks[0].with.prompt");
        Assert.Contains("undeclared task", error.Message);
        Assert.Contains("ghost", error.Message);
    }

    [Fact]
    public void Parse_SelfTaskReference_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: build
                    uses: mohist/opencode
                    with:
                      prompt: ${{ tasks.build.outputs.x }}
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e =>
            e.Path == "stages[0].tasks[0].with.prompt");
        Assert.Contains("self", error.Message);
    }

    [Fact]
    public void Parse_ForwardTaskReference_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: build
                    uses: mohist/opencode
                    with:
                      prompt: ${{ tasks.deploy.outputs.x }}
                  - id: deploy
                    uses: mohist/opencode
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e =>
            e.Path == "stages[0].tasks[0].with.prompt");
        Assert.Contains("forward", error.Message);
        Assert.Contains("deploy", error.Message);
    }

    [Fact]
    public void Parse_CrossStageEarlierReference_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: prepare
                    uses: mohist/opencode
                checks: []
              - stage: deploy
                tasks:
                  - id: ship
                    uses: mohist/opencode
                    with:
                      prompt: ${{ tasks.prepare.outputs.artifact }}
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_SameStageEarlierReference_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: prepare
                    uses: mohist/opencode
                  - id: assemble
                    uses: mohist/opencode
                    with:
                      prompt: ${{ tasks.prepare.outputs.artifact }}
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_TasksOutputsReference_SelfStillRejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: build
                    uses: mohist/opencode
                    with:
                      prompt: ${{ tasks.build.outputs.x }}
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e =>
            e.Path == "stages[0].tasks[0].with.prompt");
        Assert.Contains("self", error.Message);
    }

    [Fact]
    public void Parse_TasksReferenceInExpectString_Validated()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    expect:
                      markers:
                        - path: ${{ tasks.deploy.outputs.path }}
                          oneOf: [pass, fail]
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].expect.markers[0].path"
            && e.Message.Contains("undeclared"));
    }

    [Fact]
    public void Parse_FailureInOrdinaryTask_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      prompt: ${{ failure.error }}
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e =>
            e.Path == "stages[0].tasks[0].with.prompt");
        Assert.Contains("failure", error.Message);
        Assert.Contains("recovery-handler", error.Message);
    }

    [Fact]
    public void Parse_FailureInRecoveryHandlerTask_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    recovery:
                      budget: 1
                      handlers:
                        - when: error.code=conflict
                          tasks:
                            - id: recover
                              uses: mohist/opencode
                              with:
                                prompt: ${{ failure.error }}
                          retrySelf: true
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_FailureInCheck_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks:
                  - id: lint
                    uses: mohist/opencode
                    with:
                      prompt: ${{ failure.error }}
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e =>
            e.Path == "stages[0].checks[0].with.prompt");
        Assert.Contains("failure", error.Message);
    }

    [Fact]
    public void Parse_ApprovalFeedbackInOrdinaryTask_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      prompt: ${{ work.approvalFeedback.summary }}
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e =>
            e.Path == "stages[0].tasks[0].with.prompt");
        Assert.Contains("approvalFeedback", error.Message);
        Assert.Contains("approval-feedback", error.Message);
    }

    [Fact]
    public void Parse_ApprovalFeedbackInRecoveryHandlerTask_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    recovery:
                      budget: 1
                      handlers:
                        - when: error.code=conflict
                          tasks:
                            - id: recover
                              uses: mohist/opencode
                              with:
                                prompt: ${{ work.approvalFeedback.summary }}
                          retrySelf: true
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].recovery.handlers[0].tasks[0].with.prompt"
            && e.Message.Contains("approvalFeedback"));
    }

    [Fact]
    public void Parse_ApprovalFeedbackInApprovalFeedbackTask_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            approval:
              feedback:
                tasks:
                  - id: apply
                    uses: mohist/opencode
                    with:
                      prompt: ${{ work.approvalFeedback.summary }}
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_WorkNonApprovalFeedbackReference_OrdinaryTask_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      prompt: ${{ work.status }}
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_TemplateExpressionInsideWithValue_Validated()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      unknownKey: ${{ bogus.root }}
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e =>
            e.Path == "stages[0].tasks[0].with.unknownKey");
        Assert.Contains("bogus", error.Message);
    }

    [Fact]
    public void Parse_UnknownWithKey_StillNotAnErrorWhenExpressionIsValid()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      anything: ${{ workflow.id }}
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_TemplateErrorsAndStructuralErrors_ReportedTogether()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    extra: nope
                    with:
                      prompt: ${{ project.id }}
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].extra");
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].with.prompt");
    }

    [Fact]
    public void Parse_TemplateErrorsCarryYamlPaths_SortedWithStructuralErrors()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      a: ${{ off.table }}
                      z: ${{ also.off }}
                checks: []
            """);

        Assert.False(result.IsValid);
        var paths = result.Errors.Select(e => e.Path).ToArray();
        Assert.Equal(paths, paths.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Parse_RecoveryHandlerTaskCanReferenceOwningTask_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: build
                    uses: mohist/opencode
                    recovery:
                      budget: 1
                      handlers:
                        - when: error.code=conflict
                          tasks:
                            - id: recover
                              uses: mohist/opencode
                              with:
                                prompt: ${{ tasks.build.outputs.artifact }}
                          retrySelf: true
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_RecoveryHandlerTaskSelfReference_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: build
                    uses: mohist/opencode
                    recovery:
                      budget: 1
                      handlers:
                        - when: error.code=conflict
                          tasks:
                            - id: recover
                              uses: mohist/opencode
                              with:
                                prompt: ${{ tasks.recover.outputs.x }}
                          retrySelf: true
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Message.Contains("self"));
    }

    [Fact]
    public void Parse_RecoveryHandlerForwardReference_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: build
                    uses: mohist/opencode
                    recovery:
                      budget: 1
                      handlers:
                        - when: error.code=conflict
                          tasks:
                            - id: recover
                              uses: mohist/opencode
                              with:
                                prompt: ${{ tasks.late.outputs.x }}
                          retrySelf: true
                  - id: late
                    uses: mohist/opencode
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Message.Contains("forward"));
    }

    [Fact]
    public void Parse_ApprovalFeedbackTaskCanReferenceEarlierStageTask_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: prepare
                    uses: mohist/opencode
                checks: []
            approval:
              feedback:
                tasks:
                  - id: apply
                    uses: mohist/opencode
                    with:
                      prompt: ${{ tasks.prepare.outputs.summary }}
                      fb: ${{ work.approvalFeedback.summary }}
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_ApprovalFeedbackTaskForwardReference_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: prepare
                    uses: mohist/opencode
                checks: []
            approval:
              feedback:
                tasks:
                  - id: apply
                    uses: mohist/opencode
                    with:
                      prompt: ${{ tasks.fix.outputs.x }}
                  - id: fix
                    uses: mohist/opencode
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Message.Contains("forward"));
    }

    [Fact]
    public void Parse_ApprovalFeedbackRootWithoutApprovalFeedbackSubpath_OrdinaryTask_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      prompt: ${{ work.status }}
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ModelLevelEnforcesSameTemplateRules()
    {
        var parseResult = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      prompt: ${{ bogus.x }}
                checks: []
            """);

        Assert.False(parseResult.IsValid);
        Assert.NotNull(parseResult.Definition);

        var modelErrors = WorkflowDefinitionValidator.Validate(parseResult.Definition!);

        Assert.Contains(modelErrors, e =>
            e.Path == "stages[0].tasks[0].with.prompt"
            && e.Message.Contains("bogus"));
    }

    [Fact]
    public void Validate_ModelLevelEnforcesForwardReference()
    {
        var parseResult = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: first
                    uses: mohist/opencode
                    with:
                      prompt: ${{ tasks.second.outputs.x }}
                  - id: second
                    uses: mohist/opencode
                checks: []
            """);

        Assert.NotNull(parseResult.Definition);

        var modelErrors = WorkflowDefinitionValidator.Validate(parseResult.Definition!);

        Assert.Contains(modelErrors, e =>
            e.Path == "stages[0].tasks[0].with.prompt"
            && e.Message.Contains("forward"));
    }

    [Fact]
    public void Parse_MultipleTemplateErrorsOnSameTask_AllReported()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      a: ${{ bogus.x }}
                      b: ${{ alsobad.y }}
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].with.a"
            && e.Message.Contains("bogus"));
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].with.b"
            && e.Message.Contains("alsobad"));
    }

    [Fact]
    public void Parse_EmptyTemplateExpression_NotValidating()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      prompt: ${{  }}
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_TaskIdWithTaskDeclaration_ForwardSafeOutputsReference_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: build
                    uses: mohist/opencode
                  - id: ship
                    uses: mohist/opencode
                    with:
                      prompt: ${{ tasks.build.outputs.x }}
                checks: []
            """);

        Assert.True(result.IsValid);
    }
}