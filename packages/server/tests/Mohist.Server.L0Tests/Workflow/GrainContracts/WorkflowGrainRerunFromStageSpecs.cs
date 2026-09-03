using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.L0Tests.Workflow.GrainContracts;

/// <summary>
/// Rerun-from-stage arbitration on the real grain without a cluster: the
/// target stage initializes in the same commit while later stages stay lazy,
/// active work in range fences the request, and run variables survive with
/// new attempts re-rendering through the production translation seam (#681).
/// The sequential-lock interaction scenarios stay on the cluster: they
 /// observe a second grain (the stage lock) across the rerun boundary.
/// </summary>
[Collection("MohistDb")]
[Trait("level", "L0")]
public sealed class WorkflowGrainRerunFromStageSpecs
{
    private static readonly FakeTimeProvider TimeProvider =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly MohistDbFixture _fixture;

    public WorkflowGrainRerunFromStageSpecs(MohistDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RerunFromStage_TargetStageInitializedInSameCommit()
    {
        var arrangement = await ArrangeAsync("wr-rfs-init-same-commit");
        await CompleteAllStagesAsync(arrangement);

        await arrangement.Grain.RerunFromStageAsync("plan");

        var run = await arrangement.Store.LoadAsync(arrangement.RunId) ?? throw new InvalidOperationException("run missing");
        var planStage = run.Stages.Single(stage => stage.Id == "plan");
        Assert.True(planStage.Initialized,
            "Target stage must be initialized in the same commit as StageStarted");
        Assert.NotEmpty(planStage.Tasks);
        Assert.Equal(WorkflowActionAttemptStatus.Pending, planStage.Tasks[0].Status);
    }

    [Fact]
    public async Task RerunFromStage_LaterStageLazilyInitializedOnAdvance()
    {
        var arrangement = await ArrangeAsync("wr-rfs-lazy-later");
        await CompleteAllStagesAsync(arrangement);

        await arrangement.Grain.RerunFromStageAsync("plan");

        var run = await arrangement.Store.LoadAsync(arrangement.RunId) ?? throw new InvalidOperationException("run missing");

        var planStage = run.Stages.Single(stage => stage.Id == "plan");
        Assert.True(planStage.Initialized);

        var buildStage = run.Stages.Single(stage => stage.Id == "build");
        Assert.False(buildStage.Initialized,
            "Later stage must NOT be initialized in the same commit; will init when advance reaches it");
        Assert.Empty(buildStage.Tasks);
        Assert.Equal(StageRunStatus.Pending, buildStage.Status);
    }

    [Fact]
    public async Task RerunFromStage_ActiveTaskInLaterStage_Rejected()
    {
        var arrangement = await ArrangeAsync("wr-rfs-active-fence");
        var firstTask = await CompleteFirstStageAsync(arrangement);

        // The build stage's task is claimed but still running.
        var buildTask = (await arrangement.AssignAndClaimAsync())!;
        Assert.NotEqual(firstTask.Id, buildTask.Id);

        var result = await arrangement.Grain.RerunFromStageAsync("plan");

        Assert.False(result.Success);
        Assert.Equal("active_work_in_range", result.Code);
    }

    [Fact]
    public async Task RerunFromStage_RuntimeVariablesPreservedAndReadableByNewAttempt()
    {
        var arrangement = await ArrangeAsync(
            "wr-rfs-variables",
            TwoStages(buildWith: With("""
                {"answer":"${{ vars.answer }}"}
                """)));
        await CompleteAllStagesAsync(arrangement);

        var runVariablesStore = new WorkflowRunVariablesStore(new PooledDbContextFactory<MohistDbContext>(
            new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(_fixture.ConnectionString)
                .Options));
        await runVariablesStore.PatchVariablesAsync(arrangement.RunId, new VariableBundle(
            Vars: JsonDocument.Parse("{\"answer\":42}").RootElement.Clone()));

        await arrangement.Grain.RerunFromStageAsync("build");

        var preserved = await runVariablesStore.GetVariablesAsync(arrangement.RunId);
        Assert.NotNull(preserved.Vars);
        Assert.Equal(42, preserved.Vars.Value.GetProperty("answer").GetInt32());

        var item = (await arrangement.AssignAndClaimAsync())!;
        var dispatch = await ToDispatchAsync(arrangement, item);
        Assert.NotNull(dispatch.With);
        using var withDoc = JsonDocument.Parse(dispatch.With!);
        Assert.Equal("${{ vars.answer }}", withDoc.RootElement.GetProperty("answer").GetString());
        Assert.NotNull(dispatch.Variables);
        using var varsDoc = JsonDocument.Parse(dispatch.Variables!);
        Assert.Equal(42, varsDoc.RootElement.GetProperty("vars").GetProperty("answer").GetInt32());
    }

    private async Task<WorkflowGrainArrangement> ArrangeAsync(string runId, WorkflowDefinition? definition = null)
    {
        var arrangement = await WorkflowGrainArrangement.CreateAsync(
            _fixture,
            runId,
            definition ?? TwoStages(),
            TimeProvider,
            workerId: $"runner-{runId}");
        return arrangement;
    }

    /// <summary>Completes plan and build (tasks + checks) to a terminal run.</summary>
    private async Task<WorkItem> CompleteAllStagesAsync(WorkflowGrainArrangement arrangement)
    {
        var last = await CompleteFirstStageAsync(arrangement);

        var buildTask = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("compile.", buildTask.Id);
        await arrangement.ReportCompletedAsync(buildTask);
        var buildChecks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportChecksPassAsync(buildChecks, "build-ok");
        return last;
    }

    /// <summary>Completes the plan stage (task + checks), returns its task item.</summary>
    private async Task<WorkItem> CompleteFirstStageAsync(WorkflowGrainArrangement arrangement)
    {
        var firstTask = (await arrangement.AssignAndClaimAsync())!;
        Assert.StartsWith("draft.", firstTask.Id);
        await arrangement.ReportCompletedAsync(firstTask);
        var firstChecks = (await arrangement.AssignAndClaimAsync())!;
        await arrangement.ReportChecksPassAsync(firstChecks, "plan-ok");
        return firstTask;
    }

    private static async Task<WorkDispatch> ToDispatchAsync(
        WorkflowGrainArrangement arrangement,
        WorkItem item)
    {
        var run = await arrangement.Store.LoadAsync(arrangement.RunId)
            ?? throw new InvalidOperationException("run missing");
        return await arrangement.Translator.TranslateToDispatchAsync(
            item, arrangement.RunId, run, arrangement.WorkerId);
    }

    private static WorkflowDefinition TwoStages(Dictionary<string, JsonElement?>? buildWith = null) => new(
    [
        new StageDefinition("plan",
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")]),
        new StageDefinition("build",
            [new("compile", "Compile", "spec/task", buildWith)],
            [new("build-ok", "Build OK", "spec/check")]),
    ]);

    private static Dictionary<string, JsonElement?> With(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(json)!;
}
