using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow;

/// <summary>
/// Acceptance coverage for issue 625 T-001:
///
/// - <c>BindWorkflowRun</c> serializes the complete effective
///   <c>WorkflowDefinition</c> into <c>BoundWorkflowStart.DefinitionJson</c>
///   and persists it as the write-once
///   <c>WorkflowRun.BoundWorkflowDefinitionJson</c> field.
/// - Stage initialization and lock resolution read the snapshot, never the
///   current profile provider.
/// - Pre-snapshot runs (no snapshot) are explicit legacy mode and use the
///   retained pre-change aggregate definition for affected built-in profiles
///   without synthesizing lane state.
/// - Mixed-version rollouts: a run bound while build contains aggregate
///   verify materializes the aggregate task after a profile edit; a run
///   bound with six lanes stays authoritative after a profile edit.
/// - Verification reports classify as pass/fail/timeout at the boundary
///   and persist lane outcomes on the same commit.
/// - Build-stage status projects all six lanes for lane-enabled runs and
///   leaves legacy runs without lane projection.
/// - Ordered dispatch only exposes the first non-passing lane; later lanes
///   are blocked while predecessors are pending.
/// - The build stage cannot advance while any required lane is missing.
/// </summary>
[Collection("WorkflowExecution")]
public class VerificationLaneBindingSpecs : WorkflowGrainSpecs
{
    public VerificationLaneBindingSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Bind_PersistsDefinitionJson_SnapshotOnRun()
    {
        var workflow = await CreateWorkflowAsync();
        var projectId = TestProjectId(_workflowId!);

        // Seed a definition with the six-lane shape.
        var sixLane = BuildSixLaneDefinition();
        var definitionJson = WorkflowYamlSerializer.ToJson(sixLane);
        await SeedWorkflowTemplateAsync(_workflowId!, sixLane, projectId);

        var coordinator = Grains.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(projectId);
        var result = await coordinator.BindWorkflowRunAsync(
            new WorkflowProfileCommandPayload.BindWorkflowRun(
                ProjectId: projectId,
                WorkflowRunId: _workflowId!,
                IssueNumber: 1,
                EpicNumber: null,
                ExplicitProfileId: null,
                Metadata: TestInput(projectId).Metadata!),
            $"cmd-{Guid.NewGuid():N}",
            expectedRevision: null);

        Assert.True(result.IsApplied);
        Assert.Equal(definitionJson, result.Binding?.DefinitionJson);

        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(definitionJson, run.BoundWorkflowDefinitionJson);
    }

    [Fact]
    public async Task MixedVersion_RunBoundWithAggregate_PersistsAggregateAfterProfileEdit()
    {
        var workflow = await CreateWorkflowAsync();
        var projectId = TestProjectId(_workflowId!);

        // 1. Bind while the profile still contains the aggregate verify task.
        var aggregateDefinition = new WorkflowDefinition(new[]
        {
            new StageDefinition("build", new[]
            {
                new TaskDefinition("verify", "Verify", "core/script", new Dictionary<string, JsonElement?>
                {
                    ["timeout"] = JsonDocument.Parse("300000").RootElement.Clone(),
                }),
            }, Array.Empty<CheckDefinition>()),
        });
        await SeedWorkflowTemplateAsync(_workflowId!, aggregateDefinition, projectId);
        var coordinator = Grains.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(projectId);
        await coordinator.BindWorkflowRunAsync(
            new WorkflowProfileCommandPayload.BindWorkflowRun(
                ProjectId: projectId,
                WorkflowRunId: _workflowId!,
                IssueNumber: 1,
                EpicNumber: null,
                ExplicitProfileId: null,
                Metadata: TestInput(projectId).Metadata!),
            $"cmd-{Guid.NewGuid():N}",
            expectedRevision: null);

        // 2. Replace the profile with the six-lane shape BEFORE the build
        //    stage is initialized (before any task starts).
        var sixLane = BuildSixLaneDefinition();
        await SeedWorkflowTemplateAsync(_workflowId!, sixLane, projectId);

        // 3. Start build and observe that the snapshot is authoritative.
        await workflow.StartAsync(TestInput(projectId));

        var run = await LoadRunAsync(_workflowId!);
        var buildStage = run.Stages.Single(s => s.Id == "build");
        Assert.True(buildStage.Initialized);
        // The run must still materialize the aggregate verify task because
        // it was bound while the profile still had it. The profile edit
        // cannot retroactively change this run.
        var verify = Assert.Single(buildStage.Tasks);
        Assert.Equal("verify", verify.DefinitionId);
        Assert.False(VerificationLaneGate.IsLaneEnabledRun(run));
    }

    [Fact]
    public async Task MixedVersion_RunBoundWithSixLanes_PersistsSixLanesAfterProfileEdit()
    {
        var workflow = await CreateWorkflowAsync();
        var projectId = TestProjectId(_workflowId!);

        // 1. Bind with the six-lane shape (profile activation ahead of run).
        var sixLane = BuildSixLaneDefinition();
        await SeedWorkflowTemplateAsync(_workflowId!, sixLane, projectId);
        var coordinator = Grains.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(projectId);
        await coordinator.BindWorkflowRunAsync(
            new WorkflowProfileCommandPayload.BindWorkflowRun(
                ProjectId: projectId,
                WorkflowRunId: _workflowId!,
                IssueNumber: 1,
                EpicNumber: null,
                ExplicitProfileId: null,
                Metadata: TestInput(projectId).Metadata!),
            $"cmd-{Guid.NewGuid():N}",
            expectedRevision: null);

        // 2. Edit the profile before build is initialized (e.g. add a new
        //    task to the build stage).
        var edited = BuildSixLaneDefinition(extraBuildTask: true);
        await SeedWorkflowTemplateAsync(_workflowId!, edited, projectId);

        // 3. Start build and verify the snapshot keeps the six-lane shape.
        await workflow.StartAsync(TestInput(projectId));

        var run = await LoadRunAsync(_workflowId!);
        var buildStage = run.Stages.Single(s => s.Id == "build");
        Assert.True(buildStage.Initialized);
        Assert.Equal(VerificationLaneCatalog.LaneIds.Count, buildStage.Tasks.Count);
        Assert.True(VerificationLaneGate.IsLaneEnabledRun(run));
    }

    [Fact]
    public async Task LaneEnabledRun_PendingLanesProjectedInStatus()
    {
        var workflow = await CreateWorkflowAsync();
        var projectId = TestProjectId(_workflowId!);

        var sixLane = BuildSixLaneDefinition();
        await SeedWorkflowTemplateAsync(_workflowId!, sixLane, projectId);
        await workflow.StartAsync(TestInput(projectId));

        var run = await LoadRunAsync(_workflowId!);
        Assert.True(VerificationLaneGate.IsLaneEnabledRun(run));

        var lanes = WorkflowBoundDefinitionResolver.CollectLaneAttempts(run);
        Assert.Equal(VerificationLaneCatalog.LaneIds.Count, lanes.Count);
        Assert.All(lanes, attempt => Assert.Equal(VerificationLaneOutcome.Pending, attempt.Outcome));
        foreach (var attempt in lanes)
            Assert.Equal(VerificationLaneCatalog.OrderOf(attempt.LaneId), attempt.Order);
    }

    [Fact]
    public async Task LaneEnabledRun_AllPasses_BuildGateOpens()
    {
        var workflow = await CreateWorkflowAsync();
        var projectId = TestProjectId(_workflowId!);

        var sixLane = BuildSixLaneDefinition();
        await SeedWorkflowTemplateAsync(_workflowId!, sixLane, projectId);
        await workflow.StartAsync(TestInput(projectId));

        var run = await LoadRunAsync(_workflowId!);
        var stage = run.Stages[0];
        for (var i = 0; i < VerificationLaneCatalog.LaneIds.Count; i++)
        {
            stage.Tasks[i].Status = TaskRunStatus.Completed;
            stage.Tasks[i].Lane = stage.Tasks[i].Lane! with { Outcome = VerificationLaneOutcome.Pass };
        }

        Assert.True(VerificationLaneGate.CanAdvanceBuildStage(run));
    }

    [Fact]
    public async Task LaneEnabledRun_AnyLanePending_GateKeepsClosed()
    {
        var workflow = await CreateWorkflowAsync();
        var projectId = TestProjectId(_workflowId!);

        var sixLane = BuildSixLaneDefinition();
        await SeedWorkflowTemplateAsync(_workflowId!, sixLane, projectId);
        await workflow.StartAsync(TestInput(projectId));

        var run = await LoadRunAsync(_workflowId!);
        var stage = run.Stages[0];
        for (var i = 0; i < VerificationLaneCatalog.LaneIds.Count; i++)
        {
            stage.Tasks[i].Status = TaskRunStatus.Completed;
            stage.Tasks[i].Lane = stage.Tasks[i].Lane! with { Outcome = VerificationLaneOutcome.Pass };
        }
        // Mark the last lane as pending again.
        var last = VerificationLaneCatalog.LaneIds.Count - 1;
        stage.Tasks[last].Status = TaskRunStatus.Pending;
        stage.Tasks[last].Lane = stage.Tasks[last].Lane! with { Outcome = VerificationLaneOutcome.Pending };

        Assert.False(VerificationLaneGate.CanAdvanceBuildStage(run));
    }

    [Fact]
    public async Task LaneEnabledRun_AnyLaneTimedOut_GateKeepsClosed()
    {
        var workflow = await CreateWorkflowAsync();
        var projectId = TestProjectId(_workflowId!);

        var sixLane = BuildSixLaneDefinition();
        await SeedWorkflowTemplateAsync(_workflowId!, sixLane, projectId);
        await workflow.StartAsync(TestInput(projectId));

        var run = await LoadRunAsync(_workflowId!);
        var stage = run.Stages[0];
        for (var i = 0; i < VerificationLaneCatalog.LaneIds.Count; i++)
        {
            stage.Tasks[i].Status = TaskRunStatus.Completed;
            stage.Tasks[i].Lane = stage.Tasks[i].Lane! with { Outcome = VerificationLaneOutcome.Pass };
        }
        stage.Tasks[3].Status = TaskRunStatus.Failed;
        stage.Tasks[3].Lane = stage.Tasks[3].Lane! with { Outcome = VerificationLaneOutcome.Timeout };

        Assert.False(VerificationLaneGate.CanAdvanceBuildStage(run));
    }

    [Fact]
    public async Task LegacyAggregateRun_NoLaneProjection_NoLaneStateSynthesized()
    {
        var workflow = await CreateWorkflowAsync();
        var projectId = TestProjectId(_workflowId!);

        // Bind with aggregate verify task (legacy path).
        var aggregateDefinition = new WorkflowDefinition(new[]
        {
            new StageDefinition("build", new[]
            {
                new TaskDefinition("verify", "Verify", "core/script"),
            }, Array.Empty<CheckDefinition>()),
        });
        await SeedWorkflowTemplateAsync(_workflowId!, aggregateDefinition, projectId);
        await workflow.StartAsync(TestInput(projectId));

        var run = await LoadRunAsync(_workflowId!);
        // A run bound with the aggregate definition has a snapshot but the
        // snapshot's build stage does not carry the six-lane sequence, so
        // the run is NOT lane-enabled and the gate does not synthesize
        // pending lane state.
        Assert.False(string.IsNullOrEmpty(run.BoundWorkflowDefinitionJson));
        Assert.False(VerificationLaneGate.IsLaneEnabledRun(run));
        Assert.Empty(WorkflowBoundDefinitionResolver.CollectLaneAttempts(run));
    }

    [Theory]
    [InlineData("mohist/local")]
    [InlineData("mohist/github-pr")]
    public async Task BuiltInProfile_CleanRun_CompletesAllSixLanesInOrderAndOpensBuildGate(string profileId)
    {
        var workflow = await CreateWorkflowAsync();
        var projectId = TestProjectId(_workflowId!);

        // Representative clean run over the built-in profile's own build
        // stage: keep the profile's exact build-stage tasks (orchestration
        // tasks plus the six verification lanes) as the single stage so the
        // run starts directly in build and every lane must execute once.
        var builtIn = string.Equals(profileId, "mohist/local", StringComparison.Ordinal)
            ? WorkflowProfileCatalog.Definition
            : WorkflowProfileCatalog.GithubPrWorkflowDefinition;
        var buildStage = builtIn.Stages.Single(s => s.Stage == "build");
        var definition = new WorkflowDefinition(new[] { buildStage });

        await SeedWorkflowTemplateAsync(_workflowId!, definition, projectId);
        var coordinator = Grains.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(projectId);
        var bind = await coordinator.BindWorkflowRunAsync(
            new WorkflowProfileCommandPayload.BindWorkflowRun(
                ProjectId: projectId,
                WorkflowRunId: _workflowId!,
                IssueNumber: 1,
                EpicNumber: null,
                ExplicitProfileId: null,
                Metadata: TestInput(projectId).Metadata!),
            $"cmd-clean-{Guid.NewGuid():N}",
            expectedRevision: null);
        Assert.True(bind.IsApplied);

        await workflow.StartAsync(TestInput(projectId));

        var run = await LoadRunAsync(_workflowId!);
        Assert.True(VerificationLaneGate.IsLaneEnabledRun(run));
        var runnerId = await RegisterRunnerAsync();

        // Drive the orchestration tasks that precede the lanes to completion
        // (workspace-prepare, load-tasks, and the local build-health check);
        // none of them is a verification lane. Tasks that follow the lanes
        // (the PR profile's push) are driven after all six lanes pass.
        run = await LoadRunAsync(_workflowId!);
        var build = run.Stages.Single(s => s.Id == "build");
        var preLaneOrchestration = build.Tasks
            .TakeWhile(t => !VerificationLaneCatalog.IsKnownLane(t.DefinitionId))
            .Select(t => t.DefinitionId)
            .ToList();
        foreach (var orchestrationId in preLaneOrchestration)
        {
            var dispatched = await PollWorkAsync(runnerId);
            Assert.Equal(orchestrationId, dispatched.Work.WorkId.Split('.')[0]);
            await ReportAsync(runnerId, dispatched.Work.WorkId, "completed");
        }

        // Each lane is claimed and reported in catalog order; a later lane is
        // never exposed while its predecessor has not passed, and the gate
        // stays closed until the sixth lane passes.
        for (var i = 0; i < VerificationLaneCatalog.LaneIds.Count; i++)
        {
            var expectedId = VerificationLaneCatalog.LaneIds[i];
            var dispatched = await PollWorkAsync(runnerId);
            Assert.StartsWith($"{expectedId}.", dispatched.Work.WorkId);

            if (expectedId == VerificationLaneCatalog.VerifyDotnet)
            {
                // The .NET lane is dispatched with the runtime prelude inside
                // its own shell script, before the unchanged dotnet command.
                var dispatchedWith = dispatched.Work.With
                    ?? throw new InvalidOperationException("verify-dotnet dispatch must carry its run script");
                Assert.Contains("export DOTNET_ROOT=/home/szf/.dotnet", dispatchedWith);
                Assert.Contains("dotnet test Mohist.sln --nologo -m:1 -p:UseSharedCompilation=false", dispatchedWith);
                Assert.True(
                    dispatchedWith.IndexOf("export DOTNET_ROOT=/home/szf/.dotnet", StringComparison.Ordinal)
                    < dispatchedWith.IndexOf("dotnet test", StringComparison.Ordinal));
            }

            await ReportAsync(runnerId, dispatched.Work.WorkId, "completed");

            run = await LoadRunAsync(_workflowId!);
            var laneRun = run.Stages.Single(s => s.Id == "build")
                .Tasks.Single(t => string.Equals(t.DefinitionId, expectedId, StringComparison.Ordinal));
            Assert.Equal(VerificationLaneOutcome.Pass, laneRun.Lane!.Outcome);
            Assert.Equal(i, laneRun.Lane.Order);
            Assert.True(laneRun.Lane.ConfiguredBudgetMs > 0);

            var isFinalLane = i == VerificationLaneCatalog.LaneIds.Count - 1;
            if (!isFinalLane)
            {
                Assert.False(VerificationLaneGate.CanAdvanceBuildStage(run),
                    $"build gate must stay closed while {VerificationLaneCatalog.LaneIds[i + 1]} is still pending");
            }
        }

        // The PR profile keeps its push task after the lanes; drive it (and
        // any other remaining orchestration) to complete the clean run.
        while (true)
        {
            run = await LoadRunAsync(_workflowId!);
            var pending = run.Stages.Single(s => s.Id == "build")
                .Tasks.FirstOrDefault(t => t.Status == TaskRunStatus.Pending);
            if (pending is null) break;
            var dispatched = await PollWorkAsync(runnerId);
            await ReportAsync(runnerId, dispatched.Work.WorkId, "completed");
        }

        run = await LoadRunAsync(_workflowId!);
        Assert.True(VerificationLaneGate.CanAdvanceBuildStage(run));
        Assert.Equal(StageRunStatus.Completed, run.Stages.Single(s => s.Id == "build").Status);
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
        Assert.All(
            VerificationLaneCatalog.LaneIds,
            id => Assert.Equal(
                VerificationLaneOutcome.Pass,
                run.Stages.Single(s => s.Id == "build").Tasks.Single(t => t.DefinitionId == id).Lane!.Outcome));
    }

    [Theory]
    [InlineData("mohist/local")]
    [InlineData("mohist/github-pr")]
    public async Task BuiltInProfile_TimeoutRecovery_PreservesEarlierPassesAndRunsDownstreamOnce(string profileId)
    {
        var workflow = await CreateWorkflowAsync();
        var projectId = TestProjectId(_workflowId!);
        var builtIn = string.Equals(profileId, "mohist/local", StringComparison.Ordinal)
            ? WorkflowProfileCatalog.Definition
            : WorkflowProfileCatalog.GithubPrWorkflowDefinition;
        var buildStage = builtIn.Stages.Single(s => s.Stage == "build");
        var definition = new WorkflowDefinition(new[] { buildStage });

        await SeedWorkflowTemplateAsync(_workflowId!, definition, projectId);
        var coordinator = Grains.GetGrain<IWorkflowProfileReferenceCoordinatorGrain>(projectId);
        var bind = await coordinator.BindWorkflowRunAsync(
            new WorkflowProfileCommandPayload.BindWorkflowRun(
                ProjectId: projectId,
                WorkflowRunId: _workflowId!,
                IssueNumber: 1,
                EpicNumber: null,
                ExplicitProfileId: null,
                Metadata: TestInput(projectId).Metadata!),
            $"cmd-recovery-{Guid.NewGuid():N}",
            expectedRevision: null);
        Assert.True(bind.IsApplied);

        await workflow.StartAsync(TestInput(projectId));
        var runnerId = await RegisterRunnerAsync();

        var run = await LoadRunAsync(_workflowId!);
        var build = run.Stages.Single(s => s.Id == "build");
        foreach (var orchestrationId in build.Tasks
            .TakeWhile(task => !VerificationLaneCatalog.IsKnownLane(task.DefinitionId))
            .Select(task => task.DefinitionId))
        {
            var dispatched = await PollWorkAsync(runnerId);
            Assert.Equal(orchestrationId, dispatched.Work.WorkId.Split('.')[0]);
            await ReportAsync(runnerId, dispatched.Work.WorkId, "completed");
        }

        var first = await PollWorkAsync(runnerId);
        Assert.StartsWith($"{VerificationLaneCatalog.VerifyInstall}.", first.Work.WorkId);
        await ReportAsync(runnerId, first.Work.WorkId, "completed");

        var timedOut = await PollWorkAsync(runnerId);
        Assert.StartsWith($"{VerificationLaneCatalog.VerifyDotnet}.", timedOut.Work.WorkId);
        var laneDefinition = buildStage.Tasks.Single(task => task.Id == VerificationLaneCatalog.VerifyDotnet);
        var timedOutRun = await LoadRunAsync(_workflowId!);
        Assert.Equal(timedOut.Work.TaskRunId, timedOutRun.CurrentStage().RunningTask!.Id);
        var repairTask = laneDefinition.Recovery!.Handlers
            .Single(handler => handler.When is null)
            .Tasks.Single();
        await ReportAsync(runnerId, timedOut.Work.WorkId, new WorkResult(
            "completed",
            "recovery scheduled",
            Error: new ExecutionError("timeout", "dotnet lane exceeded its budget"),
            AddTasks:
            [
                ToRuntimeTask(repairTask),
                ToRuntimeTask(laneDefinition, recoveryRemaining: 1),
            ]));

        run = await LoadRunAsync(_workflowId!);
        var stageAfterSchedule = run.CurrentStage();
        var originalLane = stageAfterSchedule.Tasks.Single(task => task.Id == timedOut.Work.TaskRunId);
        Assert.Equal(VerificationLaneOutcome.Timeout, originalLane.Lane!.Outcome);
        Assert.Equal("dotnet lane exceeded its budget", originalLane.Lane.Error!.Message);
        Assert.Equal(TaskRunStatus.Completed, originalLane.Status);
        Assert.DoesNotContain(
            stageAfterSchedule.Tasks.SkipWhile(task => task.Id != timedOut.Work.TaskRunId).Skip(1),
            task => task.DefinitionId == VerificationLaneCatalog.VerifyInstall);

        var helper = await PollWorkAsync(runnerId);
        Assert.Equal(repairTask.Id, helper.Work.WorkId.Split('.')[0]);
        var helperRun = (await LoadRunAsync(_workflowId!)).CurrentStage().Tasks
            .Single(task => task.Id == helper.Work.TaskRunId);
        Assert.Null(helperRun.Lane);
        Assert.Equal(timedOut.Work.TaskRunId, helperRun.CausedByFailedTaskId);
        await ReportAsync(runnerId, helper.Work.WorkId, "completed");

        var retry = await PollWorkAsync(runnerId);
        Assert.StartsWith($"{VerificationLaneCatalog.VerifyDotnet}.", retry.Work.WorkId);
        Assert.NotEqual(timedOut.Work.TaskRunId, retry.Work.TaskRunId);
        var statusWhileRetryPending = await GetQuerier().GetStatusAsync(_workflowId!);
        var retryLane = statusWhileRetryPending!.VerificationLanes!.Lanes
            .Single(lane => lane.LaneId == VerificationLaneCatalog.VerifyDotnet);
        Assert.Equal("timeout", retryLane.Outcome);
        Assert.Equal(retry.Work.TaskRunId, retryLane.TaskRunId);

        var stale = await workflow.ReceiveTaskReportAsync(
            runnerId,
            timedOut.Work.WorkId,
            new TaskReport(
                timedOut.Work.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: timedOut.Work.TaskRunId));
        Assert.Equal(ReportAck.Stale, stale);

        await ReportAsync(runnerId, retry.Work.WorkId, "completed");
        for (var i = 2; i < VerificationLaneCatalog.LaneIds.Count; i++)
        {
            var next = await PollWorkAsync(runnerId);
            Assert.StartsWith($"{VerificationLaneCatalog.LaneIds[i]}.", next.Work.WorkId);
            await ReportAsync(runnerId, next.Work.WorkId, "completed");
        }

        var downstreamIds = new List<string>();
        while (true)
        {
            run = await LoadRunAsync(_workflowId!);
            var pending = run.CurrentStage().Tasks.FirstOrDefault(task => task.Status == TaskRunStatus.Pending);
            if (pending is null) break;

            var downstream = await PollWorkAsync(runnerId);
            downstreamIds.Add(downstream.Work.WorkId.Split('.')[0]);
            await ReportAsync(runnerId, downstream.Work.WorkId, "completed");
        }

        run = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
        Assert.Equal(
            downstreamIds.Count,
            run.CurrentStage().Tasks.Count(task => downstreamIds.Contains(task.DefinitionId, StringComparer.Ordinal)));
        Assert.All(
            VerificationLaneCatalog.LaneIds,
            id => Assert.Equal(
                VerificationLaneOutcome.Pass,
                run.CurrentStage().Tasks.Last(task => task.DefinitionId == id).Lane!.Outcome));
    }

    private static RuntimeTaskInput ToRuntimeTask(TaskDefinition task, int? recoveryRemaining = null) =>
        new(
            task.Id,
            task.Title ?? task.Id,
            task.Uses,
            task.With is null ? null : JsonSerializer.SerializeToElement(task.With),
            Recovery: task.Recovery,
            Artifacts: task.Artifacts,
            SetVars: task.SetVars,
            RecoveryRemaining: recoveryRemaining,
            Expect: task.Expect is null ? null : JsonSerializer.SerializeToElement(task.Expect));

    private static WorkflowDefinition BuildSixLaneDefinition(bool extraBuildTask = false)
    {
        var tasks = VerificationLaneCatalog.LaneIds
            .Select(id => new TaskDefinition(
                id,
                id,
                "core/script",
                new Dictionary<string, JsonElement?>
                {
                    ["timeout"] = JsonDocument.Parse("120000").RootElement.Clone(),
                }))
            .ToList();
        if (extraBuildTask)
            tasks.Add(new TaskDefinition("extra", "Extra", "core/script"));
        return new WorkflowDefinition(new[]
        {
            new StageDefinition("build", tasks, Array.Empty<CheckDefinition>()),
        });
    }
}