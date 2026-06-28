using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Specs.Workflow;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Specs.Runner.Grain;

[Collection("WorkflowGrain")]
public class RunnerWorkLedgerSpecs : WorkflowGrainSpecs
{
    public RunnerWorkLedgerSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task PollWorkflowWork_InsertsRunnerWorksRow_WithOutstandingStatusAndFakeTakenAt()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var takeTime = _fixture.TimeProvider.GetUtcNow();

        var (work, runnerId) = await PollWorkAnyAsync();

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("outstanding", row!.Status);
        Assert.Equal(takeTime, row.TakenAt);
        Assert.Null(row.Reason);
        Assert.Null(row.FinishedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ReportWorkflowSuccess_UpdatesRowToCompleted_WithoutDeletingIt()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var takenAt = _fixture.TimeProvider.GetUtcNow();

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        var finishedAt = _fixture.TimeProvider.GetUtcNow();
        await ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, new WorkResult("completed"));

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("completed", row!.Status);
        Assert.Equal(takenAt, row.TakenAt);
        Assert.Null(row.Reason);
        Assert.Equal(finishedAt, row.FinishedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ReportWorkflowFailure_UpdatesRowToFailed_WithReasonAndFinishedAt_WithoutDeletingIt()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var takenAt = _fixture.TimeProvider.GetUtcNow();

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        var finishedAt = _fixture.TimeProvider.GetUtcNow();
        await ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, new WorkResult("failed", "it-broke"));

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal(takenAt, row.TakenAt);
        Assert.Equal("it-broke", row.Reason);
        Assert.Equal(finishedAt, row.FinishedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task TerminalRow_IsNeverTransitionedAgain()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, new WorkResult("failed", "first"));

        var firstTerminal = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.Equal("failed", firstTerminal!.Status);
        Assert.Equal("first", firstTerminal.Reason);

        await ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, new WorkResult("completed"));

        var stillTerminal = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.Equal("failed", stillTerminal!.Status);
        Assert.Equal("first", stillTerminal.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Reactivation_HydratesOutstandingRows_FromLedger_PreservingTakenAt()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var takenAt = _fixture.TimeProvider.GetUtcNow();

        await DeactivateRunnerAsync(runnerId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(5));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", TestProjectId(work.WorkflowRunId)));
        var state = await runner.GetRuntimeStateAsync();
        Assert.Contains(state.ActiveWorks, w =>
            w.OwnerKind == WorkDispatchOwnerKinds.Workflow
            && w.OwnerId == work.WorkflowRunId
            && w.WorkId == work.WorkId
            && w.TakenAt == takenAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ReactivatedWorkflowWork_ReportPreservesOriginalTakenAt()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var takenAt = _fixture.TimeProvider.GetUtcNow();

        await DeactivateRunnerAsync(runnerId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(5));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.ReportWorkflowResultAsync(work.WorkflowRunId, work.WorkId, new WorkResult("completed"));

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.Equal("completed", row!.Status);
        Assert.Equal(takenAt, row.TakenAt);
        Assert.NotEqual(takenAt, row.FinishedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
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
            ProjectId: projectId));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        WorkDispatch? dispatch = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            dispatch = await runner.PollAsync();
            if (dispatch is not null) break;
            await Task.Delay(20);
        }

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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task RunnerLoss_SynthesizesFailedRunnerLost_AndUpdatesLedgerRow()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal("runner-lost", row.Reason);
        Assert.NotNull(row.FinishedAt);

        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(TaskRunStatus.Failed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task WorkCompletionTimeout_AfterRunnerGrainReactivation_DetectsOrphanWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        await DeactivateRunnerAsync(runnerId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(11));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", TestProjectId(work.WorkflowRunId)));
        await runner.CheckWorkTimeoutsAsync();

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal("timeout", row.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task TimeoutThenRunnerLoss_DoesNotResynthesizeWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(11));
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.CheckWorkTimeoutsAsync();

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        var runAfterTimeout = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal("timeout", runAfterTimeout.Failure?.Message);

        await runner.UnregisterAsync();

        var runAfterLoss = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal("timeout", runAfterLoss.Failure?.Message);
        Assert.Equal(TaskRunStatus.Failed, runAfterLoss.Stages.Single().Tasks.Single().Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task RunnerLossThenTimeout_DoesNotResynthesizeWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        var runAfterLoss = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal("runner-lost", runAfterLoss.Failure?.Message);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(11));
        await runner.CheckWorkTimeoutsAsync();

        var runAfterTimeout = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal("runner-lost", runAfterTimeout.Failure?.Message);
        Assert.Equal(TaskRunStatus.Failed, runAfterTimeout.Stages.Single().Tasks.Single().Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
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
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task WorkCompletionTimeout_SynthesizesWorkflowFailure_AndUpdatesLedgerRow()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(11));
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.CheckWorkTimeoutsAsync();

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal("timeout", row.Reason);
        Assert.NotNull(row.FinishedAt);

        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(TaskRunStatus.Failed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal("timeout", run.Failure?.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task WorkCompletionTimeout_SynthesizesAgentJobFailure_AndUpdatesLedgerRow()
    {
        await ClearBacklogAsync();
        var runnerId = $"agent-job-timeout-ledger-runner-{Guid.NewGuid():N}";
        var projectId = $"agent-job-timeout-ledger-project-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));

        var jobKey = $"agent-job-timeout-ledger-{Guid.NewGuid():N}";
        var work = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: $"agent-job-timeout-work-{Guid.NewGuid():N}",
            AgentJobId: jobKey,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);
        var assigned = await runner.AssignAgentJobAsync(work);
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assigned.Status);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(11));
        await runner.CheckWorkTimeoutsAsync();

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.AgentJob, jobKey, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal("timeout", row.Reason);
        Assert.NotNull(row.FinishedAt);

        var job = Grains.GetGrain<IAgentJobGrain>(jobKey);
        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal("timeout", terminal.Message);
        Assert.Equal("timeout", terminal.FailureReason);
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

    private async Task DeactivateRunnerAsync(string runnerId)
    {
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.DeactivateForTestAsync();

        var management = Grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var activations = await management.GetDetailedGrainStatistics();
            if (!activations.Any(stat => stat.GrainType.Contains(nameof(RunnerGrain), StringComparison.Ordinal)
                && stat.GrainId.ToString()!.Contains(runnerId, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Runner grain '{runnerId}' did not deactivate in time.");
    }
}
