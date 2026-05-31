using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Recovery;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class WorkflowBacklogRecoverySpecs
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void TryRestoreRunnableWorkflow_PausedWithPendingWork_DoesNotRecover()
    {
        var run = RunnableRun("wf-paused");
        run.Pause();

        var recovered = TryRestoreRunnableWorkflow(JsonSerializer.Serialize(run, JsonOptions), out var hasWork);

        Assert.False(recovered);
        Assert.False(hasWork);
    }

    [Fact]
    public void TryRestoreRunnableWorkflow_RunningWithPendingWork_Recovers()
    {
        var run = RunnableRun("wf-running");

        var recovered = TryRestoreRunnableWorkflow(JsonSerializer.Serialize(run, JsonOptions), out var hasWork);

        Assert.True(recovered);
        Assert.True(hasWork);
    }

    private static WorkflowRun RunnableRun(string id)
    {
        var run = WorkflowRun.Create(id, SingleStage());
        run.Start();
        run.CurrentStage().Initialized = true;
        run.CurrentStage().Tasks.Add(new TaskRun
        {
            Id = "task-1.1",
            DefinitionId = "task-1",
            Attempt = 1,
            Title = "Task 1",
            Uses = "spec/task",
            Status = TaskRunStatus.Pending
        });
        return run;
    }

    private static WorkflowDefinition SingleStage() => new("spec/workflow",
    [
        new StageDefinition("build", [new TaskDefinition("task-1", "Task 1", "spec/task")], [])
    ]);

    private static bool TryRestoreRunnableWorkflow(string jsonState, out bool hasWork)
    {
        object[] args = [jsonState, "test-project", false];
        var result = (bool)typeof(WorkflowBacklogRecoveryService)
            .GetMethod("TryRestoreRunnableWorkflow", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args)!;
        hasWork = (bool)args[2];
        return result;
    }
}
