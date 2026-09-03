using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Xunit;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.L0Tests.Workflow.GrainContracts;

/// <summary>
/// Pause, resume, stop, and session-stop arbitration of the workflow run,
/// driven through the real grain without a cluster. These migrate the
/// state-matrix scenarios from the L1 PausingWorkSpecs; work
/// availability behind a paused or stopped run is asserted through
/// ClaimNextAsync refusing to hand out new work.
/// </summary>
[Collection("MohistDb")]
[Trait("level", "L0")]
public sealed class WorkflowGrainPauseResumeSpecs
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider TimeProvider = new(FixedTime);
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainPauseResumeSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunningWorkflow_Pause_StopsAfterCurrentTask()
    {
        var arrangement = await ArrangeAsync("wr-pause-after-current", TwoTaskStage());

        var task1 = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(task1);
        Assert.StartsWith("task-1.", task1!.Id);
        await arrangement.Grain.PauseAsync("user requested");
        await ReportCompletedAsync(arrangement, task1);

        // The completed current task settles, but the paused run hands out no
        // further work.
        Assert.Null(await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation"));
        Assert.Equal("Paused", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task PausedWorkflow_Resume_ContinuesWithNextTask()
    {
        var arrangement = await ArrangeAsync("wr-pause-resume-next", TwoTaskStage());

        var task1 = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(task1);
        await arrangement.Grain.PauseAsync("pause");
        await ReportCompletedAsync(arrangement, task1);

        await arrangement.Grain.ResumeAsync();

        var task2 = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("task-2.", task2!.Id);
        await ReportCompletedAsync(arrangement, task2);

        var check = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(check);
        Assert.True(check!.IsChecks);
        await ReportChecksPassAsync(arrangement, check);
    }

    [Fact]
    public async Task PausedWorkflow_TaskReportWithFollowUp_RemainsPausedUntilExplicitResume()
    {
        var arrangement = await ArrangeAsync("wr-pause-followup");

        var task = await arrangement.AssignAndClaimAsync();
        await arrangement.Grain.PauseAsync("user requested");

        var acknowledgement = await arrangement.ReportFollowUpAsync(task!, "follow-up");
        Assert.Equal(WorkReportVerdict.Accepted, acknowledgement);
        Assert.Equal("Paused", await arrangement.Grain.GetRunStatusAsync());
        Assert.Null(await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation"));

        await arrangement.Grain.ResumeAsync();

        var followUp = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("follow-up.", followUp!.Id);
    }

    [Fact]
    public async Task PausedWorkflow_ConfirmedSessionStop_RequeuesTaskAndResumeDispatchesIt()
    {
        var arrangement = await ArrangeAsync("wr-pause-session-stop-requeue");

        var task = await arrangement.AssignAndClaimAsync();
        await arrangement.Grain.PauseAsync("user requested");

        var acknowledgement = await arrangement.AbandonActiveWorkAsync(task!, "session-stop");
        Assert.Equal(WorkReportVerdict.Accepted, acknowledgement);
        Assert.Equal("Paused", await arrangement.Grain.GetRunStatusAsync());
        Assert.Null(await arrangement.Grain.GetCurrentWorkIdAsync());
        Assert.Equal(
            WorkReportVerdict.Refused,
            await arrangement.AbandonActiveWorkAsync(task!, "duplicate-session-stop"));

        await arrangement.Grain.ResumeAsync();

        var resumed = await arrangement.AssignAndClaimAsync();
        Assert.Equal(task!.Id, resumed!.Id);
    }

    [Fact]
    public async Task RunningWorkflow_ConfirmedSessionStop_FailsTaskAndUnlocksRerun()
    {
        var arrangement = await ArrangeAsync("wr-pause-session-stop-fail");

        var task = await arrangement.AssignAndClaimAsync();

        var acknowledgement = await arrangement.AbandonActiveWorkAsync(task!, "session-stop");
        Assert.Equal(WorkReportVerdict.Accepted, acknowledgement);
        Assert.Equal("Failed", await arrangement.Grain.GetRunStatusAsync());
        Assert.Null(await arrangement.Grain.GetCurrentWorkIdAsync());

        var rerun = await arrangement.Grain.RerunFromStageAsync("build");
        Assert.True(rerun.Success, rerun.Error);
        var rerunTask = await arrangement.AssignAndClaimAsync();
        Assert.NotEqual(task!.Id, rerunTask!.Id);
    }

    [Fact]
    public async Task StoppedWorkflow_Resume_ThrowsInvalidOperationException()
    {
        var arrangement = await ArrangeAsync("wr-pause-resume-stopped");

        await arrangement.Grain.StopAsync("user-stop");

        await Assert.ThrowsAsync<InvalidOperationException>(() => arrangement.Grain.ResumeAsync());
    }

    [Fact]
    public async Task StoppedWorkflow_HasTerminalStatus()
    {
        var arrangement = await ArrangeAsync("wr-pause-terminal-status");

        // After Start without an assignment, the run is Pending: started,
        // dispatchable, waiting for any worker to claim it.
        Assert.Equal("Pending", await arrangement.Grain.GetRunStatusAsync());

        await arrangement.Grain.StopAsync("user-stop");

        Assert.Equal("Stopped", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task StoppedWorkflow_DoesNotReturnNewWork()
    {
        // The stage still owes its check when the task completes, so the stop
        // lands on a Stopped run with dispatchable work outstanding.
        var arrangement = await ArrangeAsync("wr-pause-stopped-no-work", SingleStageWithCheck());

        var task1 = await arrangement.AssignAndClaimAsync();
        await ReportCompletedAsync(arrangement, task1!);

        await arrangement.Grain.StopAsync("user-stop");

        Assert.Null(await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation"));
    }

    [Fact]
    public async Task StoppedWorkflow_ReleasesLease()
    {
        var arrangement = await ArrangeAsync("wr-pause-release-lease");

        var _ = await arrangement.AssignAndClaimAsync();

        await arrangement.Grain.StopAsync("user-stop");

        Assert.Null(await arrangement.Grain.GetCurrentWorkIdAsync());
    }

    [Fact]
    public async Task StoppedWorkflow_CannotBeStoppedAgain()
    {
        var arrangement = await ArrangeAsync("wr-pause-double-stop");

        await arrangement.Grain.StopAsync("first");

        await Assert.ThrowsAsync<InvalidOperationException>(() => arrangement.Grain.StopAsync("second"));
    }

    [Fact]
    public async Task CompletedWorkflow_CannotBeStopped()
    {
        var arrangement = await ArrangeAsync("wr-pause-complete-then-stop", SingleStageWithCheck());

        await ReportCompletedAsync(arrangement, (await arrangement.AssignAndClaimAsync())!);
        var check = await arrangement.AssignAndClaimAsync();
        await ReportChecksPassAsync(arrangement, check!);

        Assert.Equal("Completed", await arrangement.Grain.GetRunStatusAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => arrangement.Grain.StopAsync("after-completion"));
    }

    private sealed record Arrangement(
        WorkflowGrain Grain,
        IWorkflowRunStore Store,
        string RunId,
        string WorkerId)
    {
        public async Task<WorkItem?> AssignAndClaimAsync()
        {
            await Grain.AssignWorkerAsync(WorkerId);
            return await Grain.ClaimNextAsync(WorkerId, "test-generation");
        }

        public async Task<WorkReportVerdict> AbandonActiveWorkAsync(WorkItem item, string reason) =>
            await Grain.AbandonActiveWorkAsync(WorkerId, item.Id!, reason);

        /// <summary>
        /// Reports the claimed task complete, resolving the persisted task-run
        /// id the report must carry.
        /// </summary>
        public async Task<WorkReportVerdict> ReportCompletedAsync(WorkItem item)
        {
            var run = await Store.LoadAsync(RunId) ?? throw new InvalidOperationException("run missing");
            var runningTask = run.CurrentStage().RunningTask
                ?? throw new InvalidOperationException("no running task to report");
            return await Grain.ReceiveTaskReportAsync(
                WorkerId,
                item.Id!,
                new TaskReport(item.Id!, TaskReportStatus.Succeeded, Output: null, Artifacts: null, ActionAttemptId: runningTask.Id));
        }

        public async Task<WorkReportVerdict> ReportFollowUpAsync(WorkItem item, string followUpTaskId)
        {
            var run = await Store.LoadAsync(RunId) ?? throw new InvalidOperationException("run missing");
            var runningTask = run.CurrentStage().RunningTask
                ?? throw new InvalidOperationException("no running task to report");
            return await Grain.ReceiveTaskReportAsync(
                WorkerId,
                item.Id!,
                new TaskReport(
                    item.Id!,
                    TaskReportStatus.Succeeded,
                    Output: null,
                    Artifacts: null,
                    AddTasks: [new RuntimeTaskInput(followUpTaskId, "Follow up", "spec/task")],
                    ActionAttemptId: runningTask.Id));
        }
    }

    private async Task<Arrangement> ArrangeAsync(
        string runId,
        WorkflowDefinition? definition = null)
    {
        var projectId = $"prof-pause-{Math.Abs(WorkflowYamlSerializer.ToYaml(definition ?? SingleStage()).GetHashCode()):x8}";
        await WorkflowGrainContractSupport.SeedTemplateAsync(
            _fixture,
            projectId,
            definition ?? SingleStage(),
            FixedTime);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var grain = WorkflowGrainContractSupport.CreateGrain(scope.ServiceProvider, store, runId, TimeProvider);
        await grain.OnActivateAsync(CancellationToken.None);
        await grain.EnsureStartedAsync(new WorkflowIssueContext(projectId, 1, null));
        return new Arrangement(grain, store, runId, "worker-1");
    }

    private static async Task<WorkReportVerdict> ReportCompletedAsync(Arrangement arrangement, WorkItem item) =>
        await arrangement.ReportCompletedAsync(item);

    private static async Task ReportChecksPassAsync(Arrangement arrangement, WorkItem check) =>
        await arrangement.Grain.ReceiveCheckReportAsync(
            arrangement.WorkerId,
            check.Id!,
            new CheckReport(check.Stage, [new CheckResult("check-1", CheckResultStatus.Passed)]));

    private static WorkflowDefinition SingleStage() => new(
    [
        new StageDefinition("build", [new("task-1", "Task 1", "spec/task")], []),
    ]);

    private static WorkflowDefinition SingleStageWithCheck() => new(
    [
        new StageDefinition("build", [new("task-1", "Task 1", "spec/task")], [new("check-1", "Check 1", "spec/check")]),
    ]);

    private static WorkflowDefinition TwoTaskStage() => new(
    [
        new StageDefinition(
            "build",
            [new("task-1", "Task 1", "spec/task"), new("task-2", "Task 2", "spec/task")],
            [new("check-1", "Check 1", "spec/check")]),
    ]);
}
