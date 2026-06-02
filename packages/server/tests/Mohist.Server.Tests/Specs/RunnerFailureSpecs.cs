using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class RunnerFailureSpecs : WorkflowGrainSpecs
{
    public RunnerFailureSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task InFlightTask_LosesRunner_WorkIsRedispatchedToNewRunner()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, _) = await PollWorkAnyAsync();

        await workflow.AbandonCurrentWorkAsync(_runnerId!, "Runner heartbeat timeout");

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Running", status);

        var r2Id = await RegisterRunnerAsync();
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (redispatched, rr2) = await PollWorkAsync(r2Id);
        Assert.Equal("task", redispatched.WorkType);
        await ReportAsync(rr2, redispatched.WorkId, "completed");

        var (checks, rr3) = await PollWorkAsync(r2Id);
        await ReportChecksPassAsync(rr3, checks, "check-1");

        var finalStatus = await workflow.GetRunStatusAsync();
        Assert.Equal("Completed", finalStatus);
    }

    [Fact]
    public async Task InFlightChecks_LoseRunner_ChecksAreRedispatchedToNewRunner()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, _) = await PollWorkAnyAsync();

        await workflow.AbandonCurrentWorkAsync(_runnerId!, "Runner heartbeat timeout");

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Running", status);

        var r2Id = await RegisterRunnerAsync();
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (redispatched, rr2) = await PollWorkAsync(r2Id);
        Assert.Equal("checks", redispatched.WorkType);
        await ReportChecksPassAsync(rr2, redispatched, "check-1");

        var finalStatus = await workflow.GetRunStatusAsync();
        Assert.Equal("Completed", finalStatus);
    }

    [Fact]
    public async Task AbandonWithNoLease_DoesNothing()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        await workflow.AbandonCurrentWorkAsync(_runnerId!, "nothing in flight");

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Running", status);
    }

    [Fact]
    public async Task StaleReport_ArrivesAfterAbandon_IgnoredBecauseLeaseCleared()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        var staleWorkId = task.WorkId;

        await workflow.AbandonCurrentWorkAsync(_runnerId!, "Runner heartbeat timeout");

        await ReportAsync(r1, staleWorkId, "completed");

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Running", status);
    }

    [Fact]
    public async Task RunnerUnregistersWithInFlightTask_WorkIsRedispatched()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, _) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        await runner.UnregisterAsync();

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Running", status);

        var r2Id = await RegisterRunnerAsync();
        _runnerId = r2Id;
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (redispatched, rr2) = await PollWorkAnyAsync();
        Assert.Equal("task", redispatched.WorkType);
        await ReportAsync(rr2, redispatched.WorkId, "completed");

        var (checks, rr3) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(rr3, checks, "check-1");

        var finalStatus = await workflow.GetRunStatusAsync();
        Assert.Equal("Completed", finalStatus);
    }

    [Fact]
    public async Task RunnerUnregistersWithoutInFlightWork_WorkflowKeepsRunning()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var runner = Grains.GetGrain<IRunnerGrain>(_runnerId!);
        await runner.UnregisterAsync();

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Running", status);
    }

    [Fact]
    public async Task AbandonFromWrongRunner_IsIgnored()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, _) = await PollWorkAnyAsync();

        var otherRunnerId = await RegisterRunnerAsync();
        await workflow.AbandonCurrentWorkAsync(otherRunnerId, "wrong runner");

        var status = await GetQueryService().GetStatusAsync(_workflowId!);
        Assert.NotNull(status);
        Assert.Equal("Running", status.Status);
        Assert.NotNull(status.PendingWork);
    }

    [Fact]
    public async Task NewRunnerHoldsInFlightTask_OldRunnerAbandons_WorkflowKeepsRunning()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "boom");

        await workflow.RetryAsync();

        var r2Id = await RegisterRunnerAsync();
        _runnerId = r2Id;
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (retried, _) = await PollWorkAnyAsync();
        Assert.Equal("task", retried.WorkType);

        await workflow.AbandonCurrentWorkAsync(r1, "stale runner timeout");

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Running", status);
    }

    [Fact]
    public async Task NewRunnerHoldsInFlightChecks_OldRunnerAbandons_WorkflowKeepsRunning()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");
        var (checks, _) = await PollWorkAnyAsync();
        await workflow.AbandonCurrentWorkAsync(r1, "timeout");

        var r2Id = await RegisterRunnerAsync();
        _runnerId = r2Id;
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (retriedChecks, _) = await PollWorkAnyAsync();
        Assert.Equal("checks", retriedChecks.WorkType);

        var oldRunner = Grains.GetGrain<IRunnerGrain>(r1);
        await oldRunner.UnregisterAsync();

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Running", status);
    }

    [Fact]
    public async Task NewRunnerHoldsInFlightTask_OldRunnerUnregisters_WorkflowKeepsRunning()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "boom");

        await workflow.RetryAsync();

        var r2Id = await RegisterRunnerAsync();
        _runnerId = r2Id;
        var r2 = Grains.GetGrain<IRunnerGrain>(r2Id);
        await r2.AssignWorkflowAsync(_workflowId!);

        var (retried, _) = await PollWorkAnyAsync();
        Assert.Equal("task", retried.WorkType);

        var oldRunner = Grains.GetGrain<IRunnerGrain>(r1);
        await oldRunner.UnregisterAsync();

        var status = await workflow.GetRunStatusAsync();
        Assert.Equal("Running", status);
    }

    [Fact(Skip = "Replaced by WorkflowQueue expired lease recovery; WorkflowGrain no longer recovers persisted WorkflowLeases directly.")]
    public async Task OfflinePersistedLease_IsRecoveredThroughTimeoutAbandonment_BeforeRedispatch()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));

        var (task, runnerId) = await PollWorkAnyAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        await DeactivateWorkflowAsync(_workflowId!);

        var nextRunnerId = await RegisterRunnerAsync();
        var recovered = Grains.GetGrain<IWorkflowGrain>(_workflowId!);

        var redispatched = await recovered.GetWorkAsync(nextRunnerId);
        Assert.NotNull(redispatched);
        Assert.Equal(task.WorkId, redispatched.WorkId);
        Assert.Equal(nextRunnerId, await recovered.GetAssignedRunnerIdAsync());

        var events = (await EventStore.ListWorkflowEventsAsync(_workflowId!)).ToList();
        var abandoned = events.Single(e => e.Type == "workflow_work_abandoned");
        var started = events.Where(e => e.Type == "workflow_task_started").ToList();

        Assert.Equal(runnerId, abandoned.RunnerId);
        Assert.Equal(task.WorkId, abandoned.TaskId);
        Assert.Equal(2, started.Count);
        Assert.Equal(runnerId, started[0].RunnerId);
        Assert.Equal(nextRunnerId, started[1].RunnerId);
        Assert.Equal(task.WorkId, started[0].TaskId);
        Assert.Equal(task.WorkId, started[1].TaskId);
        Assert.True(events.IndexOf(abandoned) > events.IndexOf(started[0]));
        Assert.True(events.IndexOf(abandoned) < events.IndexOf(started[1]));
    }

    [Fact]
    public async Task RegisteredButUnavailableLeaseOwner_RemainsRecoveryBlocked()
    {
        var workflow = await StartWorkflowWithoutRunnerAsync(SingleStage(checks: []));
        var workflowId = _workflowId!;

        await SeedLeaseAsync(workflowId, new WorkLease(
            WorkId: "task-1.1",
            WorkType: "task",
            Stage: "build",
            LogicalId: "task-1",
            Title: "Task 1",
            RunnerId: "runner-blocked"));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.ForProject("test-project"));
        await registry.RegisterAsync(new RunnerInfo("runner-blocked", ["spec/*"], "test-host", "test-project"));

        await DeactivateWorkflowAsync(workflowId);

        var reactivated = Grains.GetGrain<IWorkflowGrain>(workflowId);
        Assert.Null(await reactivated.GetWorkAsync("runner-other"));
        Assert.Equal("runner-blocked", await reactivated.GetAssignedRunnerIdAsync());
        Assert.Equal("task-1.1", await reactivated.GetAssignedWorkIdAsync());

        var events = await EventStore.ListWorkflowEventsAsync(workflowId);
        Assert.DoesNotContain(events, e => e.Type == "workflow_work_abandoned");
        Assert.DoesNotContain(events, e => e.Type == "workflow_task_started");
    }
}
