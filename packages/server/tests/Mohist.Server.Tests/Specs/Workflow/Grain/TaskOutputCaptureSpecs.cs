using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class TaskOutputCaptureSpecs
{
    [Fact]
    public void CaptureTaskOutputs_StoresDeclaredOutputsWithTasksIdOutputsNameKey()
    {
        var run = WorkflowRun.Create("wr_1", SingleStageWithOutputs());
        var task = MakeTaskRun("proposal.1", "proposal", outputs:
        [
            new TaskOutputDefinition("openspecName", "output.openspecName"),
            new TaskOutputDefinition("changeDir", "output.changeDir")
        ]);
        var captured = new Dictionary<string, JsonElement>
        {
            ["openspecName"] = JsonSerializer.SerializeToElement("issue-97"),
            ["changeDir"] = JsonSerializer.SerializeToElement("openspec/changes/issue-97")
        };

        WorkflowGrain.CaptureTaskOutputs(run, task, captured);

        Assert.Equal(2, run.RuntimeVariables.Count);
        Assert.Equal("issue-97", run.RuntimeVariables["tasks.proposal.outputs.openspecName"].GetString());
        Assert.Equal("openspec/changes/issue-97", run.RuntimeVariables["tasks.proposal.outputs.changeDir"].GetString());
    }

    [Fact]
    public void CaptureTaskOutputs_IgnoresUndeclaredCapturedOutputs()
    {
        var run = WorkflowRun.Create("wr_1", SingleStageWithOutputs());
        var task = MakeTaskRun("proposal.1", "proposal", outputs:
        [
            new TaskOutputDefinition("openspecName", "output.openspecName")
        ]);
        var captured = new Dictionary<string, JsonElement>
        {
            ["openspecName"] = JsonSerializer.SerializeToElement("issue-97"),
            ["undeclared"] = JsonSerializer.SerializeToElement("ignored")
        };

        WorkflowGrain.CaptureTaskOutputs(run, task, captured);

        Assert.Single(run.RuntimeVariables);
        Assert.Equal("issue-97", run.RuntimeVariables["tasks.proposal.outputs.openspecName"].GetString());
        Assert.False(run.RuntimeVariables.ContainsKey("tasks.proposal.outputs.undeclared"));
    }

    [Fact]
    public void CaptureTaskOutputs_NullTask_DoesNotModifyStore()
    {
        var run = WorkflowRun.Create("wr_1", SingleStageWithOutputs());
        var captured = new Dictionary<string, JsonElement>
        {
            ["openspecName"] = JsonSerializer.SerializeToElement("issue-97")
        };

        WorkflowGrain.CaptureTaskOutputs(run, null, captured);

        Assert.Empty(run.RuntimeVariables);
    }

    [Fact]
    public void CaptureTaskOutputs_TaskWithoutOutputs_DoesNotModifyStore()
    {
        var run = WorkflowRun.Create("wr_1", SingleStageWithOutputs());
        var task = MakeTaskRun("proposal.1", "proposal", outputs: null);
        var captured = new Dictionary<string, JsonElement>
        {
            ["openspecName"] = JsonSerializer.SerializeToElement("issue-97")
        };

        WorkflowGrain.CaptureTaskOutputs(run, task, captured);

        Assert.Empty(run.RuntimeVariables);
    }

    [Fact]
    public void CaptureTaskOutputs_NullOrEmptyCapturedOutputs_DoesNotModifyStore()
    {
        var run = WorkflowRun.Create("wr_1", SingleStageWithOutputs());
        var task = MakeTaskRun("proposal.1", "proposal", outputs:
        [
            new TaskOutputDefinition("openspecName", "output.openspecName")
        ]);

        WorkflowGrain.CaptureTaskOutputs(run, task, null);
        WorkflowGrain.CaptureTaskOutputs(run, task, []);

        Assert.Empty(run.RuntimeVariables);
    }

    private static WorkflowDefinition SingleStageWithOutputs()
    {
        return new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
                [new("proposal", "Generate proposal", "spec/task")],
                [new("check-1", "Check 1", "spec/check")])
        ]);
    }

    private static TaskRun MakeTaskRun(string id, string definitionId, List<TaskOutputDefinition>? outputs)
    {
        return new TaskRun
        {
            Id = id,
            DefinitionId = definitionId,
            Attempt = 1,
            Title = "Task",
            Outputs = outputs,
            Status = TaskRunStatus.Running
        };
    }
}
