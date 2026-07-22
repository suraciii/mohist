using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class MultiErrorCollectionTests
{
    [Fact]
    public void Parse_CollectsMultipleErrorsInOnePass()
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
                            - id: same
                              uses: a
                            - id: same
                              uses: b
                          retrySelf: true
                checks: []
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 3,
            $"expected multiple errors, got {result.Errors.Count}: {string.Join(" | ", result.Errors.Select(e => e.Path + ": " + e.Message))}");

        Assert.Contains(result.Errors, e =>
            e.Path == "stages[1].stage");
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].recovery.budget");
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].recovery.handlers[0].tasks[1].id");
    }

    [Fact]
    public void Parse_DoesNotThrowOnFirstError_ContinuesValidating()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    extra: oops
                  - id: t2
                    dependsOn: t1
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "stages[0].tasks[0].extra");
        Assert.Contains(result.Errors, e => e.Path == "stages[0].tasks[1].dependsOn");
    }

    [Fact]
    public void Parse_RecoversFromUnrecoverableSubtree()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                  - "garbage"
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[1]");
    }

    [Fact]
    public void Parse_CollectsErrorsAcrossStages()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    extra: nope
                checks: []
              - stage: check
                tasks:
                  - id: t2
                    dependsOn: t1
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 2);
        Assert.Contains(result.Errors, e => e.Path == "stages[0].tasks[0].extra");
        Assert.Contains(result.Errors, e => e.Path == "stages[1].tasks[0].dependsOn");
    }
}
