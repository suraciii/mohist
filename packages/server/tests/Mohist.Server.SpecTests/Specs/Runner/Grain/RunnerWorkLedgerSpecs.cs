using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Specs.Workflow;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class RunnerWorkLedgerSpecs : WorkflowGrainSpecs
{
    public RunnerWorkLedgerSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    // The reconciliation model removed workflow work from the runner ledger:
    // the workflow run IS the ledger now, and the stateless DispatchService
    // computes dispatches per poll (see RunnerGrain class docstring). The
    // following ledger behaviors now apply ONLY to agent-job (push) work,
    // which is still tracked here. The previously-pinned workflow-ledger
    // variants — PollWorkflowWork, ReportWorkflowSuccess/Failure,
    // TerminalRow, Reactivation_HydratesOutstandingRows,
    // ReactivatedWorkflowWork, and RunnerLoss-Synthesizes workflow failure —
    // were deleted as they pinned removed behavior. The work-completion
    // wall-clock reminder ("work-timeout") was also removed entirely; the
    // only server-side timer is presence expiry, so the five
    // EnsureWorkTimeoutReminder_* specs were deleted too.

    [Fact]
    public async Task AgentJobWork_HasItsOnlyHomeInRunnerWorks()
    {
        await ClearBacklogAsync();
        var runnerId = $"agent-job-ledger-runner-{Guid.NewGuid():N}";
        var projectId = $"agent-job-ledger-project-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));

        var jobKey = $"agent-job-ledger-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(jobKey);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "ledger test",
            WorkspacePath: "/tmp/agent-job-ledger",
            ProjectId: projectId,
            AgentId: "agent-test"));

        WorkDispatch? dispatch = await TestWait.ForAsync(
            () => runner.PollAsync(Services),
            d => d is not null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(20),
            $"Runner '{runnerId}' to receive dispatch for job {jobKey}");

        Assert.NotNull(dispatch);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, dispatch!.OwnerKind);
        Assert.Equal(jobKey, dispatch.AgentJobId);

        var takeTime = _fixture.TimeProvider.GetUtcNow();
        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.AgentJob, jobKey, dispatch.WorkId);
        Assert.NotNull(row);
        Assert.Equal("outstanding", row!.Status);
        Assert.Equal(takeTime, row.TakenAt);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var report = await runner.ReportAgentJobResultAsync(jobKey, dispatch.WorkId, new WorkResult("completed", "ok"));
        Assert.True(report.Tracked);

        row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.AgentJob, jobKey, dispatch.WorkId);
        Assert.Equal("completed", row!.Status);
        Assert.Equal(takeTime, row.TakenAt);
    }

    [Fact]
    public async Task RunnerLoss_SynthesizesFailedRunnerLost_ForWorkflowWork()
    {
        // Under reconciliation the runner grain holds no workflow work
        // records, but closeout still synthesizes FAILED for any workflow
        // Running work this runner held (queried from the store) and reports
        // it through the normal channel. The ledger-row assertion the old
        // variant carried was removed with the workflow-ledger behavior.
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(TaskRunStatus.Failed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Fact]
    public async Task RunnerLoss_SynthesizesAgentJobFailure_AndUpdatesLedgerRow()
    {
        await ClearBacklogAsync();
        var runnerId = $"agent-job-loss-runner-{Guid.NewGuid():N}";
        var projectId = $"agent-job-loss-project-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));

        var jobKey = $"agent-job-loss-{Guid.NewGuid():N}";
        var work = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: $"agent-job-loss-work-{Guid.NewGuid():N}",
            AgentJobId: jobKey,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob,
            AgentId: "agent-test");
        var assigned = await runner.AssignAgentJobAsync(work);
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assigned.Status);

        await runner.UnregisterAsync();

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.AgentJob, jobKey, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal("runner-lost", row.Reason);
        Assert.NotNull(row.FinishedAt);

        var job = Grains.GetGrain<IAgentJobGrain>(jobKey);
        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal("runner-lost", terminal.Message);
        Assert.Equal("runner-lost", terminal.FailureReason);
    }

    [Fact]
    public async Task RunnerLoss_ContextlessAgentJob_ReactivationRetriesFailureEvent()
    {
        await ClearBacklogAsync();
        var runnerId = $"agent-job-raw-loss-runner-{Guid.NewGuid():N}";
        var projectId = $"agent-job-raw-loss-project-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));

        var jobKey = $"agent-job-raw-loss-{Guid.NewGuid():N}";
        var work = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: $"agent-job-raw-loss-work-{Guid.NewGuid():N}",
            AgentJobId: jobKey,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob,
            AgentId: "agent-test");
        _fixture.EventStore.ThrowOnAppend = evt =>
            evt.Type == EventCatalog.ReverseDns.AgentJobFailed;

        try
        {
            var assigned = await runner.AssignAgentJobAsync(work);
            Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assigned.Status);

            await runner.UnregisterAsync();
            Assert.DoesNotContain(_fixture.EventStore.Appended,
                evt => evt.Envelope.Type == EventCatalog.ReverseDns.AgentJobFailed
                    && evt.Envelope.Subject == jobKey);

            _fixture.EventStore.ThrowOnAppend = null;
            await Grains.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

            var job = Grains.GetGrain<IAgentJobGrain>(jobKey);
            await job.GetStatusAsync();

            var failure = Assert.Single(_fixture.EventStore.Appended,
                evt => evt.Envelope.Type == EventCatalog.ReverseDns.AgentJobFailed
                    && evt.Envelope.Subject == jobKey);
            Assert.Equal("agent-test", failure.Envelope.Extensions[EventCatalog.Lineage.AgentId]);
        }
        finally
        {
            _fixture.EventStore.ThrowOnAppend = null;
        }
    }

    private async Task<RunnerWorkRow?> FindRunnerWorkAsync(
        string runnerId,
        string ownerKind,
        string ownerId,
        string workId)
    {
        await using var db = new MohistDbContext(new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options);
        return await db.RunnerWorks
            .Where(r =>
                r.RunnerId == runnerId &&
                r.OwnerKind == ownerKind &&
                r.OwnerId == ownerId &&
                r.WorkId == workId)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(CancellationToken.None);
    }

    private Task DeactivateRunnerAsync(string runnerId) =>
        GrainTestSupport.ForceActivationCollectionForGrainAsync(
            Grains,
            nameof(RunnerGrain),
            runnerId,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(50),
            $"Runner grain '{runnerId}' to deactivate");
}
