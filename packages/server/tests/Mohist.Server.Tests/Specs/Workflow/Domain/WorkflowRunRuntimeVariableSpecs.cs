using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Domain;

public class WorkflowRunRuntimeVariableSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Create_InitializesEmptyRuntimeVariableStore()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage());

        Assert.NotNull(run.RuntimeVariables);
        Assert.Empty(run.RuntimeVariables);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void CaptureTaskOutputs_AppendsUnderTasksIdOutputsName()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage());
        var outputs = new Dictionary<string, JsonElement>
        {
            ["openspecName"] = JsonSerializer.Deserialize<JsonElement>("\"issue-97\""),
            ["changeDir"] = JsonSerializer.Deserialize<JsonElement>("\"openspec/changes/issue-97\"")
        };

        run.CaptureTaskOutputs("proposal", outputs);

        Assert.Equal(2, run.RuntimeVariables.Count);
        Assert.Equal("issue-97", run.RuntimeVariables["tasks.proposal.outputs.openspecName"].GetString());
        Assert.Equal("openspec/changes/issue-97", run.RuntimeVariables["tasks.proposal.outputs.changeDir"].GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void CaptureTaskOutputs_NullOrEmpty_DoesNotModifyStore()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage());

        run.CaptureTaskOutputs("proposal", null);
        run.CaptureTaskOutputs("proposal", new Dictionary<string, JsonElement>());

        Assert.Empty(run.RuntimeVariables);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void CaptureTaskOutputs_RetryOverwritesSameOutput()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage());
        run.CaptureTaskOutputs("proposal", new Dictionary<string, JsonElement>
        {
            ["openspecName"] = JsonSerializer.Deserialize<JsonElement>("\"first\"")
        });

        run.CaptureTaskOutputs("proposal", new Dictionary<string, JsonElement>
        {
            ["openspecName"] = JsonSerializer.Deserialize<JsonElement>("\"second\"")
        });

        Assert.Single(run.RuntimeVariables);
        Assert.Equal("second", run.RuntimeVariables["tasks.proposal.outputs.openspecName"].GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Serialization_RoundTripsRuntimeVariables()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage());
        run.CaptureTaskOutputs("proposal", new Dictionary<string, JsonElement>
        {
            ["openspecName"] = JsonSerializer.Deserialize<JsonElement>("\"issue-97\"")
        });

        var json = JSON.Serialize(run);
        var roundTripped = JSON.Deserialize<WorkflowRun>(json)!;

        Assert.Single(roundTripped.RuntimeVariables);
        Assert.Equal("issue-97", roundTripped.RuntimeVariables["tasks.proposal.outputs.openspecName"].GetString());
    }

    private static WorkflowDefinition SingleStage()
    {
        return new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
                [new("task-1", "Task 1", "spec/task")],
                [new("check-1", "Check 1", "spec/check")])
        ]);
    }
}
