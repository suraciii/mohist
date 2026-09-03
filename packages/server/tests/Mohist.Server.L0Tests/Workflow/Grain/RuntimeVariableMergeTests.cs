using System.Text.Json;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.Grain;

[Trait("level", "L0")]
public class RuntimeVariableMergeTests
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
        var events2 = run.StartTask("work-1", "runner-1", "test-process-generation", DateTimeOffset.UnixEpoch);
        var task = run.CurrentStage().Tasks.First(t => t.DefinitionId == "proposal");
        task.Status = WorkflowActionAttemptStatus.Completed;
        task.Output = JsonSerializer.Deserialize<JsonElement>("{\"changeName\":\"issue-97\",\"changeDir\":\"artifacts/changes/issue-97\"}");

        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        WorkflowDispatchHelpers.MergeTaskOutputsIntoPayload(payload, run);

        Assert.True(payload.TryGetValue("tasks", out var tasksEl));
        var tasks = tasksEl!.Value;
        Assert.True(tasks.TryGetProperty("proposal", out var proposal));
        Assert.True(proposal.TryGetProperty("outputs", out var outputs));
        Assert.True(outputs.TryGetProperty("changeName", out var changeName));
        Assert.Equal("issue-97", changeName.GetString());
        Assert.True(outputs.TryGetProperty("changeDir", out var changeDir));
        Assert.Equal("artifacts/changes/issue-97", changeDir.GetString());
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
        var events2 = run.StartTask("work-1", "runner-1", "test-process-generation", DateTimeOffset.UnixEpoch);

        var completed = run.CurrentStage().Tasks.First(t => t.DefinitionId == "proposal");
        completed.Status = WorkflowActionAttemptStatus.Completed;
        completed.Output = JsonSerializer.Deserialize<JsonElement>("{\"name\":\"done\"}");

        var pending = run.CurrentStage().Tasks.First(t => t.DefinitionId == "review");
        pending.Status = WorkflowActionAttemptStatus.Pending;

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
        var events2 = run.StartTask("work-1", "runner-1", "test-process-generation", DateTimeOffset.UnixEpoch);
        var task = run.CurrentStage().Tasks.First(t => t.DefinitionId == "proposal");
        task.Status = WorkflowActionAttemptStatus.Completed;
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
