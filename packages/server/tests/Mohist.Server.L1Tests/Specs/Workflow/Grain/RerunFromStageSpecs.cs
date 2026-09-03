using Mohist.Server.Infrastructure.Data.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.L1Tests.Specs.Workflow;

namespace Mohist.Server.L1Tests.Specs.Workflow.Grain;

[Collection("WorkflowExecution")]
[Trait("level", "L1")]
public class RerunFromStageSpecs : WorkflowGrainSpecs
{
    public RerunFromStageSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RerunFromStage_SequentialStageLockInRange_Released()
    {
        var resource = $"resource-{Guid.NewGuid():N}";
        var workflow = await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")]),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")],
                LockBehavior: "sequential",
                Resources: [resource]),
        ]));

        var (firstTask, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, firstTask.WorkId, "completed");
        var (firstChecks, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, firstChecks, "plan-ok");

        var (buildTask, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, buildTask.WorkId, "completed");
        var (buildChecks, r4) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r4, buildChecks, "build-ok");

        var projectId = TestProjectId(_workflowId!);
        var lockKey = WorkflowStageLockKeys.ForProjectResource(projectId, resource);
        var lockGrain = Grains.GetGrain<IWorkflowStageLockGrain>(lockKey);
        var acquireResult = await lockGrain.AcquireSequentialAsync(
            new StageLockRequest(_workflowId!, "build", resource, projectId));
        Assert.True(acquireResult.Acquired);

        var workflowGrain = Grains.GetGrain<IWorkflowGrain>(_workflowId!);
        await workflowGrain.RerunFromStageAsync("plan");

        var stateAfter = await lockGrain.GetStateAsync();
        Assert.NotNull(stateAfter);
        Assert.Null(stateAfter!.Owner);
    }

    [Fact]
    public async Task RerunFromStage_LockBeforeTarget_NotReleased()
    {
        var resource = $"resource-{Guid.NewGuid():N}";
        var workflow = await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")],
                LockBehavior: "sequential",
                Resources: [resource]),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")]),
            new StageDefinition("integrate",
                [new("merge", "Merge", "spec/task")],
                [new("merge-ok", "Merge OK", "spec/check")]),
        ]));

        var (firstTask, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, firstTask.WorkId, "completed");
        var (firstChecks, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, firstChecks, "plan-ok");

        var (buildTask, r3) = await PollWorkAnyAsync();
        await ReportAsync(r3, buildTask.WorkId, "completed");
        var (buildChecks, r4) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r4, buildChecks, "build-ok");

        var (integrateTask, r5) = await PollWorkAnyAsync();
        await ReportAsync(r5, integrateTask.WorkId, "completed");
        var (integrateChecks, r6) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r6, integrateChecks, "merge-ok");

        var projectId = TestProjectId(_workflowId!);
        var lockKey = WorkflowStageLockKeys.ForProjectResource(projectId, resource);
        var lockGrain = Grains.GetGrain<IWorkflowStageLockGrain>(lockKey);
        var acquireResult = await lockGrain.AcquireSequentialAsync(
            new StageLockRequest(_workflowId!, "plan", resource, projectId));
        Assert.True(acquireResult.Acquired);

        var workflowGrain = Grains.GetGrain<IWorkflowGrain>(_workflowId!);
        await workflowGrain.RerunFromStageAsync("build");

        var stateAfter = await lockGrain.GetStateAsync();
        Assert.NotNull(stateAfter);
        Assert.NotNull(stateAfter!.Owner);
        Assert.Equal(_workflowId, stateAfter.Owner.WorkflowRunId);
    }

    [Fact]
    public async Task RerunFromStage_ActiveTaskInLaterStage_LockNotReleasedStateUnchanged()
    {
        var resource = $"resource-{Guid.NewGuid():N}";
        var workflow = await StartWorkflowAsync(new WorkflowDefinition(
        [
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")]),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")],
                LockBehavior: "sequential",
                Resources: [resource]),
        ]));

        var (firstTask, r1) = await PollWorkAnyAsync();
        await ReportAsync(r1, firstTask.WorkId, "completed");
        var (firstChecks, r2) = await PollWorkAnyAsync();
        await ReportChecksPassAsync(r2, firstChecks, "plan-ok");

        var (buildTask, r3) = await PollWorkAnyAsync();

        var projectId = TestProjectId(_workflowId!);
        var lockKey = WorkflowStageLockKeys.ForProjectResource(projectId, resource);
        var lockGrain = Grains.GetGrain<IWorkflowStageLockGrain>(lockKey);
        var stateBefore = await lockGrain.GetStateAsync();
        Assert.NotNull(stateBefore);
        Assert.NotNull(stateBefore!.Owner);

        var workflowGrain = Grains.GetGrain<IWorkflowGrain>(_workflowId!);
        var result = await workflowGrain.RerunFromStageAsync("plan");

        Assert.False(result.Success);
        Assert.Equal("active_work_in_range", result.Code);

        var stateAfter = await lockGrain.GetStateAsync();
        Assert.NotNull(stateAfter);
        Assert.NotNull(stateAfter!.Owner);
        Assert.Equal(_workflowId, stateAfter.Owner.WorkflowRunId);

        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }
}
