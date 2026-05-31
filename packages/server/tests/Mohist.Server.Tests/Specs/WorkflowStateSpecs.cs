using Mohist.Server.Runner.Grains;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Workflow.Domain.Run;
using System.Text.Json;
using Mohist.Server.Workflow.Grains;
using Xunit;
using System.Linq;

namespace Mohist.Server.Tests.Specs;

public class WorkflowStateSpecs : WorkflowGrainSpecs
{
    public WorkflowStateSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task FailedWorkflow_NoMoreWork()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "failed", "boom");

        var runner = Grains.GetGrain<IRunnerGrain>(r1);
        Assert.Null(await runner.PollAsync());
    }

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

    [Fact]
    public async Task RejectedWorkflow_NoMoreWork()
    {
        var workflow = await StartWorkflowAsync(ApprovalStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, check, "plan-ok");

        await workflow.RejectAsync("bad");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync());
    }

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

    [Fact]
    public async Task StaleReport_IgnoredWorkflowContinues()
    {
        await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (check, r2) = await PollWorkAnyAsync();
        Assert.StartsWith("checks-", check.WorkId);

        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId!);
        await workflow.ReportResultAsync(r1, task.WorkId, new WorkDispatchResult("failed", "stale"));

        await ReportChecksPassAsync(r2, check, "check-1");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task StartedWorkflow_RunnerClaimsFromBacklog()
    {
        await ClearBacklogAsync();
        var runnerId = await RegisterRunnerAsync();
        var workflowId = $"wf-{Guid.NewGuid():N}";
        _workflowId = workflowId;
        _runnerId = runnerId;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);

        await workflow.StartAsync(SingleStage(checks: []), TestInput());

        var work = await runner.PollAsync();
        Assert.NotNull(work);
    }

    [Fact]
    public async Task StartWithoutRunner_RunnerClaimsFromBacklogLater()
    {
        var workflow = await CreateWorkflowAsync();
        await workflow.StartAsync(SingleStage(), TestInput());

        var runnerId = await RegisterRunnerAsync();
        _runnerId = runnerId;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var work = await runner.PollAsync();
        Assert.NotNull(work);
        Assert.StartsWith("task-1.", work.WorkId);
    }

    [Fact]
    public async Task ActiveLease_PreservesOwnership_BlocksDuplicateDispatch()
    {
        var workflow = await StartWorkflowAsync(SingleStage());

        var (task, r1) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task.WorkId);

        var duplicateDispatch = await workflow.GetWorkAsync("different-runner");
        Assert.Null(duplicateDispatch);

        var assignedRunner = await workflow.GetAssignedRunnerIdAsync();
        Assert.Equal(r1, assignedRunner);
    }

    [Fact]
    public async Task ActiveLease_DifferentRunnerPoll_DoesNotOverwriteExistingLease()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));

        var (work, ownerRunnerId) = await PollWorkAnyAsync();
        var otherRunnerId = await RegisterRunnerAsync();

        Assert.Null(await workflow.GetWorkAsync(otherRunnerId));
        Assert.Null(await workflow.GetWorkAsync(otherRunnerId));
        Assert.Equal(ownerRunnerId, await workflow.GetAssignedRunnerIdAsync());
        Assert.Equal(work.WorkId, await workflow.GetAssignedWorkIdAsync());

        var startEvents = (await EventStore.ListWorkflowEventsAsync(_workflowId!))
            .Where(e => e.Type == "workflow_task_started")
            .ToList();
        Assert.Single(startEvents);
        Assert.Equal(ownerRunnerId, startEvents[0].RunnerId);
        Assert.Equal(work.WorkId, startEvents[0].TaskId);
    }

    [Fact]
    public async Task ActiveLease_SameOwnerPoll_DoesNotCreateDuplicateAssignment()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));

        var (firstWork, runnerId) = await PollWorkAnyAsync();

        Assert.Null(await workflow.GetWorkAsync(runnerId));
        Assert.Null(await workflow.GetWorkAsync(runnerId));
        Assert.Equal(runnerId, await workflow.GetAssignedRunnerIdAsync());
        Assert.Equal(firstWork.WorkId, await workflow.GetAssignedWorkIdAsync());

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Null(await runner.PollAsync());

        var startEvents = (await EventStore.ListWorkflowEventsAsync(_workflowId!))
            .Where(e => e.Type == "workflow_task_started")
            .ToList();
        Assert.Single(startEvents);
        Assert.Equal(runnerId, startEvents[0].RunnerId);
        Assert.Equal(firstWork.WorkId, startEvents[0].TaskId);
    }

    [Fact]
    public async Task WorkflowTaskStarted_IsRecordedAfterMatchingLeaseIsPersisted()
    {
        await StartWorkflowAsync(SingleStage(checks: []));

        var (work, runnerId) = await PollWorkAnyAsync();

        var leaseJson = await ReadLeaseJsonAsync(_workflowId!);
        Assert.NotNull(leaseJson);

        var lease = JsonSerializer.Deserialize<WorkLease>(leaseJson!);
        Assert.NotNull(lease);
        Assert.Equal(work.WorkId, lease.WorkId);
        Assert.Equal(runnerId, lease.RunnerId);

        var started = (await EventStore.ListWorkflowEventsAsync(_workflowId!))
            .Single(e => e.Type == "workflow_task_started");

        Assert.Equal(_workflowId, started.WorkflowRunId);
        Assert.Equal(lease.WorkId, started.TaskId);
        Assert.Equal(lease.RunnerId, started.RunnerId);
    }

    private async Task<string?> ReadLeaseJsonAsync(string workflowRunId)
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options;

        await using var db = new MohistDbContext(options);
        var row = await db.WorkflowLeases.FindAsync(workflowRunId);
        return row?.StateJson;
    }
}
