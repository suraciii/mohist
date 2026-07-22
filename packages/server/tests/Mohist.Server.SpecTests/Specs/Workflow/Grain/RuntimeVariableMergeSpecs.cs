using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public class RuntimeVariableMergeSpecs
{
    [Fact]
    public void MergeTaskOutputsIntoPayload_IncludesNestedTasksOutputs()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        var events = run.InitializeStage(
            [new("proposal", "Proposal", "spec/propose")],
            [new("check-1", "Check 1", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.AssignTo("runner-1", TestTime.UtcNow);
        var events2 = run.StartTask("work-1", "runner-1", DateTimeOffset.UnixEpoch);
        var task = run.CurrentStage().Tasks.First(t => t.DefinitionId == "proposal");
        task.Status = TaskRunStatus.Completed;
        task.Output = JsonSerializer.Deserialize<JsonElement>("{\"openspecName\":\"issue-97\",\"changeDir\":\"openspec/changes/issue-97\"}");

        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        WorkflowDispatchHelpers.MergeTaskOutputsIntoPayload(payload, run);

        Assert.True(payload.TryGetValue("tasks", out var tasksEl));
        var tasks = tasksEl!.Value;
        Assert.True(tasks.TryGetProperty("proposal", out var proposal));
        Assert.True(proposal.TryGetProperty("outputs", out var outputs));
        Assert.True(outputs.TryGetProperty("openspecName", out var openspecName));
        Assert.Equal("issue-97", openspecName.GetString());
        Assert.True(outputs.TryGetProperty("changeDir", out var changeDir));
        Assert.Equal("openspec/changes/issue-97", changeDir.GetString());
    }

    [Fact]
    public void MergeTaskOutputsIntoPayload_OnlyIncludesCompletedTasks()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        var events = run.InitializeStage(
            [new("proposal", "Proposal", "spec/propose"), new("review", "Review", "spec/review")],
            [new("check-1", "Check 1", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.AssignTo("runner-1", TestTime.UtcNow);
        var events2 = run.StartTask("work-1", "runner-1", DateTimeOffset.UnixEpoch);

        var completed = run.CurrentStage().Tasks.First(t => t.DefinitionId == "proposal");
        completed.Status = TaskRunStatus.Completed;
        completed.Output = JsonSerializer.Deserialize<JsonElement>("{\"name\":\"done\"}");

        var pending = run.CurrentStage().Tasks.First(t => t.DefinitionId == "review");
        pending.Status = TaskRunStatus.Pending;

        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        WorkflowDispatchHelpers.MergeTaskOutputsIntoPayload(payload, run);

        Assert.True(payload.TryGetValue("tasks", out var tasksEl));
        var tasks = tasksEl!.Value;
        Assert.True(tasks.TryGetProperty("proposal", out _));
        Assert.False(tasks.TryGetProperty("review", out _));
    }

    [Fact]
    public void MergeTaskOutputsIntoPayload_SkipsNonObjectOutput()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        var events = run.InitializeStage(
            [new("proposal", "Proposal", "spec/propose")],
            [new("check-1", "Check 1", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.AssignTo("runner-1", TestTime.UtcNow);
        var events2 = run.StartTask("work-1", "runner-1", DateTimeOffset.UnixEpoch);
        var task = run.CurrentStage().Tasks.First(t => t.DefinitionId == "proposal");
        task.Status = TaskRunStatus.Completed;
        task.Output = JsonSerializer.Deserialize<JsonElement>("\"plain string\"");

        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        WorkflowDispatchHelpers.MergeTaskOutputsIntoPayload(payload, run);

        Assert.False(payload.ContainsKey("tasks"));
    }

    [Fact]
    public void MergeTaskOutputsIntoPayload_EmptyHistory_NoTasksKey()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage(), DateTimeOffset.UnixEpoch);

        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["workflow"] = JsonSerializer.SerializeToElement(new { runId = "wr_1" })
        };
        WorkflowDispatchHelpers.MergeTaskOutputsIntoPayload(payload, run);

        Assert.True(payload.TryGetValue("workflow", out _));
        Assert.False(payload.ContainsKey("tasks"));
    }

    private static WorkflowDefinition SingleStage()
    {
        return new WorkflowDefinition(
        [
            new StageDefinition("build",
                [new("proposal", "Proposal", "spec/propose")],
                [new("check-1", "Check 1", "spec/check")])
        ]);
    }
}
