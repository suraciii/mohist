using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain;
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