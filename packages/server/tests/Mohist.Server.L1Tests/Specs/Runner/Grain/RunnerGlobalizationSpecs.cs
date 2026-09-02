using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.TestSupport;
using Mohist.Server.L1Tests.Specs.Workflow;

namespace Mohist.Server.L1Tests.Specs.Runner.Grain;

/// <summary>
/// Covers the spec requirement "Runner is a global execution resource":
/// runner registration is bound to the global registry, and dispatch scans
/// assignable workflow records across projects. Per T-003 AC, the
/// project-scoped registry path is removed entirely; every consumer goes
/// through the global path.
/// </summary>
[Collection("RunnerGrain")]
public class RunnerGlobalizationSpecs : WorkflowGrainSpecs
{
    public RunnerGlobalizationSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Poll_GlobalRunnerAssignsAcrossProjects()
    {
        await ClearGlobalRunnerRegistryAsync();
        await ClearBacklogAsync();

        var projectA = $"globalization-proj-a-{Guid.NewGuid():N}";
        var projectB = $"globalization-proj-b-{Guid.NewGuid():N}";
        var projectC = $"globalization-proj-c-{Guid.NewGuid():N}";

        var workflowA = $"wf-a-{Guid.NewGuid():N}";
        var workflowB = $"wf-b-{Guid.NewGuid():N}";
        var workflowC = $"wf-c-{Guid.NewGuid():N}";

        var def = new WorkflowDefinition(
        [
            new StageDefinition("build",
                [new TaskDefinition("task-1", "Task 1", "spec/task")],
                [new CheckDefinition("check-1", "Check 1", "spec/check")])
        ]);

        async Task<string> StartAssignableWorkflowAsync(string workflowId, string projectId)
        {
            var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
            await SeedWorkflowTemplateAsync(workflowId, def, projectId);
            await workflow.StartAsync(new WorkflowStartInput(Metadata: new WorkflowRunMetadata(
                Name: null,
                CreatedAt: TestTime.UtcNow,
                 ProjectId: projectId),
                VerificationCommand: "true"));
            return workflowId;
        }

        await StartAssignableWorkflowAsync(workflowA, projectA);
        await StartAssignableWorkflowAsync(workflowB, projectB);
        await StartAssignableWorkflowAsync(workflowC, projectC);

        var runnerId = $"globalized-rrobin-runner-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "round-robin-host",
            null));
        // Bump capacity above the target count so unrelated assignable workflows
        // left by other specs cannot fill every slot before this assertion sees
        // all three projects.
        await runner.UpdateAsync(20);

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var assignedWorkflowIds = new List<string>();
        var expectedWorkflowIds = new HashSet<string>([workflowA, workflowB, workflowC], StringComparer.Ordinal);

        for (var attempt = 0; attempt < 50 && assigned.Count < expectedWorkflowIds.Count; attempt++)
        {
            var round = await runner.PollAllAsync(Services);
            if (round.Count == 0) break;
            foreach (var work in round)
            {
                if (!expectedWorkflowIds.Contains(work.WorkflowRunId))
                    continue;
                if (assigned.Add(work.WorkflowRunId))
                    assignedWorkflowIds.Add(work.WorkflowRunId);
            }
        }

        Assert.Equal(3, assigned.Count);
        Assert.Contains(workflowA, assignedWorkflowIds);
        Assert.Contains(workflowB, assignedWorkflowIds);
        Assert.Contains(workflowC, assignedWorkflowIds);
    }
}
