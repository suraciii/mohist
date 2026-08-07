using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowExecution")]
public class PausingWorkSpecs : WorkflowGrainSpecs
{
    public PausingWorkSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunningWorkflow_Pause_StopsAfterCurrentTask()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new("task-1", "Task 1", "spec/task"),
                new("task-2", "Task 2", "spec/task")
            ],
            checks:
            [
                new("check-1", "Check 1", "spec/check")
            ]));

        var (task1, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task1.WorkId);
        await workflow.PauseAsync("user requested");
        await ReportAsync(r1, task1.WorkId, "completed");

        var runner = Grains.GetGrain<IRunnerGrain>(r1);
        Assert.Null(await runner.PollAsync(Services));
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task PausedWorkflow_Resume_ContinuesWithNextTask()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new("task-1", "Task 1", "spec/task"),
                new("task-2", "Task 2", "spec/task")
            ],
            checks:
            [
                new("check-1", "Check 1", "spec/check")
            ]));

        var (task1, r1) = await PollWorkAnyAsync();
        await workflow.PauseAsync("pause");
        await ReportAsync(r1, task1.WorkId, "completed");

        await workflow.ResumeAsync();

        var (task2, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("task-2.", task2.WorkId);
        await ReportAsync(r2, task2.WorkId, "completed");

        var (check, r3) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r3, check, "check-1");

        var runner2 = Grains.GetGrain<IRunnerGrain>(r3);
        Assert.Equal(RunnerStatus.Online, (await runner2.GetRuntimeStateAsync()).Status);
    }

    [Fact]
    public async Task PausedWorkflow_TaskReportWithFollowUp_RemainsPausedUntilExplicitResume()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new("task-1", "Task 1", "spec/task")
            ],
            checks: []));

        var (task, runnerId) = await PollWorkAnyAsync();
        await workflow.PauseAsync("user requested");

        var acknowledgement = await workflow.ReceiveTaskReportAsync(runnerId, task.WorkId, new TaskReport(
            task.WorkId,
            TaskReportStatus.Succeeded,
            Output: null,
            Artifacts: null,
            AddTasks: new List<RuntimeTaskInput>
            {
                new("follow-up", "Follow up", "spec/task")
            }));

        Assert.Equal(ReportAck.Accepted, acknowledgement);
        Assert.Equal("Paused", await workflow.GetRunStatusAsync());
        Assert.Null(await Grains.GetGrain<IRunnerGrain>(runnerId).PollAsync(Services));

        await workflow.ResumeAsync();

        var (followUp, _) = await PollWorkAnyAsync();
        Assert.StartsWith("follow-up.", followUp.WorkId);
    }

    [Fact]
    public async Task StoppedWorkflow_Resume_ThrowsInvalidOperationException()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        await workflow.StopAsync("user-stop");

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ResumeAsync());
    }

    [Fact]
    public async Task StoppedWorkflow_HasTerminalStatus()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var statusBefore = await workflow.GetRunStatusAsync();
        // After Start without a runner assignment, the workflow is in
        // Pending (started, has dispatchable work, waiting for any
        // runner to claim).
        Assert.Equal("Pending", statusBefore);

        await workflow.StopAsync("user-stop");

        var statusAfter = await workflow.GetRunStatusAsync();
        Assert.Equal("Stopped", statusAfter);
    }

    [Fact]
    public async Task StoppedWorkflow_Resumes_DoesNotReturnNewWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (task1, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task1.WorkId, "completed");

        await workflow.StopAsync("user-stop");

        var runner = Grains.GetGrain<IRunnerGrain>(r1);
        Assert.Null(await runner.PollAsync(Services));
    }

    [Fact]
    public async Task StoppedWorkflow_ReleasesLease()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (_, r1) = await PollWorkAnyAsync();

        await workflow.StopAsync("user-stop");

        Assert.Null(await workflow.GetCurrentWorkIdAsync());
    }

    [Fact]
    public async Task StoppedWorkflow_CannotBeStoppedAgain()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        await workflow.StopAsync("first");

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.StopAsync("second"));
    }

    [Fact]
    public async Task CompletedWorkflow_CannotBeStopped()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var (task1, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task1.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "check-1");

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Completed", status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.StopAsync("after-completion"));
    }
}
