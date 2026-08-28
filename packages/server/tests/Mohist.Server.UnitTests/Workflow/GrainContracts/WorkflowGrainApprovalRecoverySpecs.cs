using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.UnitTests.Workflow.GrainContracts;

/// <summary>
/// Approval-gate and recovery-retry decisions of the workflow run, driven
/// through the real grain without a cluster. Migrates the ApprovalGateSpecs
/// and CheckRetrySpecs scenarios from the SpecTests cluster population (#681).
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainApprovalRecoverySpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainApprovalRecoverySpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApprovalStage_TasksAndChecksPass_WorkflowAwaitsApproval()
    {
        var arrangement = await ArrangeAsync("wr-gate-awaits");

        await ReportPlanAsync(arrangement);

        Assert.Null(await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation"));
        Assert.Equal("AwaitingApproval", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task AwaitingApproval_UserApprovesWithoutOperator_WorkflowContinuesToNextStage()
    {
        var arrangement = await ArrangeAsync("wr-gate-approve-noop");
        await ReportPlanAsync(arrangement);

        await arrangement.Grain.ApproveAsync();

        var run = await RequireRunAsync(arrangement);
        Assert.Null(run.Stages.Single(stage => stage.Id == "plan").ApprovalStatus!.DecidedBy);

        var compile = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(compile);
        Assert.StartsWith("compile.", compile!.Id);
        await arrangement.ReportCompletedAsync(compile);

        var buildCheck = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(buildCheck);
        Assert.StartsWith("checks-", buildCheck!.Id);
        await arrangement.ReportChecksPassAsync(buildCheck, "build-ok");
        Assert.Equal("Completed", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task AwaitingApproval_UserApproves_AssignedRunnerContinuesWorkflow()
    {
        var arrangement = await ArrangeAsync("wr-gate-approve-assigned");
        await ReportPlanAsync(arrangement);

        await arrangement.Grain.ApproveAsync("operator-1");

        // The assignment survives the approval; the assigned worker receives
        // the next stage's work while a different worker is offered nothing.
        var build = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(build);
        Assert.StartsWith("compile.", build!.Id);
    }

    [Fact]
    public async Task AwaitingApproval_LegacyReject_RoutesToFeedbackLoop_AndDoesNotFail()
    {
        var arrangement = await ArrangeAsync("wr-gate-legacy-reject");
        await ReportPlanAsync(arrangement);

#pragma warning disable CS0618
        await arrangement.Grain.RequestChangesAsync("not good enough");
#pragma warning restore CS0618

        // RequestChanges must NOT mark the workflow as failed; it routes
        // through the feedback loop with an open feedback entry.
        Assert.NotEqual(WorkflowRunStatus.Failed, (await RequireRunAsync(arrangement)).Status);
        var run = await RequireRunAsync(arrangement);
        var current = run.Stages.First(stage => stage.Id == run.CurrentStageId);
        Assert.Equal(StageRunStatus.Running, current.Status);
        Assert.NotNull(current.ApprovalStatus);
        Assert.Null(current.ApprovalStatus!.Result);
        Assert.Null(current.ApprovalStatus.DecidedBy);
        Assert.Single(run.Feedback);
        Assert.Equal("not good enough", run.Feedback[0].Body);
        Assert.Equal(ApprovalFeedbackStatus.Open, run.Feedback[0].Status);
    }

    [Fact]
    public async Task RejectedApproval_LegacyReject_NewRunResumesFromFeedbackTask()
    {
        var arrangement = await ArrangeAsync("wr-gate-reject-feedback");
        var initialRun = await RequireRunAsync(arrangement);
        Assert.Equal(1, initialRun.Stages.First(stage => stage.Id == initialRun.CurrentStageId).Attempt);

        await ReportPlanAsync(arrangement);

#pragma warning disable CS0618
        await arrangement.Grain.RequestChangesAsync("plan is too short", "operator-1");
#pragma warning restore CS0618

        var run = await RequireRunAsync(arrangement);
        var current = run.Stages.First(stage => stage.Id == run.CurrentStageId);
        Assert.Equal(1, current.Attempt);
        Assert.Equal(StageRunStatus.Running, current.Status);
        Assert.Single(run.Feedback);
        Assert.Equal("plan is too short", run.Feedback[0].Body);
        Assert.Equal(ApprovalFeedbackStatus.Open, run.Feedback[0].Status);
        Assert.Contains(current.Tasks, task => task.DefinitionId == "apply-feedback");
    }

    [Fact]
    public async Task RequestChanges_WithoutConfiguredFeedbackTasks_LeavesRunUnchanged()
    {
        var arrangement = await ArrangeAsync(
            "wr-gate-no-feedback-config",
            ApprovalStage() with { Approval = null });
        await ReportPlanAsync(arrangement);

        var before = await RequireRunAsync(arrangement);
        var beforeJson = JsonSerializer.Serialize(before);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await arrangement.Grain.RequestChangesAsync("not applicable", "operator-1"));

        var after = await RequireRunAsync(arrangement);
        Assert.Equal(beforeJson, JsonSerializer.Serialize(after));
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, after.Status);
        Assert.Equal(StageRunStatus.AwaitingApproval, after.CurrentStage().Status);
        Assert.Empty(after.Feedback);
    }

    [Theory]
    [InlineData("missing", "no bound workflow definition")]
    [InlineData("corrupt", "unreadable bound workflow definition")]
    public async Task RequestChanges_WithoutReadableBoundDefinition_LeavesRunAndEventsUnchanged(
        string snapshotKind,
        string expectedMessage)
    {
        var arrangement = await ArrangeAsync($"wr-gate-bound-feedback-{snapshotKind}");
        await ReportPlanAsync(arrangement);

        var persisted = await RequireRunAsync(arrangement);
        persisted.BoundWorkflowDefinitionJson = snapshotKind == "missing" ? null : "not-json";
        await arrangement.Store.SaveAsync(persisted);
        var grain = await WorkflowGrainContractSupport.ReactivateAsync(arrangement, TimeProvider);

        var before = await RequireRunAsync(arrangement);
        var beforeJson = JsonSerializer.Serialize(before);
        var beforeRevision = await ReadRevisionAsync(arrangement);
        var beforeEventCount = (await arrangement.Events.ListAsync(arrangement.RunId)).Count;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.RequestChangesAsync("not applicable", "operator-1"));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
        var after = await RequireRunAsync(arrangement);
        Assert.Equal(beforeJson, JsonSerializer.Serialize(after));
        Assert.Equal(beforeRevision, await ReadRevisionAsync(arrangement));
        Assert.Equal(beforeEventCount, (await arrangement.Events.ListAsync(arrangement.RunId)).Count);
    }

    [Fact]
    public async Task CheckFails_DoesNotInjectRecoveryTask()
    {
        var arrangement = await ArrangeAsync(
            "wr-recovery-check-fail",
            new WorkflowDefinition(
            [
                new StageDefinition(
                    "build",
                    [new("task-1", "Task 1", "spec/task")],
                    [new("check-1", "Check 1", "spec/check")]),
            ]));
        await ReportPlanTaskOnlyAsync(arrangement);

        var checks = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(checks);
        await arrangement.Grain.ReceiveCheckReportAsync(
            arrangement.WorkerId,
            checks!.Id!,
            new CheckReport(checks.Stage, [new CheckResult("check-1", CheckResultStatus.Failed, "no retry")]));

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        Assert.Equal("failed", status!.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("CheckFailed", status.Failure.Reason);
        Assert.Equal("check-1", status.Failure.CheckName);
        var build = Assert.Single(status.Stages);
        Assert.DoesNotContain(
            build.Tasks,
            task => task.Id.StartsWith("recover:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TaskLevelRecoveryTasks_RunBeforeRetrySelf()
    {
        var recovery = new RecoveryDefinition(
            1,
            [
                new RecoveryHandlerDefinition(
                    "error.code=script-failed",
                    [
                        new TaskDefinition("recover:fix-ci", "Fix CI verification", "spec/fix"),
                    ],
                    RetrySelf: true),
            ]);
        var arrangement = await ArrangeAsync(
            "wr-recovery-task-order",
            new WorkflowDefinition(
            [
                new StageDefinition(
                    "build",
                    [
                        new TaskDefinition("verify", "Verify", "spec/verify", Recovery: recovery),
                        new TaskDefinition("next", "Next", "spec/next"),
                    ],
                    []),
            ]));

        var verify = (await arrangement.AssignAndClaimAsync())!;
        Assert.Equal("verify.1", verify.Id);
        Assert.NotNull(verify.Recovery);

        var report = await arrangement.ReportTaskResultAsync(
            verify,
            JsonSerializer.SerializeToElement(new { errorCode = "script-failed" }),
            [
                new RuntimeTaskInput("recover:fix-ci", "Fix CI verification", "spec/fix"),
                new RuntimeTaskInput("verify", "Verify", "spec/verify", Recovery: recovery, RecoveryRemaining: 0),
            ]);
        Assert.Equal(WorkReportVerdict.Accepted, report);

        var fix = (await arrangement.AssignAndClaimAsync())!;
        Assert.Equal("recover:fix-ci.1", fix.Id);

        var retry = (await arrangement.AssignAndClaimAsync())!;
        Assert.Equal("verify.2", retry.Id);
    }

    private async Task<WorkflowGrainArrangement> ArrangeAsync(string runId, WorkflowDefinition? definition = null) =>
        await WorkflowGrainArrangement.CreateAsync(_fixture, runId, definition ?? ApprovalStage(), TimeProvider);

    /// <summary>Drives the approval stage's work and check to the gate.</summary>
    private static async Task ReportPlanAsync(WorkflowGrainArrangement arrangement)
    {
        await ReportPlanTaskOnlyAsync(arrangement);
        var check = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(check);
        Assert.StartsWith("checks-", check!.Id);
        await arrangement.ReportChecksPassAsync(check, "plan-ok");
    }

    private static async Task ReportPlanTaskOnlyAsync(WorkflowGrainArrangement arrangement)
    {
        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(task);
    }

    private static async Task<WorkflowRun> RequireRunAsync(WorkflowGrainArrangement arrangement) =>
        await arrangement.Store.LoadAsync(arrangement.RunId) ?? throw new InvalidOperationException("run missing");

    private static async Task<long> ReadRevisionAsync(WorkflowGrainArrangement arrangement)
    {
        var factory = arrangement.Services.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.WorkflowRuns.FindAsync([arrangement.RunId]);
        Assert.NotNull(row);
        return db.Entry(row!).Property<long>("ETag").CurrentValue;
    }

    private static WorkflowDefinition ApprovalStage() => new(
    [
        new StageDefinition(
            "plan",
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")],
            RequiresApproval: true),
        new StageDefinition(
            "build",
            [new("compile", "Compile", "spec/task")],
            [new("build-ok", "Build OK", "spec/check")]),
    ],
    Approval: new ApprovalConfig(new ApprovalFeedbackConfig([
        new TaskDefinition("apply-feedback", "Apply approval feedback", "spec/task")
    ])));
}
