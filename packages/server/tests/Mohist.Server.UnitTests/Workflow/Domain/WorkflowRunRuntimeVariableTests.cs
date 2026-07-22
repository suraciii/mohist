using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public class WorkflowRunRuntimeVariableTests
{
    [Fact]
    public void TaskRun_Output_StoresAsJsonElement()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        var events = run.InitializeStage(
            [new("task-1", "Task 1", "spec/task")],
            [new("check-1", "Check 1", "spec/check")],
            DateTimeOffset.UnixEpoch);
        var task = run.CurrentStage().Tasks.First(t => t.DefinitionId == "task-1");
        var outputJson = JsonSerializer.Deserialize<JsonElement>("{\"prNumber\":42,\"prUrl\":\"https://github.com/test/pr/42\"}");

        task.Output = outputJson;

        Assert.True(task.Output.HasValue);
        var outVal = task.Output.Value;
        Assert.Equal(JsonValueKind.Object, outVal.ValueKind);
        Assert.Equal(42, outVal.GetProperty("prNumber").GetInt32());
    }

    [Fact]
    public void TaskRun_Output_NullByDefault()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage(), DateTimeOffset.UnixEpoch);

        var task = new TaskRun
        {
            Id = "task-1.1",
            DefinitionId = "task-1",
            Attempt = 1,
            Title = "Task 1",
        };

        Assert.False(task.Output.HasValue);
        Assert.Equal(default, task.Output);
    }

    [Fact]
    public void TaskRun_Output_RetryOverwrites()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        var events = run.InitializeStage(
            [new("task-1", "Task 1", "spec/task")],
            [new("check-1", "Check 1", "spec/check")],
            DateTimeOffset.UnixEpoch);
        var task = run.CurrentStage().Tasks.First(t => t.DefinitionId == "task-1");

        var first = JsonSerializer.Deserialize<JsonElement>("{\"name\":\"first\"}");
        var second = JsonSerializer.Deserialize<JsonElement>("{\"name\":\"second\"}");

        task.Output = first;
        Assert.Equal("first", task.Output!.Value.GetProperty("name").GetString());

        task.Output = second;
        Assert.Equal("second", task.Output!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public void Serialization_RoundTripsTaskOutput()
    {
        var run = WorkflowRun.Create("wr_1", SingleStage(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        var events = run.InitializeStage(
            [new("task-1", "Task 1", "spec/task")],
            [new("check-1", "Check 1", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.AssignTo("worker-1", TestTime.UtcNow);
        var events2 = run.StartTask("work-1", "worker-1", DateTimeOffset.UnixEpoch);
        var task = run.CurrentStage().Tasks.First(t => t.DefinitionId == "task-1");
        task.Output = JsonSerializer.Deserialize<JsonElement>("{\"prNumber\":42}");

        var json = JSON.Serialize(run);
        var roundTripped = JSON.Deserialize<WorkflowRun>(json)!;

        var roundTask = roundTripped.Stages[0].Tasks.First(t => t.DefinitionId == "task-1");
        Assert.True(roundTask.Output.HasValue);
        Assert.Equal(42, roundTask.Output!.Value.GetProperty("prNumber").GetInt32());
    }

    private static WorkflowDefinition SingleStage()
    {
        return new WorkflowDefinition(
        [
            new StageDefinition("build",
                [new("task-1", "Task 1", "spec/task")],
                [new("check-1", "Check 1", "spec/check")])
        ]);
    }
}
