using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

/// <summary>
/// Issue-318 T-002 specs for the runner-grain poll path. Per design D4:
/// <list type="bullet">
/// <item><c>PollAssignedOrAssignableWorkflowAsync</c> calls <c>PollWorkAsync</c>
/// directly on each <c>FindAssignedToAsync</c> row. The previous
/// <c>GetCurrentWorkIdAsync</c> busy pre-check (~104 grain calls/s) is
/// gone, because the new state machine's <c>Ready</c> status already
/// excludes in-flight work.</item>
/// <item><c>ActiveWorkflowCountAsync</c> counts <c>status == Running</c>
/// rows for the runner via a new <c>CountRunningAssignedToAsync</c> query.
/// The previous implementation reused <c>FindAssignedToAsync</c> +
/// <c>GetCurrentWorkIdAsync</c> and would have collapsed to 0 once
/// <c>Ready</c> excluded in-flight work — so the slot-budget gate in
/// <c>PollAsync</c> would have let the runner exceed its
/// <c>MaxWorkflowSlots</c>.</item>
/// </list>
/// </summary>
[Collection("RunnerGrain")]
public class RunnerPollSchedulingSpecs : Mohist.Server.SpecTests.Specs.Workflow.WorkflowGrainSpecs
{
    public RunnerPollSchedulingSpecs(Mohist.Server.SpecTests.Specs.Workflow.WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task PollAsync_ReadyWorkflowIsDispatchedDirectly()
    {
        // Set up a Pending workflow, assign it to a fresh runner, then
        // poll. The new code path surfaces the workflow through
        // FindAssignedToAsync (status=Ready AND runner=<this>) and
        // calls PollWorkAsync directly — there is no GetCurrentWorkIdAsync
        // pre-check that could short-circuit pickup. The runner should
        // get back a WorkDispatch for the only ready task.
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var work = await runner.PollAsync(Services);

        Assert.NotNull(work);
        Assert.Equal(_workflowId, work!.WorkflowRunId);
    }

    [Fact]
    public async Task PollAsync_RespectsSlotBudget_WhenRunningWorkflowIsAlreadyAssigned()
    {
        var projectId = "runner-slot-budget";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, maxWorkflowSlots: 1);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var workflowAId = $"wf-running-{Guid.NewGuid():N}";
        var workflowBId = $"wf-pending-{Guid.NewGuid():N}";
        var workflowA = Grains.GetGrain<IWorkflowGrain>(workflowAId);
        var workflowB = Grains.GetGrain<IWorkflowGrain>(workflowBId);
        await SeedWorkflowTemplateAsync(workflowAId, SingleStage(checks: []), projectId);
        await SeedWorkflowTemplateAsync(workflowBId, SingleStage(checks: []), projectId);
        await workflowA.StartAsync(TestInput(projectId));
        await workflowA.AssignWorkerAsync(runnerId);
        await workflowB.StartAsync(TestInput(projectId));

        var firstDispatch = await runner.PollAsync(Services);
        Assert.NotNull(firstDispatch);
        Assert.Equal(workflowAId, firstDispatch!.WorkflowRunId);

        for (var i = 0; i < 3; i++)
        {
            var subsequent = await runner.PollAsync(Services);
            if (subsequent is null) continue;
            Assert.NotEqual(workflowBId, subsequent.WorkflowRunId);
        }
    }

    [Fact]
    public async Task CountRunningAssignedToAsync_ReturnsRunningRowsForTheRunner()
    {
        var prefix = $"count-{Guid.NewGuid():N}";
        var runnerA = $"{prefix}-runner-A";
        var runnerB = $"{prefix}-runner-B";

        await InsertStatusRowAsync($"{prefix}-run-1", "Running", runnerA);
        await InsertStatusRowAsync($"{prefix}-run-2", "Running", runnerA);
        await InsertStatusRowAsync($"{prefix}-run-3", "Running", runnerA);
        await InsertStatusRowAsync($"{prefix}-ready-A", "Ready", runnerA);
        await InsertStatusRowAsync($"{prefix}-completed-A", "Completed", runnerA);
        await InsertStatusRowAsync($"{prefix}-run-B", "Running", runnerB);

        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        Assert.Equal(3, await querier.CountRunningAssignedToAsync(runnerA));
        Assert.Equal(1, await querier.CountRunningAssignedToAsync(runnerB));
    }

    private async Task InsertStatusRowAsync(
        string workflowRunId,
        string status,
        string runnerId)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var run = WorkflowRun.Create(
            workflowRunId,
            new WorkflowDefinition(
                [new StageDefinition("build",
                    [new TaskDefinition("task-1", "Task 1", "spec/task")],
                    [])]),
            DateTimeOffset.UnixEpoch);
        run.Stages.Clear();
        run.Stages.Add(new StageRun
        {
            Id = "build",
            Attempt = 1,
            Initialized = true,
            RequiresApproval = false,
            Status = StageRunStatus.Running,
            Tasks =
            {
                new TaskRun
                {
                    Id = "task-1",
                    DefinitionId = "task-1",
                    Attempt = 1,
                    Title = "Task 1",
                    Status = status == "Running"
                        ? TaskRunStatus.Running
                        : TaskRunStatus.Pending,
                },
            },
        });
        run.CurrentStageId = "build";
        run.Status = Enum.Parse<WorkflowRunStatus>(status);
        run.Assignment = new WorkflowAssignment(runnerId, TestTime.UtcNow);

        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JSON.Serialize(run),
        });
        await db.SaveChangesAsync();
    }
}
