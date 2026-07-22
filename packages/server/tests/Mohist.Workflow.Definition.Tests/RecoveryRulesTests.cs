using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class RecoveryRulesTests
{
    [Fact]
    public void Parse_BudgetNegativeInteger_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    recovery:
                      budget: -1
                      handlers:
                        - tasks:
                            - id: r1
                              uses: a
                          retrySelf: true
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].tasks[0].recovery.budget");
        Assert.Equal("recovery.budget must be a non-negative integer", error.Message);
    }

    [Fact]
    public void Parse_BudgetNonInteger_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    recovery:
                      budget: abc
                      handlers:
                        - tasks:
                            - id: r1
                              uses: a
                          retrySelf: true
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].recovery.budget");
    }

    [Fact]
    public void Parse_TwoDefaultHandlers_Rejected()
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
                        - tasks:
                            - id: a
                              uses: a
                          retrySelf: true
                        - tasks:
                            - id: b
                              uses: b
                          retrySelf: true
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].recovery.handlers[1]"
            && e.Message.Contains("at most one default handler"));
    }

    [Fact]
    public void Parse_DefaultHandlerBeforeExplicit_Rejected()
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
                        - tasks:
                            - id: a
                              uses: a
                          retrySelf: true
                        - when: error.code=conflict
                          tasks:
                            - id: b
                              uses: b
                          retrySelf: true
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].recovery.handlers[0]"
            && e.Message == "recovery default handler (without 'when') must be last");
    }

    [Fact]
    public void Parse_HandlerWithoutTasksOrRetrySelf_Rejected()
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
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].tasks[0].recovery.handlers[0]");
        Assert.Equal("recovery handler must declare tasks or retrySelf", error.Message);
    }

    [Fact]
    public void Parse_WhenMissingField_Rejected()
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
                        - when: "=missing"
                          tasks:
                            - id: a
                              uses: a
                          retrySelf: true
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].recovery.handlers[0].when");
    }

    [Fact]
    public void Parse_WhenMissingValue_Rejected()
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
                        - when: "error.code="
                          tasks:
                            - id: a
                              uses: a
                          retrySelf: true
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].recovery.handlers[0].when");
    }

    [Fact]
    public void Parse_HandlerWithBothTasksAndRetrySelf_Accepted()
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
                            - id: a
                              uses: a
                          retrySelf: true
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_HandlerWithRetrySelfOnly_Accepted()
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
                          retrySelf: true
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_RecoveryHandlers_OrderedWithWhen_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    recovery:
                      budget: 2
                      handlers:
                        - when: error.code=conflict
                          tasks:
                            - id: a
                              uses: a
                          retrySelf: true
                        - when: error.code=retry-safe
                          retrySelf: true
                        - tasks:
                            - id: b
                              uses: b
                          retrySelf: true
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_EmptyHandlersList_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    recovery:
                      budget: 1
                      handlers: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].tasks[0].recovery.handlers");
        Assert.Equal("recovery.handlers must be non-empty", error.Message);
    }
}
