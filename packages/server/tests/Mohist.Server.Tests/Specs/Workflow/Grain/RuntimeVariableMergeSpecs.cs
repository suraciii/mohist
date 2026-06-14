using System.Text.Json;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class RuntimeVariableMergeSpecs
{
    [Fact]
    public void MergeRuntimeVariablesIntoPayload_IncludesNestedTasksOutputs()
    {
        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["workflow"] = JsonSerializer.SerializeToElement(new { runId = "wr_1" }),
            ["vars"] = JsonSerializer.SerializeToElement(new { agent = "default" })
        };

        var runtimeVariables = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["tasks.proposal.outputs.openspecName"] = JsonSerializer.SerializeToElement("issue-97"),
            ["tasks.proposal.outputs.changeDir"] = JsonSerializer.SerializeToElement("openspec/changes/issue-97")
        };

        var merged = WorkflowGrain.MergeRuntimeVariablesIntoPayload(payload, runtimeVariables);

        Assert.True(merged.TryGetValue("tasks", out var tasksEl));
        var tasks = tasksEl!.Value;
        Assert.True(tasks.TryGetProperty("proposal", out var proposal));
        Assert.True(proposal.TryGetProperty("outputs", out var outputs));
        Assert.True(outputs.TryGetProperty("openspecName", out var openspecName));
        Assert.Equal("issue-97", openspecName.GetString());
        Assert.True(outputs.TryGetProperty("changeDir", out var changeDir));
        Assert.Equal("openspec/changes/issue-97", changeDir.GetString());
    }

    [Fact]
    public void MergeRuntimeVariablesIntoPayload_RuntimeVarsTakePrecedence()
    {
        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["tasks"] = JsonSerializer.SerializeToElement(new
            {
                proposal = new
                {
                    outputs = new
                    {
                        openspecName = "static-value"
                    }
                }
            })
        };

        var runtimeVariables = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["tasks.proposal.outputs.openspecName"] = JsonSerializer.SerializeToElement("runtime-value")
        };

        var merged = WorkflowGrain.MergeRuntimeVariablesIntoPayload(payload, runtimeVariables);

        var value = merged["tasks"]!.Value.GetProperty("proposal").GetProperty("outputs").GetProperty("openspecName");
        Assert.Equal("runtime-value", value.GetString());
    }

    [Fact]
    public void MergeRuntimeVariablesIntoPayload_EmptyRuntimeStore_ReturnsEquivalentPayload()
    {
        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["workflow"] = JsonSerializer.SerializeToElement(new { runId = "wr_1" })
        };

        var merged = WorkflowGrain.MergeRuntimeVariablesIntoPayload(payload, new Dictionary<string, JsonElement>());

        Assert.True(merged.TryGetValue("workflow", out var workflow));
        Assert.Equal("wr_1", workflow!.Value.GetProperty("runId").GetString());
        Assert.False(merged.ContainsKey("tasks"));
    }

    [Fact]
    public void MergeRuntimeVariablesIntoPayload_PreservesOtherPayloadKeys()
    {
        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["workflow"] = JsonSerializer.SerializeToElement(new { runId = "wr_1" }),
            ["stage"] = JsonSerializer.SerializeToElement(new { name = "build" }),
            ["vars"] = JsonSerializer.SerializeToElement(new { agent = "default" })
        };

        var runtimeVariables = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["tasks.proposal.outputs.name"] = JsonSerializer.SerializeToElement("value")
        };

        var merged = WorkflowGrain.MergeRuntimeVariablesIntoPayload(payload, runtimeVariables);

        Assert.True(merged.ContainsKey("workflow"));
        Assert.True(merged.ContainsKey("stage"));
        Assert.True(merged.ContainsKey("vars"));
        Assert.True(merged.ContainsKey("tasks"));
    }
}
