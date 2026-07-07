using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.Tests.Specs.Runner.Grain;

/// <summary>
/// Specs for the stateless <see cref="DispatchService"/> reconciliation paths
/// (design/workflow/scheduling.md §Poll Reconciliation). One poll = one round:
/// ① touch presence ② desired (Running assigned to me) ③ repair (desired −
/// reported) ④ spare claim. These specs pin the core reconciliation contract
/// — repair re-dispatches a work the process lost, a reported work is NOT
/// re-dispatched, and the desired query drives both repair and the slot gate.
/// </summary>
[Collection("WorkflowGrain")]
public class DispatchServiceReconciliationSpecs : Mohist.Server.Tests.Specs.Workflow.WorkflowGrainSpecs
{
    public DispatchServiceReconciliationSpecs(Mohist.Server.Tests.Specs.Workflow.WorkflowGrainFixture fixture) : base(fixture) { }

    // Resolve the scoped DispatchService the same way the /poll route does.
    private DispatchService Dispatch => _fixture.Cluster.GetSiloServiceProvider(null)
        .GetRequiredService<IServiceScopeFactory>().CreateScope()
        .ServiceProvider.GetRequiredService<DispatchService>();

    private static string WorkKey(string workflowRunId, string workId) =>
        $"{WorkDispatchOwnerKinds.Workflow}:{workflowRunId}:{workId}";

    /// <summary>
    /// Repair path: a Running work the process does NOT report (it lost the
    /// dispatch, or restarted with empty memory) is re-dispatched. The work is
    /// already Running (claimed), so no new claim happens — the dispatch is a
    /// pure re-render from the persisted run.
    /// </summary>
    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Repair_RedeliversRunningWork_WhenProcessDoesNotReportIt()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        // First poll claims the work → the run's stage task is now Running.
        var first = await runner.PollAsync(Services);
        Assert.NotNull(first);
        var workId = first!.WorkId;
        var key = WorkKey(_workflowId!, workId);

        // Second poll reports NOTHING for the in-flight work (the process
        // never had it, or lost it). desired − reported must re-dispatch it.
        var slots = await runner.GetSlotsAsync();
        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []), slots);

        var repair = Assert.Single(resp.Dispatches);
        Assert.Equal(_workflowId, repair.WorkflowRunId);
        Assert.Equal(workId, repair.WorkId);
    }

    /// <summary>
    /// Reported-set suppression (the idempotent half of reconciliation): when
    /// the process reports the in-flight work in its poll body (inFlight), the
    /// server must NOT re-dispatch it. This is the contract that prevents a
    /// rollback storm — a transiently failing poll must not make every held
    /// work vanish from the report and be re-dispatched.
    /// </summary>
    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Repair_DoesNotRedeliver_WhenProcessReportsTheWorkInFlight()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var first = await runner.PollAsync(Services);
        Assert.NotNull(first);
        var key = WorkKey(_workflowId!, first!.WorkId);

        // The process reports the work as in-flight — it has it and is running
        // it. desired − reported is empty; no dispatch this round.
        var slots = await runner.GetSlotsAsync();
        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([key], []), slots);

        Assert.Empty(resp.Dispatches);
    }

    /// <summary>
    /// Awaiting-ack is also reported: a work whose result is in flight on the
    /// wire (not yet acked by the owner) stays in the report and must NOT be
    /// re-dispatched. Re-dispatching it would duplicate execution of a work
    /// whose result the owner is about to consume.
    /// </summary>
    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Repair_DoesNotRedeliver_WhenWorkIsAwaitingAck()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var first = await runner.PollAsync(Services);
        Assert.NotNull(first);
        var key = WorkKey(_workflowId!, first!.WorkId);

        var slots = await runner.GetSlotsAsync();
        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [key]), slots);

        Assert.Empty(resp.Dispatches);
    }

    /// <summary>
    /// The desired query (<see cref="WorkflowRunQuerier.FindRunningAssignedToAsync"/>)
    /// is the foundation of both repair and the slot gate. It returns only
    /// Running runs bound to the given runner — not Ready, not terminal, not
    /// another runner's. Repair and the slot count both depend on this being
    /// an accurate reflection of "what is actually executing for me".
    /// </summary>
    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task FindRunningAssignedToAsync_ReturnsOnlyRunningForTheRunner()
    {
        var prefix = $"desired-{Guid.NewGuid():N}";
        var runnerA = $"{prefix}-runner-A";
        var runnerB = $"{prefix}-runner-B";

        await InsertStatusRowAsync($"{prefix}-run-1", "Running", runnerA);
        await InsertStatusRowAsync($"{prefix}-run-2", "Running", runnerA);
        await InsertStatusRowAsync($"{prefix}-ready-A", "Ready", runnerA);
        await InsertStatusRowAsync($"{prefix}-completed-A", "Completed", runnerA);
        await InsertStatusRowAsync($"{prefix}-run-B", "Running", runnerB);

        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();

        var forA = await querier.FindRunningAssignedToAsync(runnerA);
        Assert.Equal(new[] { $"{prefix}-run-1", $"{prefix}-run-2" }, forA.Order());

        var forB = await querier.FindRunningAssignedToAsync(runnerB);
        Assert.Equal(new[] { $"{prefix}-run-B" }, forB);

        // An unknown runner gets nothing — not an error.
        Assert.Empty(await querier.FindRunningAssignedToAsync($"{prefix}-runner-unknown"));
    }

    /// <summary>
    /// Offline runners (info cleared on unregister) must not claim or be
    /// re-dispatched work — a stale/offline poll is a harmless empty round.
    /// </summary>
    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task PollAsync_OfflineRunner_ReturnsEmptyRound()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        // Take the runner offline (info cleared). The workflow stays Ready and
        // claimable, but an offline runner must not pick it up.
        await runner.UnregisterAsync();

        var slots = await runner.GetSlotsAsync();
        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []), slots);

        Assert.Empty(resp.Dispatches);
    }

    /// <summary>
    /// Mirrors <c>RunnerPollSchedulingSpecs.InsertStatusRowAsync</c> — seeds a
    /// WorkflowRuns row with the requested status/runner so the querier sees
    /// it without driving a full claim. Used here only for the desired-query
    /// spec.
    /// </summary>
    private async Task InsertStatusRowAsync(string workflowRunId, string status, string runnerId)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var run = WorkflowRun.Create(
            workflowRunId,
            new WorkflowDefinition("spec/workflow",
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
