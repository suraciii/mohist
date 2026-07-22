using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

/// <summary>
/// Verifies the stage-init eager invariant: every <see cref="StageStarted"/>
/// event committed by <see cref="WorkflowGrain"/> has the corresponding
/// <see cref="StageRun.Initialized"/> set to <c>true</c> on the same commit,
/// so <see cref="WorkflowRunExtensions.NextWork"/> never sees a non-initialized
/// stage and callers never observe a <c>stage-init</c> work item. This is the
/// runtime guarantee behind design D3 (stage-init eager, no
/// <c>WorkflowWork.StageInit</c> variant surfaced).
/// </summary>
[Collection("WorkflowExecution")]
public class StageInitEagerSpecs : WorkflowGrainSpecs
{
    public StageInitEagerSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Start_InitialStage_InitializedBeforePollReturnsWork()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var run = await LoadRunAsync(_workflowId!);
        var buildStage = run.Stages.Single(s => s.Id == "build");

        Assert.True(buildStage.Initialized,
            "Initial stage must be initialized in the same commit as StageStarted");

        var (task, _) = await PollWorkAnyAsync();
        Assert.StartsWith("task-1.", task.WorkId);
    }

    [Fact]
    public async Task MultiStage_AdvanceEnteringNextStage_InitializesImmediately()
    {
        await StartWorkflowAsync(TwoStages());

        var (firstTask, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, firstTask.WorkId, "completed");

        var (firstChecks, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, firstChecks, "plan-ok");

        var (buildTask, _) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", buildTask.WorkId);

        var run = await LoadRunAsync(_workflowId!);
        var buildStage = run.Stages.Single(s => s.Id == "build");
        Assert.True(buildStage.Initialized,
            "Stage transitioned into via Advance() must be initialized before its StageStarted is surfaced");
    }

    [Fact]
    public async Task EmptyLeadingStage_SkippedAndInitialized_WithoutSurfacingStageInit()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("plan", [], []),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [])
        ]));

        var (nextTask, _) = await PollWorkAnyAsync();
        Assert.StartsWith("compile.", nextTask.WorkId);

        var run = await LoadRunAsync(_workflowId!);
        Assert.All(run.Stages, s => Assert.True(s.Initialized,
            $"Stage {s.Id} must be initialized even when skipped due to emptiness"));
    }

    [Fact]
    public async Task Rerun_RestartsStage_StageInitializedInSameCommit()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: []));

        var (task1, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, task1.WorkId, "failed", "boom");

        await workflow.RerunAsync();

        var run = await LoadRunAsync(_workflowId!);
        var buildStage = run.Stages.Single(s => s.Id == "build");
        Assert.True(buildStage.Initialized,
            "Rerun must re-initialize the stage in the same commit as the new StageStarted");
        Assert.NotEmpty(buildStage.Tasks);
        Assert.Equal(TaskRunStatus.Pending, buildStage.Tasks[0].Status);
    }

    [Fact]
    public async Task Start_NextWork_DirectlyReturnsTaskOrChecks_NeverUninitializedStage()
    {
        await StartWorkflowAsync(SingleStage(
            tasks: [new("task-1", "Task 1", "spec/task")],
            checks: [new("check-1", "Check 1", "spec/check")]));

        var (work, _) = await PollWorkAnyAsync();
        Assert.NotNull(work);
        Assert.Equal("task", work.WorkType);

        var run = await LoadRunAsync(_workflowId!);
        Assert.True(run.CurrentStage().Initialized,
            "After Start, the initial stage must be initialized, so NextWork never has to return a stage-init work item");
    }

    [Fact]
    public async Task ProfileChange_DuringRunningStage_DoesNotMutateThatStage()
    {
        await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")]),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")])
        ]));

        var projectId = TestProjectId(_workflowId!);

        await SeedWorkflowTemplateAsync(_workflowId!, new WorkflowDefinition(
        [
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task"), new("extra", "Extra", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")]),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")])
        ]), projectId);

        var (task1, _) = await PollWorkAnyAsync();
        Assert.StartsWith("draft.", task1.WorkId);

        var run = await LoadRunAsync(_workflowId!);
        var planStage = run.Stages.Single(s => s.Id == "plan");
        Assert.True(planStage.Initialized);
        Assert.Single(planStage.Tasks);
        Assert.DoesNotContain(planStage.Tasks, t => t.Id.StartsWith("extra."));
    }
}
