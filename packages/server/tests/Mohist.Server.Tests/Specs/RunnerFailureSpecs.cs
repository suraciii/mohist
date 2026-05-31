using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
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
}
