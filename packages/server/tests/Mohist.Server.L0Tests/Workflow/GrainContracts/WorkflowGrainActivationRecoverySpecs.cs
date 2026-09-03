using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure;
using Mohist.Server.Runner.Services;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.L0Tests.Workflow.GrainContracts;

/// <summary>
/// Activation-loss recovery on the real grain without a cluster: running
/// checks stay current across reactivation, check worker identity derives
/// from the workflow assignment, foreign-runner reports are fenced, and the
/// running-task lease survives a fresh activation (#681).
/// </summary>
[Collection("MohistDb")]
[Trait("level", "L0")]
public sealed class WorkflowGrainActivationRecoverySpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainActivationRecoverySpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Reactivation_WithDispatchedCheckAndOnlineRunner_RedispatchesCheckWork()
    {
        var arrangement = await ArrangeAsync("wr-act-check-recovery");
        var taskWork = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(taskWork);
        await arrangement.ReportCompletedAsync(taskWork!);
        var checkWork = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(checkWork);

        // Activation loss: a fresh grain instance over the same store.
        var revived = await arrangement.ReactivatedAsync();

        var recoveredWorkId = await revived.GetCurrentWorkIdAsync();
        Assert.NotNull(recoveredWorkId);
        Assert.StartsWith("checks-", recoveredWorkId);
        var run = await arrangement.LoadRunAsync();
        var check = run.Stages.Single().Checks.Single();
        Assert.Equal(StageCheckStatus.Running, check.Status);
    }

    [Fact]
    public async Task DispatchedCheckWorkerIdDerivesFromWorkflowAssignment()
    {
        var arrangement = await ArrangeAsync("wr-act-check-worker");
        var taskWork = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(taskWork);
        await arrangement.ReportCompletedAsync(taskWork!);

        var checkWork = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(checkWork);
        var run = await arrangement.LoadRunAsync();
        var check = run.Stages.Single().Checks.Single();

        Assert.Equal(arrangement.WorkerId, run.Assignment!.WorkerId);
        Assert.Equal(StageCheckStatus.Running, check.Status);
    }

    [Fact]
    public async Task CheckResultFromRunnerOutsideWorkflowAssignmentIsIgnored()
    {
        var arrangement = await ArrangeAsync("wr-act-check-fence");
        var taskWork = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(taskWork);
        await arrangement.ReportCompletedAsync(taskWork!);
        var checkWork = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(checkWork);

        var beforeReport = await arrangement.LoadRunAsync();
        var beforeCheck = beforeReport.Stages.Single().Checks.Single();

        await arrangement.Grain.ReceiveCheckReportAsync(
            "other-runner",
            checkWork!.Id!,
            new CheckReport(checkWork.Stage, [new CheckResult("check-1", CheckResultStatus.Passed, null)]));

        var run = await arrangement.LoadRunAsync();
        var check = run.Stages.Single().Checks.Single();
        Assert.Equal(beforeCheck.Status, check.Status);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }

    [Fact]
    public async Task RunningTask_SurvivesActivation_AndRestoresOwnerFields()
    {
        var arrangement = await ArrangeAsync("wr-act-lease", checks: []);
        var work = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(work);

        // Activation loss: a fresh grain instance over the same store.
        var revived = await arrangement.ReactivatedAsync();

        Assert.Equal(arrangement.WorkerId, await revived.GetAssignedWorkerIdAsync());
        var snapshot = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        var runningTask = snapshot!.Stages.Single().Tasks.Single();
        Assert.Equal("running", runningTask.Status);
        var differentRunner = await revived.AssignWorkerAsync($"other-{arrangement.WorkerId}");
        Assert.Equal(WorkflowAssignmentStatus.Rejected, differentRunner.Status);
        Assert.Equal("already-assigned", differentRunner.Reason);
    }

    private async Task<RecoveryArrangement> ArrangeAsync(string runId, CheckDefinition[]? checks = null)
    {
        // Null keeps the canonical single-check stage; an empty array opts out.
        var definition = SingleStage(
            [new TaskDefinition("task-1", "Task 1", "spec/task")],
            checks ?? [new CheckDefinition("check-1", "Check 1", "spec/check")]);
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture, runId, definition, TimeProvider, workerId: $"runner-{runId}");
        return new RecoveryArrangement(arrangement, _fixture);
    }

    private static WorkflowDefinition SingleStage(List<TaskDefinition> tasks, CheckDefinition[] checks) => new(
    [
        new StageDefinition("build", tasks, checks),
    ]);

    private sealed record RecoveryArrangement(WorkflowGrainArrangement Arrangement, MohistDbFixture Fixture)
    {
        public WorkflowGrain Grain => Arrangement.Grain;
        public string RunId => Arrangement.RunId;
        public string WorkerId => Arrangement.WorkerId;
        public IServiceProvider Services => Arrangement.Services;
        public WorkflowQuerier Querier => Arrangement.Querier;

        public Task<WorkItem?> AssignAndClaimAsync() => Arrangement.AssignAndClaimAsync();

        public Task<WorkReportVerdict> ReportCompletedAsync(WorkItem item) =>
            Arrangement.ReportCompletedAsync(item);

        /// <summary>Builds a fresh activated grain over the same persisted run.</summary>
        public Task<WorkflowGrain> ReactivatedAsync() =>
            WorkflowGrainContractSupport.ReactivateAsync(Arrangement, TimeProvider);

        public async Task<WorkflowRun> LoadRunAsync() =>
            await Arrangement.Store.LoadAsync(RunId) ?? throw new InvalidOperationException("run missing");
    }
}
