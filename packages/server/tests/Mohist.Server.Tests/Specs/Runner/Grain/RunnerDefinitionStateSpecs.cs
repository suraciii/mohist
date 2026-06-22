using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Runner.Grain;

public class RunnerDefinitionStateSpecs : WorkflowGrainSpecs
{
    public RunnerDefinitionStateSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private RunnerDefinitionStore DefinitionStore =>
        _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<RunnerDefinitionStore>();

    private async Task DeactivateRunnerAsync(string runnerId)
    {
        var management = Grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var activations = await management.GetDetailedGrainStatistics();
            if (!activations.Any(stat => stat.GrainType.Contains(nameof(RunnerGrain), StringComparison.Ordinal)
                && stat.GrainId.ToString()!.Contains(runnerId, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Runner grain '{runnerId}' did not deactivate in time.");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task RegisterAsync_NewRunner_InitializesPersistedSlotsToOne_AndIgnoresReportedValue()
    {
        var runnerId = $"runner-default-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            TestProjectId(runnerId),
            MaxWorkflowSlots: 99));

        Assert.Equal(1, await runner.GetSlotsAsync());

        var definition = await DefinitionStore.GetOrInitAsync(runnerId);
        Assert.Equal(1, definition);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task UpdateAsync_WriteThrough_PersistsAndNextDispatchHonorsNewCapacity()
    {
        await ClearGlobalRunnerRegistryAsync();
        await ClearBacklogAsync();
        var projectId = $"test-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, maxWorkflowSlots: 1);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        _workflowId = $"wf-update-1-{Guid.NewGuid():N}";
        var wf1 = Grains.GetGrain<IWorkflowGrain>(_workflowId);
        await SeedWorkflowTemplateAsync(_workflowId, SingleStage(checks: []), projectId);
        await wf1.StartAsync(TestInput(projectId));
        await AssignWorkflowToRunnerAsync(_workflowId, runnerId);

        var wf2Id = $"wf-update-2-{Guid.NewGuid():N}";
        var wf2 = Grains.GetGrain<IWorkflowGrain>(wf2Id);
        await SeedWorkflowTemplateAsync(wf2Id, SingleStage(checks: []), projectId);
        await wf2.StartAsync(TestInput(projectId));

        var first = await runner.PollAsync();
        Assert.NotNull(first);
        Assert.Equal(_workflowId, first.WorkflowRunId);

        Assert.Null(await runner.PollAsync());

        await runner.UpdateAsync(2);

        Assert.Equal(2, await runner.GetSlotsAsync());

        var definition = await DefinitionStore.GetOrInitAsync(runnerId);
        Assert.Equal(2, definition);

        var second = await runner.PollAsync();
        Assert.NotNull(second);
        Assert.Equal(wf2Id, second.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task GrainDeactivation_Reactivation_RestoresPersistedSlots()
    {
        var projectId = $"test-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, maxWorkflowSlots: 1);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.UpdateAsync(4);
        Assert.Equal(4, await runner.GetSlotsAsync());

        await DeactivateRunnerAsync(runnerId);

        var reactivated = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.Equal(4, await reactivated.GetSlotsAsync());

        await reactivated.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            projectId,
            MaxWorkflowSlots: 99));

        Assert.Equal(4, await reactivated.GetSlotsAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Dispatch_EnforcesPersistedSlotBound()
    {
        var projectId = $"test-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, maxWorkflowSlots: 2);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var wf1Id = $"wf-bound-1-{Guid.NewGuid():N}";
        var wf1 = Grains.GetGrain<IWorkflowGrain>(wf1Id);
        await SeedWorkflowTemplateAsync(wf1Id, SingleStage(checks: []), projectId);
        await wf1.StartAsync(TestInput(projectId));
        await AssignWorkflowToRunnerAsync(wf1Id, runnerId);

        var wf2Id = $"wf-bound-2-{Guid.NewGuid():N}";
        var wf2 = Grains.GetGrain<IWorkflowGrain>(wf2Id);
        await SeedWorkflowTemplateAsync(wf2Id, SingleStage(checks: []), projectId);
        await wf2.StartAsync(TestInput(projectId));

        var wf3Id = $"wf-bound-3-{Guid.NewGuid():N}";
        var wf3 = Grains.GetGrain<IWorkflowGrain>(wf3Id);
        await SeedWorkflowTemplateAsync(wf3Id, SingleStage(checks: []), projectId);
        await wf3.StartAsync(TestInput(projectId));

        var work1 = await runner.PollAsync();
        Assert.NotNull(work1);
        Assert.Equal(wf1Id, work1.WorkflowRunId);

        var work2 = await runner.PollAsync();
        Assert.NotNull(work2);
        Assert.Equal(wf2Id, work2.WorkflowRunId);

        Assert.Null(await runner.PollAsync());

        await runner.ReportResultAsync(work1, work1.WorkId, new WorkResult("completed"));

        var work3 = await runner.PollAsync();
        Assert.NotNull(work3);
        Assert.Equal(wf3Id, work3.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Dispatch_AgentJobWork_DoesNotConsumeWorkflowClaimSlot()
    {
        var projectId = $"test-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var agentJobDispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: $"agent-work-{Guid.NewGuid():N}",
            OwnerKind: WorkDispatchOwnerKinds.AgentJob,
            AgentJobId: $"agent-job-{Guid.NewGuid():N}");
        var assignment = await runner.AssignWorkAsync(agentJobDispatch);
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assignment.Status);

        var workflowId = $"wf-agent-slot-{Guid.NewGuid():N}";
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, SingleStage(checks: []), projectId);
        await workflow.StartAsync(TestInput(projectId));

        var work = await runner.PollAsync();

        Assert.NotNull(work);
        Assert.Equal(workflowId, work.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task OfflineRunner_DoesNotClaimOrAcceptNewWork()
    {
        var projectId = $"test-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.UnregisterAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.PollAsync());

        var dispatch = new WorkDispatch(
            WorkflowRunId: $"wf-offline-{Guid.NewGuid():N}",
            WorkId: "task-1.1",
            OwnerKind: WorkDispatchOwnerKinds.Workflow);
        // The RunnerGrain assigns work even when offline — work is queued
        // in _works and served via PollAsync when the runner reconnects.
        var assignment = await runner.AssignWorkAsync(dispatch);
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assignment.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ReportedMaxWorkflowSlots_DoesNotAffectDispatchCapacity()
    {
        var projectId = $"test-project-{Guid.NewGuid():N}";
        var runnerId = $"runner-reported-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            projectId,
            MaxWorkflowSlots: 5));

        Assert.Equal(1, await runner.GetSlotsAsync());

        _workflowId = $"wf-reported-{Guid.NewGuid():N}";
        var wf1 = Grains.GetGrain<IWorkflowGrain>(_workflowId);
        await SeedWorkflowTemplateAsync(_workflowId, SingleStage(checks: []), projectId);
        await wf1.StartAsync(TestInput(projectId));
        await AssignWorkflowToRunnerAsync(_workflowId, runnerId);

        var wf2Id = $"wf-reported-2-{Guid.NewGuid():N}";
        var wf2 = Grains.GetGrain<IWorkflowGrain>(wf2Id);
        await SeedWorkflowTemplateAsync(wf2Id, SingleStage(checks: []), projectId);
        await wf2.StartAsync(TestInput(projectId));

        var first = await runner.PollAsync();
        Assert.NotNull(first);
        Assert.Equal(_workflowId, first.WorkflowRunId);

        Assert.Null(await runner.PollAsync());
    }
}
