using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using System.Text.Json;
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

    [Fact]
    public async Task RuntimeReadinessFence_RejectsFreshClaimsButRedeliversHeldWork()
    {
        await ClearBacklogAsync();
        var workflowId = $"runtime-readiness-{Guid.NewGuid():N}";
        var projectId = TestProjectId(workflowId);
        var runnerId = $"runtime-readiness-runner-{Guid.NewGuid():N}";
        _workflowId = workflowId;
        _runnerId = runnerId;

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            projectId,
            ConnectionGeneration: "connection-current"));

        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await SeedWorkflowTemplateAsync(
            workflowId,
            SingleStage(tasks: [new TaskDefinition("agent", "Agent", "mohist/pi")], checks: []),
            projectId);
        await workflow.StartAsync(TestInput(projectId));

        var staleConnection = await Dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest(
                [],
                [],
                [new RuntimeReadinessWitness("pi", true, 1)],
                ConnectionGeneration: "connection-stale",
                AdmissionReady: true));
        Assert.Empty(staleConnection.Dispatches);

        var unhealthy = await Dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest(
                [],
                [],
                [new RuntimeReadinessWitness("pi", false, 1)],
                ConnectionGeneration: "connection-current",
                AdmissionReady: true));
        Assert.Empty(unhealthy.Dispatches);

        var first = Assert.Single((await Dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest(
                [],
                [],
                [new RuntimeReadinessWitness("pi", true, 1)],
                ConnectionGeneration: "connection-current",
                AdmissionReady: true))).Dispatches);

        var redelivery = await Dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest(
                [],
                [],
                [new RuntimeReadinessWitness("pi", false, 1)],
                ConnectionGeneration: "connection-current",
                AdmissionReady: true));
        Assert.Equal(first, Assert.Single(redelivery.Dispatches));
    }

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
    public async Task Redelivery_UsesPersistedDispatchSnapshotAfterGrainActivation()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var first = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var snapshotStore = scope.ServiceProvider.GetRequiredService<IDispatchSnapshotStore>();
        var storedJson = await snapshotStore.LoadJsonAsync(_workflowId!, first.WorkId);
        Assert.Equal(first, JSON.Deserialize<WorkDispatch>(storedJson!));

        await TestLifecycle.Deactivate(workflow);
        var redelivery = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        Assert.Equal(first, redelivery);
    }

    [Fact]
    public async Task RunnerReconnectReport_SuppressesUnresolvedRedeliveryWhileHeld()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/pi")],
            checks: []));
        var runnerId = _runnerId!;
        var work = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        var key = WorkKey(_workflowId!, work.WorkId);

        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentRunnerDisconnectedAsync(runnerId));
        Assert.Empty((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([key], []))).Dispatches);

        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveTaskReportAsync(
            runnerId,
            work.WorkId,
            new TaskReport(
                work.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: work.TaskRunId)));

        var recovered = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed, Assert.Single(recovered.CurrentStage().Tasks).Status);
    }

    [Fact]
    public async Task Redelivery_UnresolvedAgentWork_RedeliversToRecordedRunnerWithBinding()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/pi")],
            checks: []));
        var runnerId = _runnerId!;
        var first = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(new AgentExecutionBinding(
            first.TaskRunId!,
            first.WorkId,
            runnerId,
            "session-1",
            "turn-1",
            "pi",
            "/pi/sessions/spec-1")));

        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentRunnerDisconnectedAsync(runnerId));

        var redelivery = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        Assert.Equal(first.WorkflowRunId, redelivery.WorkflowRunId);
        Assert.Equal(first.WorkId, redelivery.WorkId);
        Assert.Equal(first.TaskRunId, redelivery.TaskRunId);
        var binding = Assert.IsType<AgentRecoveryBinding>(redelivery.AgentRecovery);
        Assert.Equal("pi", binding.Runtime);
        Assert.Equal("/pi/sessions/spec-1", binding.RuntimeSessionId);
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Running, Assert.Single(run.CurrentStage().Tasks).Status);
    }

    [Fact]
    public async Task Redelivery_UnresolvedAgentWork_RequiresFullRuntimeBinding()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/pi")],
            checks: []));
        var runnerId = _runnerId!;
        var work = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);

        // An unknown observation without a bound execution leaves the
        // settlement without runtime facts; redelivery stays closed.
        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentResultUnknownAsync(
            runnerId,
            work.TaskRunId!,
            work.WorkId,
            "runner-restarted"));

        Assert.Empty((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(AgentResultSettlementState.Unknown, Assert.Single(run.CurrentStage().Tasks).AgentResultSettlement!.State);
    }

    [Fact]
    public async Task Redelivery_BlockedUnresolvedAgentWork_StillRedeliversUntilAuthoritativeResult()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks: [new TaskDefinition("agent", "Agent", "mohist/pi")],
            checks: []));
        var runnerId = _runnerId!;
        var first = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        Assert.Equal(ReportAck.Accepted, await workflow.BindAgentExecutionAsync(new AgentExecutionBinding(
            first.TaskRunId!,
            first.WorkId,
            runnerId,
            "session-blocked",
            "turn-blocked",
            "pi",
            "/pi/sessions/spec-blocked")));

        Assert.Equal(ReportAck.Accepted, await workflow.ObserveAgentRunnerDisconnectedAsync(runnerId));
        var unsettled = await LoadRunAsync(_workflowId!);
        var deadline = Assert.Single(unsettled.CurrentStage().Tasks).AgentResultSettlement!.DeadlineAt!.Value;
        _fixture.TimeProvider.Advance(deadline - _fixture.TimeProvider.GetUtcNow());
        await workflow.ReceiveReminder(WorkflowGrain.AgentResultSettlementReminderName, default);

        var blocked = await LoadRunAsync(_workflowId!);
        Assert.Equal(AgentResultSettlementState.Blocked, Assert.Single(blocked.CurrentStage().Tasks).AgentResultSettlement!.State);

        var redelivery = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        Assert.NotNull(redelivery.AgentRecovery);
        Assert.Equal(first.WorkId, redelivery.WorkId);

        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveTaskReportAsync(
            runnerId,
            first.WorkId,
            new TaskReport(
                first.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: first.TaskRunId)));

        var recovered = await LoadRunAsync(_workflowId!);
        Assert.Equal(TaskRunStatus.Completed, Assert.Single(recovered.CurrentStage().Tasks).Status);
        Assert.False(recovered.HasUnresolvedAgentResult());
        Assert.Empty((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
    }

    [Fact]
    public async Task Redelivery_UnresolvedAgentWork_IsDeliveredRegardlessOfDispatchSlots()
    {
        await ClearBacklogAsync();
        var projectId = $"recovery-slots-{Guid.NewGuid():N}";
        var runnerId = await RegisterRunnerForProjectAsync(projectId, maxWorkflowSlots: 1);
        _runnerId = runnerId;

        var recoveryWorkflow = Grains.GetGrain<IWorkflowGrain>($"{projectId}-recovery");
        await SeedWorkflowTemplateAsync(
            $"{projectId}-recovery",
            SingleStage(tasks: [new TaskDefinition("agent", "Agent", "mohist/pi")], checks: []),
            projectId);
        await recoveryWorkflow.StartAsync(TestInput(projectId));
        var work = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        Assert.Equal(ReportAck.Accepted, await recoveryWorkflow.BindAgentExecutionAsync(new AgentExecutionBinding(
            work.TaskRunId!,
            work.WorkId,
            runnerId,
            "session-slots",
            "turn-slots",
            "pi",
            "/pi/sessions/spec-slots")));
        Assert.Equal(ReportAck.Accepted, await recoveryWorkflow.ObserveAgentRunnerDisconnectedAsync(runnerId));

        var freshWorkflow = Grains.GetGrain<IWorkflowGrain>($"{projectId}-fresh");
        await SeedWorkflowTemplateAsync($"{projectId}-fresh", SingleStage(checks: []), projectId);
        await freshWorkflow.StartAsync(TestInput(projectId));

        // The runner holds no reported work, so the recovery render must not
        // consume the dispatch slot even at capacity.
        var response = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []));
        var recovery = Assert.Single(response.Dispatches);
        Assert.Equal(work.WorkId, recovery.WorkId);
        Assert.NotNull(recovery.AgentRecovery);
    }

    [Fact]
    public async Task Redelivery_UnresolvedAgentWork_RefusesRunnerOtherThanTheRecordedSettlement()
    {
        await ClearBacklogAsync();
        var prefix = $"recovery-runner-{Guid.NewGuid():N}";
        var recordedRunner = $"{prefix}-recorded";
        var otherRunner = $"{prefix}-other";
        var projectId = $"{prefix}-project";
        foreach (var runnerId in new[] { recordedRunner, otherRunner })
        {
            await Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(
                new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));
        }

        var workflowRunId = $"{prefix}-run";
        await InsertUnresolvedAgentRunAsync(workflowRunId, otherRunner, recordedRunner, binding: true);

        // The run is assigned to the other runner, but the settlement was
        // recorded against the recorded runner: neither may take it over.
        Assert.Empty((await Dispatch.PollAsync(otherRunner, new RunnerPollRequest([], []))).Dispatches);
    }

    private async Task InsertUnresolvedAgentRunAsync(
        string workflowRunId,
        string assignedRunner,
        string settlementRunner,
        bool binding)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();

        var run = WorkflowRun.Create(
            workflowRunId,
            new WorkflowDefinition(
            [new StageDefinition("build",
                [new TaskDefinition("agent", "Agent", "mohist/pi")],
                [])]),
            DateTimeOffset.UnixEpoch);
        var task = new TaskRun
        {
            Id = "agent",
            DefinitionId = "agent",
            Attempt = 1,
            Title = "Agent",
            Uses = "mohist/pi",
            Status = TaskRunStatus.Running,
            WorkId = "agent",
            WorkerId = assignedRunner,
            AgentResultSettlement = new AgentResultSettlement
            {
                State = AgentResultSettlementState.Unknown,
                TaskRunId = "agent",
                WorkId = "agent",
                RunnerId = settlementRunner,
                Runtime = binding ? "pi" : null,
                RuntimeSessionId = binding ? "/pi/sessions/spec" : null,
            },
        };
        run.Stages.Clear();
        run.Stages.Add(new StageRun
        {
            Id = "build",
            Attempt = 1,
            Initialized = true,
            RequiresApproval = false,
            Status = StageRunStatus.Running,
            Tasks = { task },
        });
        run.CurrentStageId = "build";
        run.Status = WorkflowRunStatus.Running;
        run.Assignment = new WorkflowAssignment(assignedRunner, TestTime.UtcNow);

        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = JSON.Serialize(run),
        });
        await db.SaveChangesAsync();
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
    public async Task Reconnect_RedeliversInterruptedWorkflowUnderOriginalIdentity()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var first = Assert.Single(
            (await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);

        await runner.UnregisterAsync();
        Assert.Empty((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);

        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "test-host",
            TestProjectId(_workflowId!)));

        var redelivery = Assert.Single(
            (await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        Assert.Equal(first, redelivery);
        Assert.Equal(first.WorkId, redelivery.WorkId);
        Assert.Equal(first.TaskRunId, redelivery.TaskRunId);

        var repeated = Assert.Single(
            (await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
        Assert.Equal(first, repeated);

        var key = WorkKey(_workflowId!, first.WorkId);
        Assert.Empty((await Dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest([key], []))).Dispatches);

        Assert.Equal(ReportAck.Accepted, await workflow.ReceiveTaskReportAsync(
            runnerId,
            first.WorkId,
            new TaskReport(
                first.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                TaskRunId: first.TaskRunId)));
        Assert.Empty((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);
    }

    [Fact]
    public async Task Reconnect_DoesNotTakeInterruptedWorkflowOverFromRecordedRunner()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var originalRunnerId = _runnerId!;
        var originalRunner = Grains.GetGrain<IRunnerGrain>(originalRunnerId);
        var first = Assert.Single(
            (await Dispatch.PollAsync(originalRunnerId, new RunnerPollRequest([], []))).Dispatches);
        await originalRunner.UnregisterAsync();

        var otherRunnerId = $"other-recovery-runner-{Guid.NewGuid():N}";
        var otherRunner = Grains.GetGrain<IRunnerGrain>(otherRunnerId);
        await otherRunner.RegisterAsync(new RunnerInfo(
            otherRunnerId,
            ["spec/*"],
            "other-host",
            TestProjectId(_workflowId!)));

        Assert.Empty((await Dispatch.PollAsync(otherRunnerId, new RunnerPollRequest([], []))).Dispatches);
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(originalRunnerId, run.Assignment?.WorkerId);
        Assert.Equal(first.WorkId, run.CurrentStage().Tasks.Single().WorkId);

        await otherRunner.UnregisterAsync();
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
    public async Task Dispatch_MissingAgent_PersistsAgentNotFoundOnTaskRunAndFailure()
    {
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new TaskDefinition(
                    "reviewer",
                    "Use Agent reviewer",
                    "mohist/agent",
                    With("""{"name":"reviewer","prompt":"Review the change."}"""),
                    Recovery: new RecoveryDefinition(
                        1,
                        [new RecoveryHandlerDefinition("failure.error.code=agent_not_found", [], RetrySelf: true)])),
            ],
            checks: [],
            stage: "build"));
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
        Assert.Equal("agent_not_found", task.Error?.Code);
        Assert.Equal("agent_not_found", run.Failure?.Error?.Code);
        Assert.Contains("reviewer", run.Failure?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatch_ArchivedAgent_PersistsAgentNotFoundOnTaskRunAndFailure()
    {
        var projectId = TestProjectId(_workflowId ?? $"wf-{Guid.NewGuid():N}");
        var workflow = await StartWorkflowAsync(SingleStage(
            tasks:
            [
                new TaskDefinition(
                    "reviewer",
                    "Use Agent reviewer",
                    "mohist/agent",
                    With("""{"name":"reviewer","prompt":"Review the change."}""")),
            ],
            checks: [],
            stage: "build"));
        var runnerId = _runnerId!;

        await SeedArchivedAgentAsync(projectId, "reviewer");

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
        Assert.Equal("agent_not_found", task.Error?.Code);
        Assert.Equal("agent_not_found", run.Failure?.Error?.Code);
        Assert.Contains("reviewer", run.Failure?.Message, StringComparison.Ordinal);
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
    public async Task PollAsync_AssignedWorkflowCanClaimItsOwnNextWorkAtCapacity()
    {
        var (runnerId, workflowIds) = await StartReadyWorkflowsAsync(
            $"poll-assigned-capacity-{Guid.NewGuid():N}", count: 1, slots: 1);
        var workflow = Grains.GetGrain<IWorkflowGrain>(Assert.Single(workflowIds));

        var assignment = await workflow.AssignWorkerAsync(runnerId);
        Assert.Equal(WorkflowAssignmentStatus.Assigned, assignment.Status);

        var dispatch = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], []))).Dispatches);

        Assert.Equal(workflowIds[0], dispatch.WorkflowRunId);
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
    public async Task PollAsync_CancelledAfterInfoRead_ReleasesAdmission()
    {
        var (runnerId, _) = await StartReadyWorkflowsAsync(
            $"poll-cancel-{Guid.NewGuid():N}", count: 1, slots: 1);
        _fixture.DispatchPollObserver.Reset();
        _fixture.DispatchPollObserver.BlockAfterRunnerInfo();
        using var cancellation = new CancellationTokenSource();

        try
        {
            var poll = Dispatch.PollAsync(
                runnerId,
                new RunnerPollRequest([], []),
                cancellation.Token);
            await _fixture.DispatchPollObserver.WaitForRunnerInfoAsync();

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);

            var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
            var next = await runner.TryBeginPollAsync();
            Assert.True(next.Admitted);
            await runner.EndPollAsync(next.AdmissionToken);
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

    private async Task SeedArchivedAgentAsync(string projectId, string agentName)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var id = $"agent_{Guid.NewGuid():N}";
        db.Agents.Add(new AgentRow
        {
            Id = id,
            ProjectId = projectId,
            Name = agentName,
            Status = AgentStatus.Archived,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = id,
                ProjectId = projectId,
                Name = agentName,
                Status = AgentStatus.Archived,
            }, Mohist.Server.Infrastructure.JSON.Options),
        });
        await db.SaveChangesAsync();
    }
}
