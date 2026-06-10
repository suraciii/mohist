using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

public class StageLockSpecs : WorkflowGrainSpecs
{
    public StageLockSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

        var wf1Plan = await runner1.PollAsync();
        Assert.NotNull(wf1Plan);
        await ReportAsync(runner1Id, workflow1Id, wf1Plan.WorkId, new WorkResult("completed"));

        var wf2Plan = await runner2.PollAsync();
        Assert.NotNull(wf2Plan);
        await ReportAsync(runner2Id, workflow2Id, wf2Plan.WorkId, new WorkResult("completed"));

        var firstIntegrate = await runner1.PollAsync();
        var firstRunnerId = runner1Id;
        var secondRunnerId = runner2Id;
        var secondWorkflowId = workflow2Id;
        if (firstIntegrate is null)
        {
            firstIntegrate = await runner2.PollAsync();
            firstRunnerId = runner2Id;
            secondRunnerId = runner1Id;
            secondWorkflowId = workflow1Id;
        }

        Assert.NotNull(firstIntegrate);
        Assert.Equal("integrate", firstIntegrate.Stage);
        Assert.StartsWith("integrate:spec-sync.", firstIntegrate.WorkId);

        var lockGrain = Grains.GetGrain<IWorkflowStageLockGrain>(
            WorkflowStageLockKeys.ForProjectResource(projectId, resource));
        var state = await lockGrain.GetStateAsync();
        Assert.Equal(firstIntegrate.WorkflowRunId, state?.Owner?.WorkflowRunId);

        var firstRunner = Grains.GetGrain<IRunnerGrain>(firstRunnerId);
        var secondRunner = Grains.GetGrain<IRunnerGrain>(secondRunnerId);
        var blocked = await secondRunner.PollAsync();
        Assert.Null(blocked);

        state = await lockGrain.GetStateAsync();
        Assert.Equal(firstIntegrate.WorkflowRunId, state?.Owner?.WorkflowRunId);
        Assert.Contains(state!.Waiting, w => w.WorkflowRunId == secondWorkflowId);

        await ReportAsync(firstRunnerId, firstIntegrate.WorkflowRunId, firstIntegrate.WorkId, new WorkResult("completed"));
        var firstMerge = await firstRunner.PollAsync();
        Assert.NotNull(firstMerge);
        Assert.Equal("integrate", firstMerge.Stage);
        Assert.StartsWith("integrate:merge.", firstMerge.WorkId);

        blocked = await secondRunner.PollAsync();
        Assert.Null(blocked);

        await ReportAsync(firstRunnerId, firstMerge.WorkflowRunId, firstMerge.WorkId, new WorkResult("completed"));

        var secondIntegrate = await secondRunner.PollAsync();
        Assert.NotNull(secondIntegrate);
        Assert.Equal(secondWorkflowId, secondIntegrate.WorkflowRunId);
        Assert.Equal("integrate", secondIntegrate.Stage);
        Assert.StartsWith("integrate:spec-sync.", secondIntegrate.WorkId);

        await ReportAsync(secondRunnerId, secondIntegrate.WorkflowRunId, secondIntegrate.WorkId, new WorkResult("completed"));
        var secondMerge = await secondRunner.PollAsync();
        Assert.NotNull(secondMerge);
        Assert.StartsWith("integrate:merge.", secondMerge.WorkId);
        await ReportAsync(secondRunnerId, secondMerge.WorkflowRunId, secondMerge.WorkId, new WorkResult("completed"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public async Task FailedIntegrateStage_ReleasesSequentialLock()
    {
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

        var wf1Plan = await runner1.PollAsync();
        Assert.NotNull(wf1Plan);
        await ReportAsync(runner1Id, workflow1Id, wf1Plan.WorkId, new WorkResult("completed"));

        var wf2Plan = await runner2.PollAsync();
        Assert.NotNull(wf2Plan);
        await ReportAsync(runner2Id, workflow2Id, wf2Plan.WorkId, new WorkResult("completed"));

        var wf1Integrate = await runner1.PollAsync();
        Assert.NotNull(wf1Integrate);
        await ReportAsync(runner1Id, workflow1Id, wf1Integrate.WorkId, new WorkResult("failed", "merge conflict"));

        var wf2Integrate = await runner2.PollAsync();
        Assert.NotNull(wf2Integrate);
        Assert.Equal(workflow2Id, wf2Integrate.WorkflowRunId);
        Assert.Equal("integrate", wf2Integrate.Stage);

        await ReportAsync(runner2Id, workflow2Id, wf2Integrate.WorkId, new WorkResult("completed"));
        var wf2Merge = await runner2.PollAsync();
        Assert.NotNull(wf2Merge);
        await ReportAsync(runner2Id, workflow2Id, wf2Merge.WorkId, new WorkResult("completed"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
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

        var wf1Build = await runner1.PollAsync();
        Assert.NotNull(wf1Build);
        await ReportAsync(runner1Id, workflow1Id, wf1Build.WorkId, new WorkResult("completed"));

        var wf2Build = await runner2.PollAsync();
        Assert.NotNull(wf2Build);
        await ReportAsync(runner2Id, workflow2Id, wf2Build.WorkId, new WorkResult("completed"));

        var wf1Integrate = await runner1.PollAsync();
        Assert.NotNull(wf1Integrate);
        Assert.Equal("integrate", wf1Integrate.Stage);

        await wf1.StopAsync("stopped by test");

        var wf2Integrate = await runner2.PollAsync();
        Assert.NotNull(wf2Integrate);
        Assert.Equal(workflow2Id, wf2Integrate.WorkflowRunId);
        Assert.Equal("integrate", wf2Integrate.Stage);

        await ReportAsync(runner2Id, workflow2Id, wf2Integrate.WorkId, new WorkResult("completed"));
        var wf2Merge = await runner2.PollAsync();
        Assert.NotNull(wf2Merge);
        await ReportAsync(runner2Id, workflow2Id, wf2Merge.WorkId, new WorkResult("completed"));
    }

    private static WorkflowDefinition IntegrateWorkflow(string resource = "project-integration")
    {
        return new WorkflowDefinition("spec/integrate-lock",
        [
            new StageDefinition("build",
                [new("build-task", "Build task", "spec/task")],
                []),
            new StageDefinition("integrate",
                [
                    new("integrate:spec-sync", "Sync specs", "spec/task"),
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
            CreatedAt: DateTimeOffset.UtcNow,
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectId"] = projectId,
            }));
    }
}
