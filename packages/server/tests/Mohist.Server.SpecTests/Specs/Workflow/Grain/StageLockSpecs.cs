using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

[Collection("WorkflowRecovery")]
public class StageLockSpecs : WorkflowGrainSpecs
{
    public StageLockSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task SameProjectIntegrateStages_RunSequentiallyAcrossWorkflows()
    {
        var definition = IntegrateWorkflow();
        Assert.Equal("sequential", definition.Stages[1].LockBehavior);
        Assert.Equal(["project-integration"], definition.Stages[1].Resources);

        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"stage-lock-project-{suffix}";
        var resource = $"project-integration-{suffix}";
        var workflow1Id = $"wf-stage-lock-1-{suffix}";
        var workflow2Id = $"wf-stage-lock-2-{suffix}";
        var runner1Id = await RegisterRunnerForProjectAsync(projectId, $"stage-lock-runner-1-{suffix}");
        var runner2Id = await RegisterRunnerForProjectAsync(projectId, $"stage-lock-runner-2-{suffix}");
        var runner1 = Grains.GetGrain<IRunnerGrain>(runner1Id);
        var runner2 = Grains.GetGrain<IRunnerGrain>(runner2Id);

        var wf1 = Grains.GetGrain<IWorkflowGrain>(workflow1Id);
        var wf2 = Grains.GetGrain<IWorkflowGrain>(workflow2Id);

        definition = IntegrateWorkflow(resource);
        await SeedWorkflowTemplateAsync(workflow1Id, definition, projectId);
        await SeedWorkflowTemplateAsync(workflow2Id, definition, projectId);
        await wf1.StartAsync(ProjectInput(projectId));
        await wf2.StartAsync(ProjectInput(projectId));
        await AssignWorkflowToRunnerAsync(workflow1Id, runner1Id);
        await AssignWorkflowToRunnerAsync(workflow2Id, runner2Id);

        var wf1Plan = await runner1.PollAsync(Services);
        Assert.NotNull(wf1Plan);
        await ReportAsync(runner1Id, workflow1Id, wf1Plan.WorkId, new WorkResult("completed"));

        var wf2Plan = await runner2.PollAsync(Services);
        Assert.NotNull(wf2Plan);
        await ReportAsync(runner2Id, workflow2Id, wf2Plan.WorkId, new WorkResult("completed"));

        var firstIntegrate = await runner1.PollAsync(Services);
        var firstRunnerId = runner1Id;
        var secondRunnerId = runner2Id;
        var secondWorkflowId = workflow2Id;
        if (firstIntegrate is null)
        {
            firstIntegrate = await runner2.PollAsync(Services);
            firstRunnerId = runner2Id;
            secondRunnerId = runner1Id;
            secondWorkflowId = workflow1Id;
        }

        Assert.NotNull(firstIntegrate);
        Assert.Equal("integrate", firstIntegrate.Stage);
        Assert.StartsWith("integrate:archive-change.", firstIntegrate.WorkId);

        var lockGrain = Grains.GetGrain<IWorkflowStageLockGrain>(
            WorkflowStageLockKeys.ForProjectResource(projectId, resource));
        var state = await lockGrain.GetStateAsync();
        Assert.Equal(firstIntegrate.WorkflowRunId, state?.Owner?.WorkflowRunId);

        var firstRunner = Grains.GetGrain<IRunnerGrain>(firstRunnerId);
        var secondRunner = Grains.GetGrain<IRunnerGrain>(secondRunnerId);
        var blocked = await secondRunner.PollAsync(Services);
        Assert.Null(blocked);

        state = await lockGrain.GetStateAsync();
        Assert.Equal(firstIntegrate.WorkflowRunId, state?.Owner?.WorkflowRunId);
        Assert.Contains(state!.Waiting, w => w.WorkflowRunId == secondWorkflowId);

        await ReportAsync(firstRunnerId, firstIntegrate.WorkflowRunId, firstIntegrate.WorkId, new WorkResult("completed"));
        var firstMerge = await firstRunner.PollAsync(Services);
        Assert.NotNull(firstMerge);
        Assert.Equal("integrate", firstMerge.Stage);
        Assert.StartsWith("integrate:merge.", firstMerge.WorkId);

        blocked = await secondRunner.PollAsync(Services);
        Assert.Null(blocked);

        await ReportAsync(firstRunnerId, firstMerge.WorkflowRunId, firstMerge.WorkId, new WorkResult("completed"));
        // issue-361 T-002: bus no longer dispatches lock-release; replay the
        // persisted stage.completed row through the handler.
        await DispatchStageCompletedAsync(firstMerge.WorkflowRunId, "integrate");

        // Confirm the lock grain has dropped the owner before polling for
        // the second runner — the handler's grain call is awaited, so by
        // the time it returns the owner is gone.
        var lockStateAfter = await lockGrain.GetStateAsync();
        Assert.Null(lockStateAfter?.Owner);

        // Poll the second runner until the dispatch service claims the
        // released lock and assigns the next integrate stage.
        var secondIntegrate = await Mohist.Server.SpecTests.Support.TestWait.ForAsync(
            async () => await secondRunner.PollAsync(Services),
            work => work is not null
                && work.WorkflowRunId == secondWorkflowId
                && work.Stage == "integrate"
                && work.WorkId.StartsWith("integrate:archive-change.", StringComparison.Ordinal),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(50),
            "second runner to claim integrate:archive-change after lock release");

        Assert.Equal(secondWorkflowId, secondIntegrate!.WorkflowRunId);
        Assert.Equal("integrate", secondIntegrate.Stage);
        Assert.StartsWith("integrate:archive-change.", secondIntegrate.WorkId);

        await ReportAsync(secondRunnerId, secondIntegrate.WorkflowRunId, secondIntegrate.WorkId, new WorkResult("completed"));
        var secondMerge = await secondRunner.PollAsync(Services);
        Assert.NotNull(secondMerge);
        Assert.StartsWith("integrate:merge.", secondMerge.WorkId);
        await ReportAsync(secondRunnerId, secondMerge.WorkflowRunId, secondMerge.WorkId, new WorkResult("completed"));
    }

    [Fact]
    public async Task FailedIntegrateStage_ReleasesSequentialLock()
    {
        // issue-361 T-002: the bus is write-only, so the
        // WorkflowStageLockReleaseHandler is no longer invoked inline.
        // Drive it manually (as the future dispatcher will) so the
        // failing run releases the sequential lock and the queued
        // runner can claim the next integrate stage.
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"stage-lock-fail-project-{suffix}";
        var resource = $"project-integration-fail-{suffix}";
        var workflow1Id = $"wf-stage-lock-fail-1-{suffix}";
        var workflow2Id = $"wf-stage-lock-fail-2-{suffix}";
        var runner1Id = await RegisterRunnerForProjectAsync(projectId, $"stage-lock-fail-runner-1-{suffix}");
        var runner2Id = await RegisterRunnerForProjectAsync(projectId, $"stage-lock-fail-runner-2-{suffix}");
        var runner1 = Grains.GetGrain<IRunnerGrain>(runner1Id);
        var runner2 = Grains.GetGrain<IRunnerGrain>(runner2Id);

        var wf1 = Grains.GetGrain<IWorkflowGrain>(workflow1Id);
        var wf2 = Grains.GetGrain<IWorkflowGrain>(workflow2Id);

        var definition = IntegrateWorkflow(resource);
        await SeedWorkflowTemplateAsync(workflow1Id, definition, projectId);
        await SeedWorkflowTemplateAsync(workflow2Id, definition, projectId);
        await wf1.StartAsync(ProjectInput(projectId));
        await wf2.StartAsync(ProjectInput(projectId));
        await AssignWorkflowToRunnerAsync(workflow1Id, runner1Id);
        await AssignWorkflowToRunnerAsync(workflow2Id, runner2Id);

        var wf1Plan = await runner1.PollAsync(Services);
        Assert.NotNull(wf1Plan);
        await ReportAsync(runner1Id, workflow1Id, wf1Plan.WorkId, new WorkResult("completed"));

        var wf2Plan = await runner2.PollAsync(Services);
        Assert.NotNull(wf2Plan);
        await ReportAsync(runner2Id, workflow2Id, wf2Plan.WorkId, new WorkResult("completed"));

        var wf1Integrate = await runner1.PollAsync(Services);
        Assert.NotNull(wf1Integrate);
        await ReportAsync(runner1Id, workflow1Id, wf1Integrate.WorkId, new WorkResult("failed", "merge conflict"));
        await DispatchStageFailedAsync(workflow1Id, "integrate");

        var wf2Integrate = await runner2.PollAsync(Services);
        Assert.NotNull(wf2Integrate);
        Assert.Equal(workflow2Id, wf2Integrate.WorkflowRunId);
        Assert.Equal("integrate", wf2Integrate.Stage);

        await ReportAsync(runner2Id, workflow2Id, wf2Integrate.WorkId, new WorkResult("completed"));
        var wf2Merge = await runner2.PollAsync(Services);
        Assert.NotNull(wf2Merge);
        await ReportAsync(runner2Id, workflow2Id, wf2Merge.WorkId, new WorkResult("completed"));
    }

    [Fact]
    public async Task FailedIntegrateStage_LockReleasedViaBusSubscriptionHandler()
    {
        // Pins the T-005 D8 bus-side lock release contract: the grain's
        // On() dispatch no longer invokes ReleaseStageLocksAsync directly
        // for StageCompleted/StageFailed — instead, the bus-side
        // WorkflowStageLockReleaseHandler subscribes to the events and
        // routes them back into the grain. Lock acquisition by the next
        // workflow run therefore requires the publish → handler → grain
        // round trip to complete before the next poll succeeds.
        //
        // issue-361 T-002: the bus is write-only, so the handler no
        // longer fires inline with the publish. Drive the handler
        // directly — that is the future dispatcher's job and the only
        // path that completes the round trip today.
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"stage-lock-bus-fail-project-{suffix}";
        var resource = $"project-integration-bus-fail-{suffix}";
        var workflow1Id = $"wf-stage-lock-bus-fail-1-{suffix}";
        var workflow2Id = $"wf-stage-lock-bus-fail-2-{suffix}";
        var runner1Id = await RegisterRunnerForProjectAsync(projectId, $"stage-lock-bus-fail-runner-1-{suffix}");
        var runner2Id = await RegisterRunnerForProjectAsync(projectId, $"stage-lock-bus-fail-runner-2-{suffix}");
        var runner1 = Grains.GetGrain<IRunnerGrain>(runner1Id);
        var runner2 = Grains.GetGrain<IRunnerGrain>(runner2Id);

        var wf1 = Grains.GetGrain<IWorkflowGrain>(workflow1Id);
        var wf2 = Grains.GetGrain<IWorkflowGrain>(workflow2Id);

        var definition = IntegrateWorkflow(resource);
        await SeedWorkflowTemplateAsync(workflow1Id, definition, projectId);
        await SeedWorkflowTemplateAsync(workflow2Id, definition, projectId);
        await wf1.StartAsync(ProjectInput(projectId));
        await wf2.StartAsync(ProjectInput(projectId));
        await AssignWorkflowToRunnerAsync(workflow1Id, runner1Id);
        await AssignWorkflowToRunnerAsync(workflow2Id, runner2Id);

        var wf1Build = await runner1.PollAsync(Services);
        Assert.NotNull(wf1Build);
        await ReportAsync(runner1Id, workflow1Id, wf1Build.WorkId, new WorkResult("completed"));

        var wf2Build = await runner2.PollAsync(Services);
        Assert.NotNull(wf2Build);
        await ReportAsync(runner2Id, workflow2Id, wf2Build.WorkId, new WorkResult("completed"));

        var wf1Integrate = await runner1.PollAsync(Services);
        Assert.NotNull(wf1Integrate);

        var lockGrain = Grains.GetGrain<IWorkflowStageLockGrain>(
            WorkflowStageLockKeys.ForProjectResource(projectId, resource));
        var stateBefore = await lockGrain.GetStateAsync();
        Assert.Equal(workflow1Id, stateBefore?.Owner?.WorkflowRunId);

        // Failing the integrate task emits StageFailed. The grain's On()
        // branch used to release the lock here; T-005 moves that into the
        // WorkflowStageLockReleaseHandler bus subscription. The dispatcher
        // (future step 3) will replay the persisted row; until then,
        // pull the row and run the handler manually.
        await ReportAsync(runner1Id, workflow1Id, wf1Integrate.WorkId, new WorkResult("failed", "merge conflict"));
        await DispatchStageFailedAsync(workflow1Id, "integrate");

        var stateAfter = await lockGrain.GetStateAsync();
        Assert.Null(stateAfter?.Owner);

        var wf2Integrate = await runner2.PollAsync(Services);
        Assert.NotNull(wf2Integrate);
        Assert.Equal(workflow2Id, wf2Integrate.WorkflowRunId);
        Assert.Equal("integrate", wf2Integrate.Stage);
    }

    [Fact]
    public async Task StoppedIntegrateWorkflow_ReleasesSequentialLock()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"stage-lock-stop-project-{suffix}";
        var resource = $"project-integration-stop-{suffix}";
        var workflow1Id = $"wf-stage-lock-stop-1-{suffix}";
        var workflow2Id = $"wf-stage-lock-stop-2-{suffix}";
        var runner1Id = await RegisterRunnerForProjectAsync(projectId, $"stage-lock-stop-runner-1-{suffix}");
        var runner2Id = await RegisterRunnerForProjectAsync(projectId, $"stage-lock-stop-runner-2-{suffix}");
        var runner1 = Grains.GetGrain<IRunnerGrain>(runner1Id);
        var runner2 = Grains.GetGrain<IRunnerGrain>(runner2Id);

        var wf1 = Grains.GetGrain<IWorkflowGrain>(workflow1Id);
        var wf2 = Grains.GetGrain<IWorkflowGrain>(workflow2Id);

        var definition = IntegrateWorkflow(resource);
        await SeedWorkflowTemplateAsync(workflow1Id, definition, projectId);
        await SeedWorkflowTemplateAsync(workflow2Id, definition, projectId);
        await wf1.StartAsync(ProjectInput(projectId));
        await wf2.StartAsync(ProjectInput(projectId));
        await AssignWorkflowToRunnerAsync(workflow1Id, runner1Id);
        await AssignWorkflowToRunnerAsync(workflow2Id, runner2Id);

        var wf1Build = await runner1.PollAsync(Services);
        Assert.NotNull(wf1Build);
        await ReportAsync(runner1Id, workflow1Id, wf1Build.WorkId, new WorkResult("completed"));

        var wf2Build = await runner2.PollAsync(Services);
        Assert.NotNull(wf2Build);
        await ReportAsync(runner2Id, workflow2Id, wf2Build.WorkId, new WorkResult("completed"));

        var wf1Integrate = await runner1.PollAsync(Services);
        Assert.NotNull(wf1Integrate);
        Assert.Equal("integrate", wf1Integrate.Stage);

        await wf1.StopAsync("stopped by test");

        var wf2Integrate = await runner2.PollAsync(Services);
        Assert.NotNull(wf2Integrate);
        Assert.Equal(workflow2Id, wf2Integrate.WorkflowRunId);
        Assert.Equal("integrate", wf2Integrate.Stage);

        await ReportAsync(runner2Id, workflow2Id, wf2Integrate.WorkId, new WorkResult("completed"));
        var wf2Merge = await runner2.PollAsync(Services);
        Assert.NotNull(wf2Merge);
        await ReportAsync(runner2Id, workflow2Id, wf2Merge.WorkId, new WorkResult("completed"));
    }

    private static WorkflowDefinition IntegrateWorkflow(string resource = "project-integration")
    {
        return new WorkflowDefinition(
        [
            new StageDefinition("build",
                [new("build-task", "Build task", "spec/task")],
                []),
            new StageDefinition("integrate",
                [
                    new("integrate:archive-change", "Archive change", "spec/task"),
                    new("integrate:merge", "Merge branch", "spec/task")
                ],
                [],
                LockBehavior: "sequential",
                Resources: [resource])
        ]);
    }

    private static WorkflowStartInput ProjectInput(string projectId)
    {
        return new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
            Name: null,
            CreatedAt: TestTime.UtcNow,
            ProjectId: projectId));
    }

    private async Task DispatchStageFailedAsync(string workflowRunId, string stage)
    {
        await DispatchStageTerminalAsync(workflowRunId, EventCatalog.ReverseDns.StageFailed, stage);
    }

    private async Task DispatchStageCompletedAsync(string workflowRunId, string stage)
    {
        await DispatchStageTerminalAsync(workflowRunId, EventCatalog.ReverseDns.StageCompleted, stage);
    }

    private async Task DispatchStageTerminalAsync(string workflowRunId, string type, string stage)
    {
        using var scope = Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var stored = await Mohist.Server.SpecTests.Support.TestWait.ForAsync(
            async () =>
            {
                var list = await events.ListAsync(workflowRunId);
                return list.FirstOrDefault(e =>
                    e.Envelope.Type == type &&
                    e.Envelope.Extensions.TryGetValue(EventCatalog.Lineage.Stage, out var stampedStage)
                    && stampedStage == stage);
            },
            envelope => envelope is not null,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(50),
            $"{type}({stage}) event row for {workflowRunId}");

        await scope.ServiceProvider
            .GetRequiredService<EventDispatcherService>()
            .DispatchAsync(CancellationToken.None);
    }
}
