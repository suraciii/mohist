using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;
using Xunit;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

public class RunnerDefinitionStateSpecs : WorkflowGrainSpecs
{
    public RunnerDefinitionStateSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private RunnerDefinitionStore DefinitionStore =>
        _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<RunnerDefinitionStore>();

    private async Task DeactivateRunnerAsync(string runnerId)
    {
        var management = Grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);

        await TestWait.ForAsync(
            async () => await management.GetDetailedGrainStatistics(),
            activations => !activations.Any(stat => stat.GrainType.Contains(nameof(RunnerGrain), StringComparison.Ordinal)
                && stat.GrainId.ToString()!.Contains(runnerId, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(50),
            $"Runner grain '{runnerId}' to deactivate");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task RegisterAsync_NewRunner_InitializesPersistedSlotsToOne()
    {
        var runnerId = $"runner-default-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            TestProjectId(runnerId)));

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

        var first = await runner.PollAsync(Services);
        Assert.NotNull(first);
        Assert.Equal(_workflowId, first.WorkflowRunId);

        var secondAtCapacity = await runner.PollAsync(Services);
        if (secondAtCapacity is not null)
        {
            Assert.Equal(_workflowId, secondAtCapacity.WorkflowRunId);
        }

        await runner.UpdateAsync(2);

        Assert.Equal(2, await runner.GetSlotsAsync());

        var definition = await DefinitionStore.GetOrInitAsync(runnerId);
        Assert.Equal(2, definition);

        WorkDispatch? second = null;
        for (var i = 0; i < 3 && second is null; i++)
        {
            foreach (var d in await runner.PollAllAsync(Services))
            {
                if (d.WorkflowRunId == wf2Id) second = d;
            }
        }
        Assert.NotNull(second);
        Assert.Equal(wf2Id, second!.WorkflowRunId);
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
            projectId));

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

        // A 2-slot runner can hold two workflows in flight. The first
        // reconciliation round may dispatch wf-bound-1 (assigned Ready) and
        // wf-bound-2 (claimable Pending) together, so collect both before
        // asserting the bound. wf-bound-3 must NOT be claimed while both
        // slots are occupied.
        var inFlight = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 3 && inFlight.Count < 2; i++)
        {
            foreach (var d in await runner.PollAllAsync(Services))
                inFlight.Add(d.WorkflowRunId);
        }
        Assert.Contains(wf1Id, inFlight);
        Assert.Contains(wf2Id, inFlight);

        // The third workflow must not be claimed while both slots are full
        // (subsequent polls only re-dispatch in-flight work, never wf-bound-3).
        for (var i = 0; i < 3; i++)
        {
            foreach (var d in await runner.PollAllAsync(Services))
                Assert.DoesNotContain(wf3Id, d.WorkflowRunId);
        }

        // Free one slot by completing wf-bound-1's task.
        await ReportAsync(runnerId, wf1Id, "task-1.1", new WorkResult("completed"));

        // Now wf-bound-3 can be claimed. Collect dispatches until it appears.
        WorkDispatch? work3 = null;
        for (var i = 0; i < 5 && work3 is null; i++)
        {
            foreach (var d in await runner.PollAllAsync(Services))
            {
                if (d.WorkflowRunId == wf3Id) work3 = d;
            }
        }
        Assert.NotNull(work3);
        Assert.Equal(wf3Id, work3!.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Dispatch_AgentJobWork_DoesNotConsumeWorkflowAssignmentSlot()
    {
        var projectId = $"test-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var agentJobDispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: $"agent-work-{Guid.NewGuid():N}",
            OwnerKind: WorkDispatchOwnerKinds.AgentJob,
            AgentJobId: $"agent-job-{Guid.NewGuid():N}");
        var assignment = await runner.AssignAgentJobAsync(agentJobDispatch);
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assignment.Status);

        var workflowId = $"wf-agent-slot-{Guid.NewGuid():N}";
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(workflowId, SingleStage(checks: []), projectId);
        await workflow.StartAsync(TestInput(projectId));

        var work = await runner.PollAsync(Services);

        Assert.NotNull(work);
        Assert.Equal(workflowId, work.WorkflowRunId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task OfflineRunner_DoesNotAssignmentOrAcceptNewWork()
    {
        var projectId = $"test-project-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.UnregisterAsync();

        // Under the reconciliation model poll no longer throws for an offline
        // runner — it refreshes presence and computes dispatches through the
        // stateless DispatchService, which simply finds nothing to dispatch
        // for a runner that has been closed out. Direct agent-job assignment
        // is still rejected.
        var polled = await runner.PollAsync(Services);
        Assert.Null(polled);

        var dispatch = new WorkDispatch(
            WorkflowRunId: $"wf-offline-{Guid.NewGuid():N}",
            WorkId: "task-1.1",
            OwnerKind: WorkDispatchOwnerKinds.Workflow);
        // Workflow delivery no longer accepts direct runner-side assignment.
        var assignment = await runner.AssignAgentJobAsync(dispatch);
        Assert.Equal(RunnerWorkAssignmentStatus.Rejected, assignment.Status);
        Assert.Equal("invalid-work", assignment.Reason);
    }

}
