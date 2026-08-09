using System.Text.Json;
using Mohist.Server.Workflow.Domain;
using Xunit;

namespace Mohist.Server.UnitTests.Foundation;

public class VariableBundleStageResolutionTests
{
    [Fact]
    public void ResolveStageVars_StageVariantOverridesTopLevelAndInheritsModel()
    {
        var effective = VariableBundle.MergeAll(
            new VariableBundle(
                Vars: JsonSerializer.SerializeToElement(new
                {
                    agent = new { model = "old-project-model", variant = "old-project-variant" },
                })),
            new VariableBundle(
                Vars: JsonSerializer.SerializeToElement(new
                {
                    agent = new { model = "old-issue-model" },
                }),
                Stages: new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase)
                {
                    ["build"] = new(JsonSerializer.SerializeToElement(new
                    {
                        agent = new { variant = "stage-variant" },
                    })),
                }));

        var result = effective.ResolveStageVars("build");
        Assert.NotNull(result);
        var agent = result!.Value.GetProperty("agent");

        Assert.Equal("old-issue-model", agent.GetProperty("model").GetString());
        Assert.Equal("stage-variant", agent.GetProperty("variant").GetString());
    }
}
