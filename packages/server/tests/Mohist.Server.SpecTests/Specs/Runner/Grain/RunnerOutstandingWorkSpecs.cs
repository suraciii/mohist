using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerOutstandingWorkSpecs : WorkflowGrainSpecs
{
    public RunnerOutstandingWorkSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunnerLoss_FailsActiveWorkflowTask()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = run.Stages.Single().Tasks.Single();
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Equal("runner-lost", run.Failure?.Message);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
    }

    [Fact]
    public async Task RunnerLoss_WithoutOutstandingWorkflowWork_IsNoOp()
    {
        var runnerId = $"lonely-runner-{Guid.NewGuid():N}";
        await Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            "test-project-no-workflow"));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var runtimeBefore = await runner.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Online, runtimeBefore.Status);
        Assert.Empty(runtimeBefore.ActiveWorks);

        await runner.UnregisterAsync();

        var runtimeAfter = await runner.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Offline, runtimeAfter.Status);
    }

    [Fact]
    public async Task RunnerLoss_FailedTaskKeepsRunnerLostMessage()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(TaskRunStatus.Failed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal(FailureReason.TaskFailed, run.Failure?.Reason);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Theory]
    [InlineData("mohist/opencode")]
    [InlineData("mohist/pi")]
    public async Task RunnerLoss_PreservesAgentResultAndReconnectSettlesTheOriginalAttempt(string uses)
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new("agent", "Agent", uses)],
            checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();
        var initial = await LoadRunAsync(work.WorkflowRunId);
        var originalTask = Assert.Single(initial.CurrentStage().Tasks);
        var binding = new AgentExecutionBinding(
            originalTask.Id,
            work.WorkId,
            runnerId,
            $"session-{uses}",
            $"turn-{uses}",
            uses == "mohist/pi" ? "pi" : "opencode",
            $"runtime-{uses}");
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(binding));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var disconnected = await LoadRunAsync(work.WorkflowRunId);
        var unsettledTask = Assert.Single(disconnected.CurrentStage().Tasks);
        var settlement = Assert.IsType<AgentResultSettlement>(unsettledTask.AgentResultSettlement);
        Assert.Equal(AgentResultSettlementState.Unknown, settlement.State);
        Assert.Equal(AgentExecutionObservationKind.Disconnected, settlement.LastObservation);
        Assert.Equal("runner-disconnected", settlement.ReasonCode);
        Assert.Equal(TaskRunStatus.Running, unsettledTask.Status);
        Assert.Equal(WorkflowRunStatus.Running, disconnected.Status);
        Assert.Null(disconnected.Failure);

        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", TestProjectId(work.WorkflowRunId)));
        var dispatch = Services.GetRequiredService<Mohist.Server.Runner.Services.DispatchService>();
        var redelivery = Assert.Single((await dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        Assert.Equal(work.WorkId, redelivery.WorkId);
        Assert.Equal(work.TaskRunId, redelivery.TaskRunId);
        Assert.Equal(binding.Runtime, redelivery.AgentRecovery?.Runtime);
        Assert.Equal(binding.RuntimeSessionId, redelivery.AgentRecovery?.RuntimeSessionId);

        var report = Services.GetRequiredService<Mohist.Server.Runner.Services.WorkflowReportService>();
        var (ack, _) = await report.ReportAsync(
            runnerId,
            work.WorkflowRunId,
            work.WorkId,
            work.TaskRunId,
            new WorkResult("completed"));
        Assert.Equal("accepted", ack);

        var completed = await LoadRunAsync(work.WorkflowRunId);
        var completedTask = Assert.Single(completed.CurrentStage().Tasks);
        Assert.Equal(originalTask.Id, completedTask.Id);
        Assert.Equal(TaskRunStatus.Completed, completedTask.Status);
        Assert.Equal(WorkflowRunStatus.Completed, completed.Status);
    }

    [Fact]
    public async Task RunnerLoss_WithRunningChecks_FailsEachRunningCheck()
    {
        await StartWorkflowAsync(SingleStage(
            checks: [
                new("typecheck", "TypeCheck", "spec/typecheck"),
                new("lint", "Lint", "spec/lint")
            ]));
        var runnerId = _runnerId!;
        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");
        var (checks, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var run = await LoadRunAsync(checks.WorkflowRunId);
        var stage = run.Stages.Single();
        var typecheck = stage.Checks.Single(c => c.Name == "typecheck");
        var lint = stage.Checks.Single(c => c.Name == "lint");

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(StageCheckStatus.Failed, typecheck.Status);
        Assert.Equal("runner-lost", typecheck.Message);
        Assert.Equal(StageCheckStatus.Failed, lint.Status);
        Assert.Equal("runner-lost", lint.Message);
        Assert.Equal("typecheck", run.Failure?.CheckName);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }
}
