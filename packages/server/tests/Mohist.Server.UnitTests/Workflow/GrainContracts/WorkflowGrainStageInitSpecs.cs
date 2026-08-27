using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.GrainContracts;

/// <summary>
/// The stage-init eager invariant: every StageStarted commit carries
/// Initialized=true for that stage, so work claiming never observes (or
/// surfaces) an uninitialized stage. Migrates the SpecTests
/// StageInitEagerSpecs scenarios to direct grain construction (#681).
/// </summary>
[Collection("MohistDb")]
public sealed class WorkflowGrainStageInitSpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainStageInitSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Start_InitialStage_InitializedBeforeWorkIsClaimable()
    {
        var arrangement = await ArrangeAsync(
            "wr-stageinit-start",
            SingleStageWithCheck());

        var run = await RequireRunAsync(arrangement);
        var buildStage = run.Stages.Single(stage => stage.Id == "build");
        Assert.True(
            buildStage.Initialized,
            "Initial stage must be initialized in the same commit as StageStarted");

        var claimed = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(claimed);
        Assert.StartsWith("task-1.", claimed!.Id);
    }

    [Fact]
    public async Task MultiStage_AdvanceEnteringNextStage_InitializesImmediately()
    {
        var arrangement = await ArrangeAsync("wr-stageinit-advance", PlanThenBuild());
        await ReportPlanThroughCheckAsync(arrangement);

        var buildTask = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("compile.", buildTask!.Id);

        var run = await RequireRunAsync(arrangement);
        var buildStage = run.Stages.Single(stage => stage.Id == "build");
        Assert.True(
            buildStage.Initialized,
            "Stage transitioned into via Advance() must be initialized before its StageStarted is surfaced");
    }

    [Fact]
    public async Task EmptyLeadingStage_SkippedAndInitialized_WithoutSurfacingStageInit()
    {
        var arrangement = await ArrangeAsync(
            "wr-stageinit-empty-lead",
            new WorkflowDefinition(
            [
                new StageDefinition("plan", [], []),
                new StageDefinition("build", [new("compile", "Compile", "spec/task")], []),
            ]));

        var nextTask = await arrangement.AssignAndClaimAsync();
        Assert.StartsWith("compile.", nextTask!.Id);

        var run = await RequireRunAsync(arrangement);
        Assert.All(
            run.Stages,
            stage => Assert.True(
                stage.Initialized,
                $"Stage {stage.Id} must be initialized even when skipped due to emptiness"));
    }

    [Fact]
    public async Task Rerun_RestartsStage_StageInitializedInSameCommit()
    {
        var arrangement = await ArrangeAsync(
            "wr-stageinit-rerun",
            SingleStage(checks: false));

        var task1 = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportFailedAsync(task1, "boom");

        await arrangement.Grain.RerunAsync();

        var run = await RequireRunAsync(arrangement);
        var buildStage = run.Stages.Single(stage => stage.Id == "build");
        Assert.True(
            buildStage.Initialized,
            "Rerun must re-initialize the stage in the same commit as the new StageStarted");
        Assert.NotEmpty(buildStage.Tasks);
        Assert.Equal(WorkflowActionAttemptStatus.Pending, buildStage.Tasks[0].Status);
    }

    [Fact]
    public async Task Start_NextWork_DirectlyReturnsTask_NeverAnUninitializedStageItem()
    {
        var arrangement = await ArrangeAsync("wr-stageinit-direct-work");

        var work = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(work);
        Assert.Equal("task", work!.WorkType);

        var run = await RequireRunAsync(arrangement);
        Assert.True(
            run.CurrentStage().Initialized,
            "After Start, the initial stage must be initialized, so NextWork never has to return a stage-init work item");
    }

    [Fact]
    public async Task ProfileChange_DuringRunningStage_DoesNotMutateThatStage()
    {
        const string runId = "wr-stageinit-profile-change";
        const string projectId = "proj-wr-stageinit-profile-change";
        var original = PlanThenBuild();
        await WorkflowGrainContractSupport.SeedTemplateAsync(_fixture, projectId, original, Fixed);
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture, runId, original, TimeProvider, projectId: projectId);

        // Re-seed the project's template while the plan stage is already
        // running; the running stage keeps the definition it started with.
        await WorkflowGrainContractSupport.SeedTemplateAsync(_fixture, projectId, PlanThenBuildWithExtraTask(), Fixed);

        var task1 = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(task1);
        Assert.StartsWith("draft.", task1!.Id);

        var run = await arrangement.Store.LoadAsync(runId) ?? throw new InvalidOperationException("run missing");
        var planStage = run.Stages.Single(stage => stage.Id == "plan");
        Assert.True(planStage.Initialized);
        Assert.Single(planStage.Tasks);
        Assert.DoesNotContain(planStage.Tasks, task => task.Id.StartsWith("extra."));
    }

    private static readonly DateTimeOffset Fixed =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<WorkflowRun> RequireRunAsync(WorkflowGrainArrangement arrangement) =>
        await arrangement.Store.LoadAsync(arrangement.RunId) ?? throw new InvalidOperationException("run missing");

    private Task<WorkflowGrainArrangement> ArrangeAsync(string runId, WorkflowDefinition? definition = null) =>
        WorkflowGrainArrangement.CreateAsync(_fixture, runId, definition ?? SingleStageWithCheck(), TimeProvider);

    /// <summary>Drives the plan stage through task and check into the next stage.</summary>
    private static async Task ReportPlanThroughCheckAsync(WorkflowGrainArrangement arrangement)
    {
        var draft = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportCompletedAsync(draft);
        var check = await arrangement.AssignAndClaimAsync();
        Assert.NotNull(check);
        await arrangement.ReportChecksPassAsync(check!, "plan-ok");
    }

    private static WorkflowDefinition SingleStage(bool checks) => new(
    [
        new StageDefinition(
            "build",
            [new("task-1", "Task 1", "spec/task")],
            checks ? [new("check-1", "Check 1", "spec/check")] : []),
    ]);

    private static WorkflowDefinition SingleStageWithCheck() => SingleStage(checks: true);

    private static WorkflowDefinition PlanThenBuild() => new(
    [
        new StageDefinition(
            "plan",
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")]),
        new StageDefinition("build", [new("compile", "Compile", "spec/task")], []),
    ]);

    private static WorkflowDefinition PlanThenBuildWithExtraTask() => new(
    [
        new StageDefinition(
            "plan",
            [new("draft", "Draft", "spec/task"), new("extra", "Extra", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")]),
        new StageDefinition("build", [new("compile", "Compile", "spec/task")], []),
    ]);
}
