using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class ChecksParallelSpecs : WorkflowGrainSpecs
{
    public ChecksParallelSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private static WorkflowDefinitionInput MultiCheckStage(
        List<TaskDefinitionInput>? tasks = null,
        List<CheckDefinitionInput>? checks = null,
        string stage = "build")
    {
        return new WorkflowDefinitionInput(
        [
            new StageDefinitionInput(stage,
                tasks ?? [new("task-1", "Task 1", "spec/task")],
                checks ?? [
                    new("typecheck", "TypeCheck", "spec/typecheck"),
                    new("lint", "Lint", "spec/lint"),
                    new("test", "Test", "spec/test")
                ])
        ]);
    }

    [Fact]
    public async Task MultipleChecks_DispatchedAsSingleWorkItem()
    {
        await StartWorkflowAsync(MultiCheckStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks.WorkType);
        Assert.StartsWith("checks-", checks.WorkId);
        Assert.NotNull(checks.With);

        var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(checks.With!);
        Assert.True(parsed.TryGetProperty("checks", out var checksArr));
        Assert.Equal(3, checksArr.GetArrayLength());
    }

    [Fact]
    public async Task MultipleChecks_AllPass_WorkflowCompletes()
    {
        await StartWorkflowAsync(MultiCheckStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, checks, "typecheck", "lint", "test");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task MultipleChecks_OneFails_WholeBatchFails()
    {
        await StartWorkflowAsync(MultiCheckStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks, "lint", "lint errors", "typecheck", "test");

        var runner = Grains.GetGrain<IRunnerGrain>(r2);
        Assert.Null(await runner.PollAsync());

        var status = await (await StartWorkflowAsync(MultiCheckStage())).GetStatusAsync();
    }

    [Fact]
    public async Task MultipleChecks_OneFails_FailureReportsCorrectCheck()
    {
        var workflow = await StartWorkflowAsync(MultiCheckStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks, "lint", "unused imports", "typecheck");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("CheckUnrepaired", status.Failure.Reason);
        Assert.Equal("lint", status.Failure.CheckName);
    }

    [Fact]
    public async Task MultipleChecks_PartialFailure_RetryResetsOnlyFailedCheck()
    {
        var workflow = await StartWorkflowAsync(MultiCheckStage(
            checks: [
                new("typecheck", "TypeCheck", "spec/typecheck"),
                new("lint", "Lint", "spec/lint")
            ]));

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks, "lint", "lint errors", "typecheck");

        await workflow.RetryAsync();

        var (retried, r3) = await PollWorkAnyAsync();
        Assert.Equal("checks", retried.WorkType);

        var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(retried.With!);
        Assert.True(parsed.TryGetProperty("checks", out var checksArr));
        Assert.Equal(1, checksArr.GetArrayLength());

        await ReportChecksPassAsync(r3, retried, "lint");

        var runner = Grains.GetGrain<IRunnerGrain>(r3);
        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task MultipleChecks_AllFail_FirstFailureRecorded()
    {
        var workflow = await StartWorkflowAsync(MultiCheckStage());

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksAsync(r2, checks,
            ("typecheck", "fail", "type errors"),
            ("lint", "fail", "lint errors"),
            ("test", "fail", "test failures"));

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
        Assert.NotNull(status.Failure);
        Assert.Equal("typecheck", status.Failure.CheckName);
    }

    [Fact]
    public async Task MultipleChecks_FailedCheckHasRetry_RepairTaskInjectedThenCheckReRun()
    {
        var workflow = await StartWorkflowAsync(MultiCheckStage(
            checks: [
                new("typecheck", "TypeCheck", "spec/typecheck"),
                new("lint", "Lint", "spec/lint",
                    RetryLimit: 2,
                    RetryTask: new("fix-lint", "Fix lint", "spec/fix-lint"))
            ]));

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks, "lint", "unused imports", "typecheck");

        var (repair, r3) = await PollWorkAnyAsync();
        Assert.Equal("task", repair.WorkType);

        await ReportAsync(r3, repair.WorkId, "completed");

        var (recheck, r4) = await PollWorkAnyAsync();
        Assert.Equal("checks", recheck.WorkType);

        var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(recheck.With!);
        Assert.True(parsed.TryGetProperty("checks", out var checksArr));
        Assert.Equal(1, checksArr.GetArrayLength());

        await ReportChecksPassAsync(r4, recheck, "lint");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task MultipleChecks_FailedCheckWithRetry_RetryActionTargetsCorrectCheck()
    {
        var workflow = await StartWorkflowAsync(MultiCheckStage(
            checks: [
                new("typecheck", "TypeCheck", "spec/typecheck"),
                new("lint", "Lint", "spec/lint")
            ]));

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var (checks, r2) = await PollWorkAnyAsync();
        await ReportChecksFailAsync(r2, checks, "lint", "unused imports", "typecheck");

        var status = await workflow.GetStatusAsync();
        Assert.NotNull(status);

        var retryAction = status.AvailableActions.Find(a => a.Name == "retry");
        Assert.NotNull(retryAction);
        Assert.Equal("lint", retryAction.Target);

        var rerunAction = status.AvailableActions.Find(a => a.Name == "rerun");
        Assert.NotNull(rerunAction);
        Assert.Equal("build", rerunAction.Target);
    }

    [Fact]
    public async Task NoChecks_WorkflowCompletesAfterTasks()
    {
        var workflow = await StartWorkflowAsync(MultiCheckStage(
            checks: []));

        var (task, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task.WorkId, "completed");

        var runner = Grains.GetGrain<IRunnerGrain>(r1);
        Assert.Null(await runner.PollAsync());
    }

    [Fact]
    public async Task TwoStages_EachStageDispatchesOwnChecksBatch()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinitionInput(
        [
            new StageDefinitionInput("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")]),
            new StageDefinitionInput("build",
                [new("compile", "Compile", "spec/task")],
                [new("typecheck", "TypeCheck", "spec/typecheck"), new("test", "Test", "spec/test")])
        ]));

        var (task1, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task1.WorkId, "completed");

        var (checks1, r2) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks1.WorkType);
        Assert.Equal("plan", checks1.Stage);
        await ReportChecksPassAsync(r2, checks1, "plan-ok");

        var (task2, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, task2.WorkId, "completed");

        var (checks2, r4) = await PollWorkAnyAsync();
        Assert.Equal("checks", checks2.WorkType);
        Assert.Equal("build", checks2.Stage);

        var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(checks2.With!);
        Assert.True(parsed.TryGetProperty("checks", out var checksArr));
        Assert.Equal(2, checksArr.GetArrayLength());

        await ReportChecksPassAsync(r4, checks2, "typecheck", "test");

        var runner = Grains.GetGrain<IRunnerGrain>(r4);
        Assert.Null(await runner.PollAsync());
    }
}
