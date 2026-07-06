using Mohist.Server.Runner.Grains;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Infrastructure.Data.Workflow;
using System.Text.Json;
using Mohist.Server.Workflow.Grains;
using Xunit;
using System.Linq;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class WorkflowStateSpecs : WorkflowGrainSpecs
{
    public WorkflowStateSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task FailedWorkflow_NoMoreWork()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "boom");

        var runner = Grains.GetGrain<IRunnerGrain>(r1);
        Assert.Null(await runner.PollAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task CompletedWorkflow_NoMoreWork()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task RejectedWorkflow_LegacyReject_SchedulesFeedbackTask()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

#pragma warning disable CS0618
        await workflow.RequestChangesAsync("bad");
#pragma warning restore CS0618

        // The legacy reject path now routes through the feedback loop,
        // so a new apply-feedback task is dispatched. The runner
        // should pick it up.
        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        var work = await runner.PollAsync();
        Assert.NotNull(work);
        Assert.StartsWith("apply-feedback.", work!.WorkId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskRunning_SecondPollWaitsForCompletion()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task.WorkId);

        var runner = Grains.GetGrain<IRunnerGrain>(r1);
        Assert.Null(await runner.PollAsync());

        await ReportAsync(r1, task.WorkId, "completed");
        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check.WorkId);
        await ReportChecksPassAsync(r2, check, "check-1");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task StaleReport_IgnoredWorkflowContinues()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check.WorkId);

        var staleRunner = Grains.GetGrain<IRunnerGrain>(r1);
        await staleRunner.ReportWorkflowResultAsync(_workflowId!, task.WorkId, new WorkResult("failed", "stale"));

        await ReportChecksPassAsync(r2, check, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task StartedWorkflow_RunnerAssignsFromBacklog()
    {
        await ClearBacklogAsync();
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;
        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

        await SeedWorkflowTemplateAsync(workflowId, SingleStage(checks: []));
        await workflow.StartAsync(TestInput());

        var work = await runner.PollAsync();
        Assert.NotNull(work);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task StartWithoutRunner_RunnerAssignsFromBacklogLater()
    {
        var workflow = await CreateWorkflowAsync();
        await SeedWorkflowTemplateAsync(_workflowId!, SingleStage());
        await workflow.StartAsync(TestInput());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var work = await runner.PollAsync();
        Assert.NotNull(work);
        Assert.StartsWith("task-1.", work.WorkId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ActiveTask_PreservesOwnership_BlocksDuplicateDispatch()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task.WorkId);

        var duplicateAssignment = await workflow.AssignRunnerAsync("different-runner");
        Assert.Equal(WorkflowAssignmentStatus.Rejected, duplicateAssignment.Status);
        Assert.Equal("already-assigned", duplicateAssignment.Reason);

        var assignedRunner = await workflow.GetAssignedRunnerIdAsync();
        Assert.Equal(r1, assignedRunner);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ActiveTask_DifferentRunnerPoll_DoesNotOverwriteExistingWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));

        var (work, ownerRunnerId) = await PollWorkAnyAsync();
        var otherRunnerId = await RegisterRunnerAsync();

        var firstAttempt = await workflow.AssignRunnerAsync(otherRunnerId);
        var secondAttempt = await workflow.AssignRunnerAsync(otherRunnerId);
        Assert.Equal(WorkflowAssignmentStatus.Rejected, firstAttempt.Status);
        Assert.Equal("already-assigned", firstAttempt.Reason);
        Assert.Equal(WorkflowAssignmentStatus.Rejected, secondAttempt.Status);
        Assert.Equal("already-assigned", secondAttempt.Reason);
        Assert.Equal(ownerRunnerId, await workflow.GetAssignedRunnerIdAsync());
        Assert.Equal(work.WorkId, await workflow.GetCurrentWorkIdAsync());

    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task ActiveTask_SameOwnerPoll_DoesNotCreateDuplicateAssignment()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));

        var (firstWork, runnerId) = await PollWorkAnyAsync();

        var firstAttempt = await workflow.AssignRunnerAsync(runnerId);
        var secondAttempt = await workflow.AssignRunnerAsync(runnerId);
        Assert.Equal(WorkflowAssignmentStatus.Assigned, firstAttempt.Status);
        Assert.Equal(WorkflowAssignmentStatus.Assigned, secondAttempt.Status);
        Assert.Equal(runnerId, await workflow.GetAssignedRunnerIdAsync());
        Assert.Equal(firstWork.WorkId, await workflow.GetCurrentWorkIdAsync());

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Null(await runner.PollAsync());

    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task TaskDelivery_IsCompletedWhenRunnerReports()
    {
        await StartWorkflowAsync(SingleStage(checks: []));

        var (work, runnerId) = await PollWorkAnyAsync();
        var running = await LoadRunAsync(work.WorkflowRunId);
        var runningTask = running.CurrentStage().RunningTask;
        Assert.NotNull(runningTask);
        Assert.Equal(TaskRunStatus.Running, runningTask!.Status);
        Assert.Equal(work.WorkId, runningTask.WorkId);

        await ReportAsync(runnerId, work, "completed");

        var completed = await LoadRunAsync(work.WorkflowRunId);
        Assert.Null(completed.CurrentStage().RunningTask);
        Assert.Equal(TaskRunStatus.Completed, completed.CurrentStage().Tasks.Single().Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task WorkflowTaskStarted_IsRecordedAfterRunningTaskIsPersisted()
    {
        await StartWorkflowAsync(SingleStage(checks: []));

        var (work, runnerId) = await PollWorkAnyAsync();

        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId!);
        Assert.Equal(work.WorkId, await workflow.GetCurrentWorkIdAsync());
        Assert.Equal(runnerId, await workflow.GetAssignedRunnerIdAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task StoppedAssignedWorkflow_RequestWorkRejectsAsNotRunnable()
    {
        var runnerId = await RegisterRunnerAsync("stopped-assigned-runner");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var workflow = await CreateWorkflowAsync("wf-stopped-assigned");
        await SeedWorkflowTemplateAsync(_workflowId!, SingleStage(checks: []));
        await workflow.StartAsync(TestInput());
        await AssignWorkflowToRunnerAsync(_workflowId!, runnerId);
        await workflow.StopAsync("test-stop");

        var request = await workflow.AssignRunnerAsync(runnerId);
        Assert.Equal(WorkflowAssignmentStatus.Rejected, request.Status);
        Assert.Equal("not-runnable", request.Reason);

        Assert.Null(await runner.PollAsync());
        var runtime = await runner.GetRuntimeStateAsync();
        Assert.DoesNotContain(_workflowId, runtime.ActiveWorks.Select(w => w.OwnerId));
    }

    // Offer/claim two-phase dispatch (issue #387 follow-up): PollWorkAsync is
    // the offer — it returns work WITHOUT transitioning the task to Running.
    // Only ClaimAsync (called by the runner after it durably registers the
    // work) flips the task to Running. This makes "Running ⟺ durably
    // claimed" a flow invariant with no reconcile.
    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task PollWork_OffersWorkWithoutStarting_ClaimTransitionsToRunning()
    {
        var runnerId = await RegisterRunnerAsync("offer-claim-runner");
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        await AssignWorkflowToRunnerAsync(_workflowId!, runnerId);

        // Offer: returns the pending task, but the task stays Pending.
        var offered = await workflow.PollWorkAsync(runnerId);
        Assert.NotNull(offered);
        Assert.Equal(WorkItemTypes.Task, offered!.WorkType);

        var runAfterOffer = await LoadRunAsync(_workflowId!);
        var task = runAfterOffer.Stages.Single().Tasks.Single();
        Assert.Equal(TaskRunStatus.Pending, task.Status);
        Assert.Null(task.RunnerId);

        // Claim: transitions the task to Running, sets runner id + work id.
        var claimedWorkId = await workflow.ClaimAsync(runnerId, offered.Id!);
        Assert.NotNull(claimedWorkId);

        var runAfterClaim = await LoadRunAsync(_workflowId!);
        var claimedTask = runAfterClaim.Stages.Single().Tasks.Single();
        Assert.Equal(TaskRunStatus.Running, claimedTask.Status);
        Assert.Equal(runnerId, claimedTask.RunnerId);
        Assert.Equal(claimedWorkId, claimedTask.WorkId);
    }

    // Re-entry recovery is no longer the workflow grain's responsibility.
    // PollWorkAsync is a pure offer: it surfaces only Pending work. Once a
    // task is claimed (Running), re-polling returns null — the runner
    // rebuilds the dispatch from its own snapshot on recovery, never asking
    // the workflow to reconstruct it.
    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task PollWork_OfferOnly_NoReentryForInFlightTask()
    {
        var runnerId = await RegisterRunnerAsync("reentry-runner");
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        await AssignWorkflowToRunnerAsync(_workflowId!, runnerId);

        // Claim a task the normal way (offer + claim).
        var offered = await workflow.PollWorkAsync(runnerId);
        Assert.NotNull(offered);
        var claimedWorkId = await workflow.ClaimAsync(runnerId, offered!.Id!);
        Assert.NotNull(claimedWorkId);

        // Re-poll: the task is Running, so the offer returns null. Recovery
        // of in-flight work is the runner's own business (dispatch snapshot),
        // not the workflow's.
        var reentered = await workflow.PollWorkAsync(runnerId);
        Assert.Null(reentered);
    }
}
