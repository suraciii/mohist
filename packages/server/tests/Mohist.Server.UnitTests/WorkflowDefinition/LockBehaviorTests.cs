using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class LockBehaviorTests
{
    [Fact]
    public void Parse_LockBehaviorSequentialWithResources_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: integrate
                lockBehavior: sequential
                resources:
                  - r1
                  - r2
                tasks: []
                checks: []
            """);

        Assert.True(result.IsValid);
        var stage = result.Definition!.Stages[0];
        Assert.Equal("sequential", stage.LockBehavior);
        Assert.Equal(new[] { "r1", "r2" }, stage.Resources);
    }

    [Fact]
    public void Parse_LockBehaviorWithoutResources_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: integrate
                lockBehavior: sequential
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].lockBehavior");
        Assert.Equal("lockBehavior requires non-empty resources", error.Message);
    }

    [Fact]
    public void Parse_ResourcesWithoutLockBehavior_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: integrate
                resources:
                  - r1
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].resources");
        Assert.Equal("resources require lockBehavior", error.Message);
    }

    [Fact]
    public void Parse_NonSequentialLockBehavior_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: integrate
                lockBehavior: parallel
                resources:
                  - r1
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].lockBehavior");
        Assert.Equal("lockBehavior must be 'sequential'", error.Message);
    }

    [Fact]
    public void Parse_EmptyResourcesListWithLockBehavior_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: integrate
                lockBehavior: sequential
                resources: []
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].lockBehavior"
            && e.Message == "lockBehavior requires non-empty resources");
    }
}
