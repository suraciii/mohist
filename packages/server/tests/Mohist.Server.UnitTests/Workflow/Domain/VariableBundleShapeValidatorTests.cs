using System.Text.Json;
using Mohist.Server.Workflow.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public class VariableBundleShapeValidatorTests
{
    [Fact]
    public void RejectsNonObjectWorkflowVariables()
    {
        var bundle = new VariableBundle(JsonSerializer.Deserialize<JsonElement>("[]"));

        var exception = Assert.Throws<ArgumentException>(() =>
            VariableBundleShapeValidator.Validate(bundle));

        Assert.Contains("vars", exception.Message);
    }

    [Fact]
    public void RejectsNonObjectStageVariables()
    {
        var bundle = new VariableBundle(
            Stages: new Dictionary<string, StageVariables>
            {
                ["check"] = new(JsonSerializer.Deserialize<JsonElement>("false")),
            });

        var exception = Assert.Throws<ArgumentException>(() =>
            VariableBundleShapeValidator.Validate(bundle));

        Assert.Contains("stages.check.vars", exception.Message);
    }

    [Fact]
    public void AcceptsObjectWorkflowAndStageVariables()
    {
        var bundle = new VariableBundle(
            Vars: JsonSerializer.Deserialize<JsonElement>("{}"),
            Stages: new Dictionary<string, StageVariables>
            {
                ["check"] = new(JsonSerializer.Deserialize<JsonElement>("{}")),
            });

        VariableBundleShapeValidator.Validate(bundle);
    }
}
