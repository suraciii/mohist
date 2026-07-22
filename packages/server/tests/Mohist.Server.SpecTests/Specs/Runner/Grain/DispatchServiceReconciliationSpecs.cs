using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public class DispatchServiceReconciliationSpecs : Mohist.Server.SpecTests.Specs.Workflow.WorkflowGrainSpecs
{
    public DispatchServiceReconciliationSpecs(Mohist.Server.SpecTests.Specs.Workflow.WorkflowGrainFixture fixture) : base(fixture) { }

    private DispatchService Dispatch => _fixture.Cluster.GetSiloServiceProvider(null)
        .GetRequiredService<IServiceScopeFactory>().CreateScope()
        .ServiceProvider.GetRequiredService<DispatchService>();

    private static string WorkKey(string workflowRunId, string workId) =>
        $"{WorkDispatchOwnerKinds.Workflow}:{workflowRunId}:{workId}";

    private async Task<(string RunnerId, string[] WorkflowIds)> StartReadyWorkflowsAsync(
        string prefix,
        int count,
        int slots)
    {
        await ClearBacklogAsync();
        var projectId = $"{prefix}-project";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, $"{prefix}-runner", slots);
        var workflowIds = new string[count];
        for (var index = 0; index < count; index++)
        {
            var workflowId = $"{prefix}-workflow-{index}";
            var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
            await SeedWorkflowTemplateAsync(workflowId, SingleStage(checks: []), projectId);
            await workflow.StartAsync(TestInput(projectId));
            workflowIds[index] = workflowId;
        }
        return (runnerId, workflowIds);
    }

    [Fact]
    public async Task Redelivery_RedeliversRunningWork_WhenProcessDoesNotReportIt()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var first = await runner.PollAsync(Services);
        Assert.NotNull(first);
        var workId = first!.WorkId;

        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));

        var redelivery = Assert.Single(resp.Dispatches);
        Assert.Equal(_workflowId, redelivery.WorkflowRunId);
        Assert.Equal(workId, redelivery.WorkId);
    }

    [Fact]
    public async Task Redelivery_InvalidPersistedTaskInput_FailsClaimedWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new TaskDefinition(
                    "recover:fix-review-findings",
                    "Fix review findings",
                    "mohist/opencode",
                    With("""{"session":"check","prompt":"fix","agent":"${{ vars.agent }}"}""")),
            ],
            checks: [],
            stage: "check"));
        var runnerId = _runnerId!;

        var assignment = await workflow.AssignWorkerAsync(runnerId);
        Assert.Equal(WorkflowAssignmentStatus.Assigned, assignment.Status);
        var claimed = await workflow.ClaimNextAsync(runnerId);
        Assert.NotNull(claimed);

        var response = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));

        Assert.Empty(response.Dispatches);
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Contains("with.agent", run.Failure?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redelivery_DoesNotRedeliver_WhenProcessReportsTheWorkInFlight()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var first = await runner.PollAsync(Services);
        Assert.NotNull(first);
        var key = WorkKey(_workflowId!, first!.WorkId);

        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([key], []));

        Assert.Empty(resp.Dispatches);
    }

    [Fact]
    public async Task Redelivery_DoesNotRedeliver_WhenWorkIsAwaitingAck()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var first = await runner.PollAsync(Services);
        Assert.NotNull(first);
        var key = WorkKey(_workflowId!, first!.WorkId);

        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [key]));

        Assert.Empty(resp.Dispatches);
    }

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

        Assert.Empty(await querier.FindRunningAssignedToAsync($"{prefix}-runner-unknown"));
    }

    [Fact]
    public async Task PollAsync_OfflineRunner_ReturnsEmptyRound()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.UnregisterAsync();

        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));

        Assert.Empty(resp.Dispatches);
    }

    [Fact]
    public async Task PollAsync_UnregisterAfterInfoRead_DoesNotAssignWorkflow()
    {
        var (runnerId, workflowIds) = await StartReadyWorkflowsAsync(
            $"poll-unregister-{Guid.NewGuid():N}", count: 1, slots: 1);
        _fixture.DispatchPollObserver.Reset();
        _fixture.DispatchPollObserver.BlockAfterRunnerInfo();

        try
        {
            var poll = Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));
            await _fixture.DispatchPollObserver.WaitForRunnerInfoAsync();

            await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();
            _fixture.DispatchPollObserver.ReleaseAfterRunnerInfo();

            Assert.Empty((await poll).Dispatches);
            var workflow = Grains.GetGrain<IWorkflowGrain>(workflowIds[0]);
            Assert.Null(await workflow.GetAssignedWorkerIdAsync());
            Assert.Equal("Pending", await workflow.GetRunStatusAsync());
        }
        finally
        {
            _fixture.DispatchPollObserver.ReleaseAfterRunnerInfo();
        }
    }

    [Fact]
    public async Task PollAsync_CapacityReducedAfterInfoRead_ClaimsAtMostNewCapacity()
    {
        var (runnerId, workflowIds) = await StartReadyWorkflowsAsync(
            $"poll-capacity-{Guid.NewGuid():N}", count: 2, slots: 2);
        _fixture.DispatchPollObserver.Reset();
        _fixture.DispatchPollObserver.BlockAfterRunnerInfo();

        try
        {
            var poll = Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));
            await _fixture.DispatchPollObserver.WaitForRunnerInfoAsync();

            await Grains.GetGrain<IRunnerGrain>(runnerId).UpdateAsync(1);
            _fixture.DispatchPollObserver.ReleaseAfterRunnerInfo();

            var response = await poll;
            Assert.Single(response.Dispatches);
            var statuses = await Task.WhenAll(workflowIds.Select(async workflowId =>
                await Grains.GetGrain<IWorkflowGrain>(workflowId).GetRunStatusAsync()));
            Assert.Equal(1, statuses.Count(status => status == "Running"));
            Assert.Equal(1, statuses.Count(status => status == "Pending"));
        }
        finally
        {
            _fixture.DispatchPollObserver.ReleaseAfterRunnerInfo();
        }
    }

    private async Task InsertStatusRowAsync(string workflowRunId, string status, string runnerId)
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
