using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs.Runner.Grain;

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
[Collection("WorkflowGrain")]
public class RunnerPollSchedulingSpecs : Mohist.Server.Tests.Specs.Workflow.WorkflowGrainSpecs
{
    public RunnerPollSchedulingSpecs(Mohist.Server.Tests.Specs.Workflow.WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task PollAsync_RespectsSlotBudget_WhenRunningWorkflowIsAlreadyAssigned()
    {
        // The slot-budget gate counts the runner's in-flight (desired) work:
        // desired = Running runs assigned to me, and spare = slots − |desired|.
        // With MaxWorkflowSlots=1 and workflowA picked up (now Running), a
        // second poll must NOT claim workflowB — the spare budget is 0.
        //
        // Note: under reconciliation the second poll with an empty reported
        // set may legitimately RE-DISPATCH workflowA (a repair: the runner
        // reported nothing, so the server resends the in-flight work). That
        // re-dispatch is correct and is not a slot-budget violation. The
        // property under test is that workflowB is never claimed.
        var projectId = "runner-slot-budget";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, maxWorkflowSlots: 1);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        // Seed two workflows assigned to the same runner. The first
        // gets picked up and runs to Running; the second is still
        // Pending (unclaimed). After the first poll the runner should
        // refuse the second because its slot budget is exhausted.
        var workflowAId = $"wf-running-{Guid.NewGuid():N}";
        var workflowBId = $"wf-pending-{Guid.NewGuid():N}";
        var workflowA = Grains.GetGrain<IWorkflowGrain>(workflowAId);
        var workflowB = Grains.GetGrain<IWorkflowGrain>(workflowBId);
        await SeedWorkflowTemplateAsync(workflowAId, SingleStage(checks: []), projectId);
        await SeedWorkflowTemplateAsync(workflowBId, SingleStage(checks: []), projectId);
        await workflowA.StartAsync(TestInput(projectId));
        await workflowA.AssignRunnerAsync(runnerId);
        await workflowB.StartAsync(TestInput(projectId));

        var firstDispatch = await runner.PollAsync(Services);
        Assert.NotNull(firstDispatch);
        Assert.Equal(workflowAId, firstDispatch!.WorkflowRunId);

        // The next poll must NOT claim workflowB — the slot budget is 1 and
        // the count sees the in-flight Running workflowA. A repair re-dispatch
        // of workflowA is acceptable; workflowB must never be dispatched.
        for (var i = 0; i < 3; i++)
        {
            var subsequent = await runner.PollAsync(Services);
            if (subsequent is null) continue;
            Assert.NotEqual(workflowBId, subsequent.WorkflowRunId);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task CountRunningAssignedToAsync_ReturnsRunningRowsForTheRunner()
    {
        // Direct spec for the count query — three Running rows for
        // runner A, one for runner B, one Ready for runner A, one
        // terminal for runner A. The querier is the same one
        // ActiveWorkflowCountAsync now uses, so its correctness is
        // what keeps the slot gate honest.
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

    // The two structural source-scan specs that pinned
    // PollAssignedOrAssignableWorkflowAsync / ActiveWorkflowCountAsync in
    // RunnerGrain.cs were removed: those methods no longer exist. Under the
    // reconciliation model the entire poll loop lives in the stateless
    // DispatchService.PollAsync (desired = FindRunningAssignedToAsync,
    // spare = slots − |desired|, repair = desired − reported). The behavioural
    // slot-budget spec below covers the meaningful property.

    /// <summary>
    /// Inserts a <c>WorkflowRuns</c> row with the requested status and
    /// runner. Mirrors the schema the runner-grain reads at runtime:
    /// State.status is camelCase via the JSON serializer and the
    /// STORED Status computed column gets its value from a JSON extract
    /// in the production migration (here populated via the trigger
    /// installed by <c>GrainTestConfig.ApplyWorkflowRunsStatusSchemaFix</c>).
    /// </summary>
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
                "spec/workflow",
                [new StageDefinition("build",
                    [new TaskDefinition("task-1", "Task 1", "spec/task")],
                    [])]));
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
        run.Assignment = new WorkflowAssignment(runnerId, DateTimeOffset.UtcNow);

        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JSON.Serialize(run),
        });
        await db.SaveChangesAsync();
    }
}