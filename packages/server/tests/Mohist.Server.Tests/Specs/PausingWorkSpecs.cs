using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Errors;
using Xunit;

namespace Mohist.Server.Tests.Specs;

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
        Assert.Null(await runner.PollAsync());
        Assert.True(await runner.IsAvailableAsync());
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
        Assert.True(await runner2.IsAvailableAsync());
    }

    [Fact]
    public async Task StoppedWorkflow_Resume_ThrowsDomainException()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        await workflow.StopAsync("user-stop");

        await Assert.ThrowsAsync<WorkflowDomainException>(() => workflow.ResumeAsync());
    }

    [Fact]
    public async Task StoppedWorkflow_HasTerminalStatus()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var statusBefore = await workflow.GetRunStatusAsync();
        Assert.Equal("Running", statusBefore);

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
        Assert.Null(await runner.PollAsync());
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

        await Assert.ThrowsAsync<WorkflowDomainException>(() => workflow.StopAsync("second"));
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

        await Assert.ThrowsAsync<WorkflowDomainException>(() => workflow.StopAsync("after-completion"));
    }
}
