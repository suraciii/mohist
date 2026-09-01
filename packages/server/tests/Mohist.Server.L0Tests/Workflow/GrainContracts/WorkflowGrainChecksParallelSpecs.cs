using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.GrainContracts;

/// <summary>
/// Checks-batch arbitration of the workflow run: batch failure terminality,
/// failure attribution, partial retry resetting only the failed check,
/// per-check recording of mixed results, and retry/rerun action targets.
/// Drives the real grain without a cluster (#681); single-work-item dispatch
/// and per-stage fan-out remain cluster representative proofs.
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainChecksParallelSpecs
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider TimeProvider = new(FixedTime);
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainChecksParallelSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MultipleChecks_AllPass_WorkflowCompletes()
    {
        var arrangement = await ArrangeAsync("wr-checks-all-pass");

        await CompleteFirstTaskAsync(arrangement);
        var checks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportChecksPassAsync(checks, "typecheck", "lint", "test");

        Assert.Null(await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation"));
        Assert.Equal("Completed", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task MultipleChecks_OneFails_WholeBatchFails()
    {
        var arrangement = await ArrangeAsync("wr-checks-one-fails");

        await CompleteFirstTaskAsync(arrangement);
        var checks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCheckResultsAsync(
            checks,
            ("typecheck", CheckResultStatus.Passed, null),
            ("lint", CheckResultStatus.Failed, "lint errors"),
            ("test", CheckResultStatus.Passed, null));

        Assert.Null(await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation"));
        Assert.Equal("Failed", await arrangement.Grain.GetRunStatusAsync());
    }

    [Fact]
    public async Task MultipleChecks_OneFails_FailureReportsCorrectCheck()
    {
        var arrangement = await ArrangeAsync("wr-checks-attributes-lint");

        await CompleteFirstTaskAsync(arrangement);
        var checks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCheckResultsAsync(
            checks,
            ("typecheck", CheckResultStatus.Passed, null),
            ("lint", CheckResultStatus.Failed, "unused imports"),
            ("test", CheckResultStatus.Passed, null));

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        Assert.Equal("failed", status!.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("CheckFailed", status.Failure!.Reason);
        Assert.Equal("lint", status.Failure.CheckName);
    }


    [Fact]
    public async Task MultipleChecks_AllFail_FirstFailureRecorded()
    {
        var arrangement = await ArrangeAsync("wr-checks-all-fail-first");

        await CompleteFirstTaskAsync(arrangement);
        var checks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCheckResultsAsync(
            checks,
            ("typecheck", CheckResultStatus.Failed, "type errors"),
            ("lint", CheckResultStatus.Failed, "lint errors"),
            ("test", CheckResultStatus.Failed, "test failures"));

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);
        Assert.Equal("failed", status!.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("typecheck", status.Failure!.CheckName);
    }

    [Fact]
    public async Task MultipleChecks_MixedResults_RecordsEachReportedCheck()
    {
        var arrangement = await ArrangeAsync("wr-checks-mixed-recorded");

        await CompleteFirstTaskAsync(arrangement);
        var checks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCheckResultsAsync(
            checks,
            ("typecheck", CheckResultStatus.Passed, null),
            ("lint", CheckResultStatus.Failed, "lint errors"),
            ("test", CheckResultStatus.Failed, "test failures"));

        var run = await RequireRunAsync(arrangement);
        var stage = run.Stages.Single();
        var typecheck = stage.Checks.Single(c => c.Name == "typecheck");
        var lint = stage.Checks.Single(c => c.Name == "lint");
        var test = stage.Checks.Single(c => c.Name == "test");

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(StageCheckStatus.Passed, typecheck.Status);
        Assert.Equal(StageCheckStatus.Failed, lint.Status);
        Assert.Equal("lint errors", lint.Message);
        Assert.Equal(StageCheckStatus.Failed, test.Status);
        Assert.Equal("test failures", test.Message);
        Assert.Equal("lint", run.Failure?.CheckName);
    }

    [Fact]
    public async Task MultipleChecks_FailedCheckWithRetry_RetryActionTargetsCorrectCheck()
    {
        var arrangement = await ArrangeAsync(
            "wr-checks-retry-action",
            MultiCheckStage(checks:
            [
                new("typecheck", "TypeCheck", "spec/typecheck"),
                new("lint", "Lint", "spec/lint"),
            ]));

        await CompleteFirstTaskAsync(arrangement);
        var checks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCheckResultsAsync(
            checks,
            ("lint", CheckResultStatus.Failed, "unused imports"),
            ("typecheck", CheckResultStatus.Passed, null));

        var status = await arrangement.Querier.GetStatusAsync(arrangement.RunId);
        Assert.NotNull(status);

        var retryAction = status!.AvailableActions.Find(a => a.Name == "retry");
        Assert.NotNull(retryAction);
        Assert.Equal("lint", retryAction!.Target);

        var rerunAction = status.AvailableActions.Find(a => a.Name == "rerun");
        Assert.NotNull(rerunAction);
        Assert.Equal("build", rerunAction!.Target);
    }

    [Fact]
    public async Task NoChecks_WorkflowCompletesAfterTasks()
    {
        var arrangement = await ArrangeAsync(
            "wr-checks-none",
            MultiCheckStage(checks: []));

        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(task);

        Assert.Null(await arrangement.Grain.ClaimNextAsync(arrangement.WorkerId, "test-generation"));
    }

    private Task<WorkflowGrainArrangement> ArrangeAsync(string runId, WorkflowDefinition? definition = null) =>
        WorkflowGrainArrangement.CreateAsync(_fixture, runId, definition ?? MultiCheckStage(), TimeProvider);

    /// <summary>Claims and completes the stage's single leading task.</summary>
    private static async Task CompleteFirstTaskAsync(WorkflowGrainArrangement arrangement)
    {
        var task = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(task);
    }

    private static async Task<WorkflowRun> RequireRunAsync(WorkflowGrainArrangement arrangement) =>
        await arrangement.Store.LoadAsync(arrangement.RunId) ?? throw new InvalidOperationException("run missing");

    private static WorkflowDefinition MultiCheckStage(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null,
        string stage = "build") => new(
    [
        new StageDefinition(
            stage,
            tasks ?? [new("task-1", "Task 1", "spec/task")],
            checks ?? [
                new("typecheck", "TypeCheck", "spec/typecheck"),
                new("lint", "Lint", "spec/lint"),
                new("test", "Test", "spec/test"),
            ]),
    ]);
}
