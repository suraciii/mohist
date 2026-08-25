using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public class ChecksParallelSpecs : WorkflowGrainSpecs
{
    public ChecksParallelSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    // MultipleChecks_PartialFailure_RetryResetsOnlyFailedCheck stays here
    // pending investigation: through the direct report path the retried
    // checks batch is not re-claimable after RetryAsync, while the
    // reconciliation-driven poll path re-offers it correctly.

    private static WorkflowDefinition MultiCheckStage(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null,
        string stage = "build")
    {
        return new WorkflowDefinition(
        [
            new StageDefinition(stage,
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
        Assert.Null(await runner.PollAsync(Services));
    }

    [Fact]
    public async Task TwoStages_EachStageDispatchesOwnChecksBatch()
    {
        var workflow = await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")]),
            new StageDefinition("build",
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
        Assert.Null(await runner.PollAsync(Services));
    }
}