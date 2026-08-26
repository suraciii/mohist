using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.GrainContracts;

/// <summary>
/// Stage-advance decisions of the workflow run — approval gating, empty-stage
/// skipping, auto-advance, and task/check failure terminality — driven through
/// the real grain without a cluster. Migrates the AdvanceSpecs, BoundarySpecs,
/// and FailureSpecs scenarios from the SpecTests cluster population (#681).
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainStageAdvanceSpecs
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider TimeProvider = new(FixedTime);
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainStageAdvanceSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ApprovalStage_CompletesWork_WaitsForApproval()
    {
        var arrangement = await ArrangeAsync(
            "wr-advance-approval-wait",
            PlanThenBuild(approvalRequired: true));

        await ReportPlanAsync(arrangement);

        Assert.Null(await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation"));
        Assert.Equal("AwaitingApproval", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task NonApprovalStage_CompletesWork_AutoAdvancesToNextStage()
    {
        var arrangement = await ArrangeAsync(
            "wr-advance-auto",
            PlanThenBuild(approvalRequired: false));

        await ReportPlanAsync(arrangement);

        var compile = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(compile);
        Assert.StartsWith("compile.", compile!.Id);
        await arrangement.ReportCompletedAsync(compile);
        Assert.Equal("Completed", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task EmptyFirstStage_SkipsToNextStage()
    {
        var arrangement = await ArrangeAsync(
            "wr-advance-empty-skip",
            new WorkflowDefinition(
            [
                new StageDefinition("plan", [], []),
                new StageDefinition("build", [new("compile", "Compile", "spec/task")], []),
            ]));

        var work = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(work);
        Assert.StartsWith("compile.", work!.Id);
    }

    [Fact]
    public async Task EmptyApprovalStage_UserApproves_AdvancesToNextStage()
    {
        var arrangement = await ArrangeAsync(
            "wr-advance-empty-approval",
            new WorkflowDefinition(
            [
                new StageDefinition("plan", [], [], RequiresApproval: true),
                new StageDefinition("build", [new("compile", "Compile", "spec/task")], []),
            ]));

        // Nothing is dispatchable before the approval decision.
        Assert.Equal(
            WorkflowAssignmentStatus.Rejected,
            (await arrangement.Grain.AssignWorkerAsync(arrangement.WorkerId)).Status);

        await arrangement.Grain.ApproveAsync("operator-1");

        var work = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(work);
        Assert.StartsWith("compile.", work!.Id);
    }

    [Fact]
    public async Task EmptyStage_NoTasksOrChecks_WorkflowCompletes()
    {
        var arrangement = await ArrangeAsync(
            "wr-advance-empty-complete",
            new WorkflowDefinition([new StageDefinition("build", [], [])]));

        Assert.Null(await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation"));
        Assert.Equal("Completed", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task CheckReportsPending_CheckRunsAgain()
    {
        var arrangement = await ArrangeAsync("wr-advance-check-pending");

        await ReportPlanTaskAsync(arrangement);

        var check = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(check);
        Assert.True(check!.IsChecks);
        await arrangement.Grain.ReceiveCheckReportAsync(
            arrangement.WorkerId,
            check.Id!,
            new CheckReport(check.Stage, [new CheckResult("check-1", CheckResultStatus.Pending)]));

        var reoffered = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(reoffered);
        Assert.StartsWith("checks-", reoffered!.Id);

        await arrangement.ReportChecksPassAsync(reoffered, "check-1");
        Assert.Equal("Completed", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task UnknownWorkReport_IsIgnoredAndCurrentWorkContinues()
    {
        var arrangement = await ArrangeAsync("wr-advance-unknown-report");
        var claimed = (await arrangement.AssignAndClaimAsync())!;

        var stale = await arrangement.ReportUnknownWorkAsync("unknown-work");
        Assert.Equal(ReportAck.Stale, stale);
        Assert.Equal(claimed.Id, await arrangement.Grain.GetCurrentWorkIdAsync());

        await arrangement.ReportCompletedAsync(claimed);
        var check = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(check);
        await arrangement.ReportChecksPassAsync(check!, "check-1");
        Assert.Equal("Completed", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task RunningTask_ReportsFailure_WorkflowFails()
    {
        var arrangement = await ArrangeAsync("wr-advance-task-failure");
        var claimed = (await arrangement.AssignAndClaimAsync())!;

        await arrangement.ReportFailedAsync(claimed, "compile error");

        Assert.Equal("Failed", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task RunningCheck_ReportsFail_WorkflowFails()
    {
        var arrangement = await ArrangeAsync("wr-advance-check-failure");

        await ReportPlanTaskAsync(arrangement);
        var check = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(check);

        await arrangement.Grain.ReceiveCheckReportAsync(
            arrangement.WorkerId,
            check!.Id!,
            new CheckReport(check.Stage, [new CheckResult("check-1", CheckResultStatus.Failed, "typecheck errors")]));

        Assert.Equal("Failed", await arrangement.Grain.GetRunStatusAsync());
    }

    private async Task<WorkflowGrainArrangement> ArrangeAsync(string runId, WorkflowDefinition? definition = null) =>
        await WorkflowGrainArrangement.CreateAsync(_fixture, runId, definition ?? SingleStage(), TimeProvider);

    /// <summary>Drives the plan stage to its approval gate or completion.</summary>
    private static async Task ReportPlanAsync(WorkflowGrainArrangement arrangement)
    {
        await ReportPlanTaskAsync(arrangement);
        var check = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(check);
        Assert.StartsWith("checks-", check!.Id);
        await arrangement.ReportChecksPassAsync(check, "review");
    }

    private static async Task ReportPlanTaskAsync(WorkflowGrainArrangement arrangement)
    {
        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(task);
    }

    private static WorkflowDefinition SingleStage() => new(
    [
        new StageDefinition(
            "build",
            [new("task-1", "Task 1", "spec/task")],
            [new("check-1", "Check 1", "spec/check")]),
    ]);

    private static WorkflowDefinition PlanThenBuild(bool approvalRequired) => new(
    [
        new StageDefinition(
            "plan",
            [new("draft", "Draft", "spec/task")],
            [new("review", "Review", "spec/check")],
            RequiresApproval: approvalRequired),
        new StageDefinition("build", [new("compile", "Compile", "spec/task")], []),
    ]);
}
