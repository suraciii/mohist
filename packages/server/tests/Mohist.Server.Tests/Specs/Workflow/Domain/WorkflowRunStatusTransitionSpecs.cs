using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Domain;

public class WorkflowRunStatusTransitionSpecs
{
    private const string RunnerId = "runner-1";

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void WorkflowRunStatus_ValuesMatchSingleWaitingObjectVocabulary()
    {
        Assert.Equal(
            [
                nameof(WorkflowRunStatus.Created),
                nameof(WorkflowRunStatus.Pending),
                nameof(WorkflowRunStatus.Ready),
                nameof(WorkflowRunStatus.Running),
                nameof(WorkflowRunStatus.AwaitingApproval),
                nameof(WorkflowRunStatus.Paused),
                nameof(WorkflowRunStatus.Stopped),
                nameof(WorkflowRunStatus.Completed),
                nameof(WorkflowRunStatus.Failed)
            ],
            Enum.GetNames<WorkflowRunStatus>());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Start_MovesCreatedToPending()
    {
        var run = CreateRun();

        var events = run.Start();

        Assert.Equal(WorkflowRunStatus.Pending, run.Status);
        Assert.NotEqual(WorkflowRunStatus.Ready, run.Status);
        Assert.NotEqual(WorkflowRunStatus.Running, run.Status);
        Assert.IsType<WorkflowRunStarted>(WorkflowEventSerializer.Unwrap(events[0]));
        Assert.IsType<StageStarted>(WorkflowEventSerializer.Unwrap(events[1]));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void AssignRunner_MovesPendingToReady_AndStoresRunnerOnAssignment()
    {
        var run = BuildPendingRun();

        run.AssignTo(RunnerId, DateTimeOffset.UtcNow);

        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.Equal(RunnerId, run.Assignment!.RunnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void StartTask_MovesReadyToRunning()
    {
        var run = BuildReadyRun();

        run.StartTask("work-1", RunnerId);

        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.Equal(TaskRunStatus.Running, run.CurrentStage().Tasks.Single().Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void CompleteTask_WithRemainingDispatchableWork_ReturnsAssignedRunToReady()
    {
        var run = BuildReadyRun(tasks:
        [
            new("compile", "Compile", "spec/task"),
            new("test", "Test", "spec/task")
        ], checks: []);
        run.StartTask("work-1", RunnerId);

        run.CompleteTask();

        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.Equal(TaskRunStatus.Pending, run.CurrentStage().Tasks[1].Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void CompleteTask_WithRemainingDispatchableWork_ReturnsUnassignedRunToPending()
    {
        var run = BuildReadyRun(tasks:
        [
            new("compile", "Compile", "spec/task"),
            new("test", "Test", "spec/task")
        ], checks: []);
        run.StartTask("work-1", RunnerId);
        run.Assignment = null;

        run.CompleteTask();

        Assert.Equal(WorkflowRunStatus.Pending, run.Status);
        Assert.Equal(TaskRunStatus.Pending, run.CurrentStage().Tasks[1].Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void CompleteTask_WithNoRemainingWork_CompletesRun()
    {
        var run = BuildReadyRun(checks: []);
        run.StartTask("work-1", RunnerId);

        run.CompleteTask();

        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Pause_AdmitsPendingReadyAndRunningStates()
    {
        var pending = BuildPendingRun();
        var ready = BuildReadyRun();
        var running = BuildReadyRun();
        running.StartTask("work-1", RunnerId);

        pending.Pause();
        ready.Pause();
        running.Pause();

        Assert.Equal(WorkflowRunStatus.Paused, pending.Status);
        Assert.Equal(WorkflowRunStatus.Paused, ready.Status);
        Assert.Equal(WorkflowRunStatus.Paused, running.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Resume_WithInFlightWork_ReturnsToRunning()
    {
        var run = BuildReadyRun();
        run.StartTask("work-1", RunnerId);
        run.Pause();

        run.Resume();

        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Resume_WithDispatchableWorkAndNoInFlightWork_ReturnsToReady()
    {
        var run = BuildReadyRun();
        run.Pause();

        run.Resume();

        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Approve_MovesAwaitingApprovalToReadyForNextStage()
    {
        var run = BuildAwaitingApprovalRun();

        var events = run.Approve();

        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.Equal("build", run.CurrentStageId);
        Assert.Contains(events, e => WorkflowEventSerializer.Unwrap(e) is StageStarted { Stage: "build" });
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void FailTask_LandsOnFailed()
    {
        var run = BuildReadyRun();
        run.StartTask("work-1", RunnerId);

        run.FailTask(new TaskResult("failed", "boom"));

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Stop_LandsOnStopped()
    {
        var run = BuildReadyRun();

        run.Stop();

        Assert.Equal(WorkflowRunStatus.Stopped, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void Reject_LandsOnFailed()
    {
        var run = BuildAwaitingApprovalRun();

        run.Reject("not enough detail");

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ReconcileReadyStatusWithInFlightWork_CorrectsReadyToRunning()
    {
        var run = BuildReadyRun();
        run.StartTask("work-1", RunnerId);
        run.Status = WorkflowRunStatus.Ready;

        var changed = run.ReconcileReadyStatusWithInFlightWork();

        Assert.True(changed);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }

    private static WorkflowRun BuildAwaitingApprovalRun()
    {
        var run = WorkflowRun.Create("wr_approval", new WorkflowDefinition("spec/workflow", [
            new StageDefinition("plan", [new("draft", "Draft", "spec/task")], [new("plan-ok", "Plan OK", "spec/check")], RequiresApproval: true),
            new StageDefinition("build", [new("compile", "Compile", "spec/task")], [])
        ]));
        run.Start();
        run.InitializeStage([new("draft", "Draft", "spec/task")], [new("plan-ok", "Plan OK", "spec/check")]);
        run.AssignTo(RunnerId, DateTimeOffset.UtcNow);
        run.StartTask("work-1", RunnerId);
        run.CompleteTask();
        run.PassCheck(new CheckResult("plan-ok", "pass"));
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);
        return run;
    }

    private static WorkflowRun BuildReadyRun(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null)
    {
        var run = BuildPendingRun(tasks, checks);
        run.AssignTo(RunnerId, DateTimeOffset.UtcNow);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        return run;
    }

    private static WorkflowRun BuildPendingRun(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null)
    {
        var taskList = tasks ?? [new("compile", "Compile", "spec/task")];
        var checkList = checks ?? [new("build-ok", "Build OK", "spec/check")];
        var run = CreateRun(taskList, checkList);
        run.Start();
        run.InitializeStage(taskList, checkList);
        Assert.Equal(WorkflowRunStatus.Pending, run.Status);
        return run;
    }

    private static WorkflowRun CreateRun(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null)
    {
        return WorkflowRun.Create("wr_1", new WorkflowDefinition("spec/workflow", [
            new StageDefinition("build",
                tasks ?? [new("compile", "Compile", "spec/task")],
                checks ?? [new("build-ok", "Build OK", "spec/check")])
        ]));
    }
}
