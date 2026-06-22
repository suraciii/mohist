using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.Tests.Support;
using Mohist.Server.Tests.Specs.Workflow;

namespace Mohist.Server.Tests.Specs.Runner.Grain;

/// <summary>
/// Covers the spec requirement "Runner is a global execution resource":
/// runner registration is bound to the global registry, and dispatch
/// round-robins across every known project backlog. Per T-003 AC, the
/// project-scoped registry path is removed entirely; every consumer goes
/// through the global path.
/// </summary>
public class RunnerGlobalizationSpecs : WorkflowGrainSpecs
{
    public RunnerGlobalizationSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    private IWorkflowBacklogDirectory BacklogDirectory =>
        _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IWorkflowBacklogDirectory>();

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Register_RunnerIsRecordedInGlobalRegistry_NotInProjectRegistry()
    {
        var runnerId = $"globalized-runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "globalized-host",
            "some-legacy-project-id"));

        var globalRegistry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var globalIds = await globalRegistry.ListRunnerIdsAsync();
        Assert.Contains(runnerId, globalIds);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ListEligibleRunnersAsync_ReturnsAllRegisteredRunnersRegardlessOfProjectIdField()
    {
        var globalRunnerId = $"global-runner-{Guid.NewGuid():N}";
        var projectRunnerId = $"legacy-project-runner-{Guid.NewGuid():N}";

        var globalRunner = Grains.GetGrain<IRunnerGrain>(globalRunnerId);
        await globalRunner.RegisterAsync(new RunnerInfo(globalRunnerId, ["spec/*"], "host-g", null));

        var projectRunner = Grains.GetGrain<IRunnerGrain>(projectRunnerId);
        await projectRunner.RegisterAsync(new RunnerInfo(projectRunnerId, ["spec/*"], "host-p", "any-project-id"));

        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await registry.ListEligibleRunnersAsync("querying-project");

        Assert.Contains(eligible, r => r.RunnerId == globalRunnerId);
        Assert.Contains(eligible, r => r.RunnerId == projectRunnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Poll_RoundRobinClaimsAcrossProjectBacklogs()
    {
        await ClearGlobalRunnerRegistryAsync();
        await ClearBacklogAsync();

        var projectA = $"globalization-proj-a-{Guid.NewGuid():N}";
        var projectB = $"globalization-proj-b-{Guid.NewGuid():N}";
        var projectC = $"globalization-proj-c-{Guid.NewGuid():N}";

        BacklogDirectory.RegisterProject(projectA);
        BacklogDirectory.RegisterProject(projectB);
        BacklogDirectory.RegisterProject(projectC);

        var workflowA = $"wf-a-{Guid.NewGuid():N}";
        var workflowB = $"wf-b-{Guid.NewGuid():N}";
        var workflowC = $"wf-c-{Guid.NewGuid():N}";

        var def = new WorkflowDefinition("spec/workflow",
        [
            new StageDefinition("build",
                [new TaskDefinition("task-1", "Task 1", "spec/task")],
                [new CheckDefinition("check-1", "Check 1", "spec/check")])
        ]);

        async Task<string> EnqueueAsync(string workflowId, string projectId)
        {
            var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
            await SeedWorkflowTemplateAsync(workflowId, def, projectId);
            await workflow.StartAsync(new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UtcNow,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = projectId,
                })));
            return workflowId;
        }

        await EnqueueAsync(workflowA, projectA);
        await EnqueueAsync(workflowB, projectB);
        await EnqueueAsync(workflowC, projectC);

        var runnerId = $"globalized-rrobin-runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "round-robin-host",
            null));
        // Bump capacity to 3 so the global runner can hold one workflow per
        // project backlog and exercise the round-robin claim path across all
        // of them. The default of 1 would only let the runner pick a single
        // workflow before its slot was full.
        await runner.UpdateAsync(3);

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var claimedWorkflowIds = new List<string>();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var work = await runner.PollAsync();
            if (work is null) break;
            Assert.Contains(work.WorkflowRunId, new[] { workflowA, workflowB, workflowC });
            if (claimed.Add(work.WorkflowRunId))
                claimedWorkflowIds.Add(work.WorkflowRunId);
        }

        Assert.Equal(3, claimed.Count);
        Assert.Contains(workflowA, claimedWorkflowIds);
        Assert.Contains(workflowB, claimedWorkflowIds);
        Assert.Contains(workflowC, claimedWorkflowIds);
    }
}
