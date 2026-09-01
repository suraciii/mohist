using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Workflow.Domain;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using System.Text.Json;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Runner.Grain;

[Collection("RunnerGrain")]
public partial class DispatchServiceReconciliationSpecs : Mohist.Server.L1Tests.Specs.Workflow.WorkflowGrainSpecs
{
    public DispatchServiceReconciliationSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

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
    public async Task ActiveChecks_ConflictingPullRequestCarrier_FailsThroughCheckFailurePath()
    {
        var (workflow, runnerId, _) = await StartActiveChecksWithPullRequestIdentityAsync();
        await SetConflictingRunCarrierAsync();

        var response = await Dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

        Assert.Empty(response.Dispatches);
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(FailureReason.CheckFailed, run.Failure?.Reason);
        Assert.Contains("pull_request_identity_conflict", run.Failure?.Message, StringComparison.Ordinal);
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();
        Assert.DoesNotContain(_workflowId!, await querier.FindRunningAssignedToAsync(runnerId));
    }

    [Fact]
    public async Task ActiveChecks_ConflictingPullRequestCarrier_RedeliveryAfterGrainActivationFailsRun()
    {
        var (workflow, runnerId, _) = await StartActiveChecksWithPullRequestIdentityAsync();
        await SetConflictingRunCarrierAsync();
        await DeactivateWorkflowAsync(_workflowId!);

        var response = await Dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

        Assert.Empty(response.Dispatches);
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(FailureReason.CheckFailed, run.Failure?.Reason);
        Assert.Contains("pull_request_identity_conflict", run.Failure?.Message, StringComparison.Ordinal);
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();
        Assert.DoesNotContain(_workflowId!, await querier.FindRunningAssignedToAsync(runnerId));
    }

    [Fact]
    public async Task PostClaimConflict_SettlementFailurePreservesAssignmentAndNextPollSettles()
    {
        await ClearBacklogAsync();
        _workflowId = $"post-claim-identity-{Guid.NewGuid():N}";
        _runnerId = await RegisterRunnerAsync();
        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId);
        await SeedWorkflowTemplateAsync(
            _workflowId,
            SingleStage(
                tasks: [new("task-1", "Task 1", "spec/task")],
                checks: [new("check-1", "Check 1", "spec/check")]),
            TestProjectId(_workflowId));
        var repository = new WorkflowRepositoryContext(
            "web",
            "https://github.com/octocat/hello-world.git",
            "master");
        await workflow.EnsureStartedAsync(
            new WorkflowIssueContext(TestProjectId(_workflowId), 1, null),
            new WorkflowStartSnapshot(repository, null));
        await workflow.PatchVariablesAsync(new VariableBundle(Vars: JsonSerializer.SerializeToElement(new
        {
            github = new { pr = new { number = 42 } },
        })));
        Assert.Equal(WorkflowAssignmentStatus.Assigned,
            (await workflow.AssignWorkerAsync(_runnerId)).Status);

        var initial = await PollWorkAsync(_runnerId);
        await ReportAsync(_runnerId, initial.Work, "completed");

        var checksWorkId = WorkflowRunExtensions.ChecksWorkIdFor("build");
        _fixture.ReportPersistenceFailures.FailNextWorkflowReport(_workflowId, checksWorkId);
        _fixture.DispatchPollObserver.BeforeWorkflowClaim = async workflowRunId =>
        {
            Assert.Equal(_workflowId, workflowRunId);
            await SetConflictingRunCarrierAsync();
            _fixture.DispatchPollObserver.BeforeWorkflowClaim = null;
        };

        try
        {
            var first = await Dispatch.PollAsync(
                _runnerId,
                new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));
            Assert.Empty(first.Dispatches);

            var stillAssigned = await LoadRunAsync(_workflowId);
            Assert.Equal(WorkflowRunStatus.Running, stillAssigned.Status);
            Assert.Equal(_runnerId, stillAssigned.Assignment?.WorkerId);
            await DeactivateWorkflowAsync(_workflowId);

            var second = await Dispatch.PollAsync(
                _runnerId,
                new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));
            Assert.Empty(second.Dispatches);

            var settled = await LoadRunAsync(_workflowId);
            Assert.Equal(WorkflowRunStatus.Failed, settled.Status);
            Assert.Equal(FailureReason.CheckFailed, settled.Failure?.Reason);
            Assert.Contains("pull_request_identity_conflict", settled.Failure?.Message, StringComparison.Ordinal);
            using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
            var querier = scope.ServiceProvider.GetRequiredService<WorkflowRunQuerier>();
            Assert.DoesNotContain(_workflowId, await querier.FindRunningAssignedToAsync(_runnerId));
        }
        finally
        {
            _fixture.DispatchPollObserver.BeforeWorkflowClaim = null;
        }
    }

    private async Task<(IWorkflowGrain Workflow, string RunnerId, WorkDispatch Checks)> StartActiveChecksWithPullRequestIdentityAsync()
    {
        await ClearBacklogAsync();
        _workflowId = $"active-check-identity-{Guid.NewGuid():N}";
        _runnerId = await RegisterRunnerAsync();
        var workflow = Grains.GetGrain<IWorkflowGrain>(_workflowId);
        await SeedWorkflowTemplateAsync(_workflowId, SingleStage(checks: [new("check-1", "Check 1", "spec/check")]));
        var repository = new WorkflowRepositoryContext(
            "web",
            "https://github.com/octocat/hello-world.git",
            "master");
        await workflow.EnsureStartedAsync(
            new WorkflowIssueContext(TestProjectId(_workflowId), 1, null),
            new WorkflowStartSnapshot(repository, null));
        await workflow.PatchVariablesAsync(new VariableBundle(Vars: JsonSerializer.SerializeToElement(new
        {
            github = new { pr = new { number = 42 } },
        })));

        var (task, taskRunnerId) = await PollWorkAnyAsync();
        await ReportAsync(taskRunnerId, task.WorkId, "completed");
        var (checks, checksRunnerId) = await PollWorkAnyAsync();
        Assert.Equal(_runnerId, checksRunnerId);
        return (workflow, checksRunnerId, checks);
    }

    private async Task SetConflictingRunCarrierAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var row = await db.WorkflowRunProfiles
            .SingleAsync(value => value.WorkflowRunId == _workflowId);
        row.Variables = new VariableBundle(Vars: JsonSerializer.SerializeToElement(new
        {
            github = new { pr = new { number = 43 } },
        })).ToJson();
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Redelivery_UsesPersistedDispatchSnapshotAfterGrainActivation()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var first = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

        await using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateAsyncScope();
        var snapshotStore = scope.ServiceProvider.GetRequiredService<IDispatchSnapshotStore>();
        var storedJson = await snapshotStore.LoadJsonAsync(_workflowId!, first.WorkId);
        Assert.Equal(first, JSON.Deserialize<WorkDispatch>(storedJson!));

        await TestLifecycle.Deactivate(workflow);
        var redelivery = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
        Assert.Equal(first, redelivery);
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

        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

        var redelivery = Assert.Single(resp.Dispatches);
        Assert.Equal(_workflowId, redelivery.WorkflowRunId);
        Assert.Equal(workId, redelivery.WorkId);
    }

    [Fact]
    public async Task Reconnect_DoesNotRedeliverWorkClosedWithTheLostGeneration()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var first = Assert.Single(
            (await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

        await runner.UnregisterAsync();
        await runner.RegisterAsync(
            new RunnerInfo(runnerId, ["spec/*"], "test-host", TestProjectId(_workflowId!)),
            "replacement-generation");

        Assert.Empty((await Dispatch.PollAsync(
            runnerId,
            new RunnerPollRequest([], [], ProcessGeneration: "replacement-generation"))).Dispatches);
        Assert.Equal("Failed", await workflow.GetRunStatusAsync());

        Assert.Equal(WorkReportVerdict.Refused, await workflow.ReceiveTaskReportAsync(
            runnerId,
            first.WorkId,
            new TaskReport(
                first.WorkId,
                TaskReportStatus.Succeeded,
                Output: null,
                Artifacts: null,
                ActionAttemptId: first.ActionAttemptId)));
        Assert.Empty((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: "replacement-generation"))).Dispatches);
    }

    [Fact]
    public async Task Reconnect_DoesNotTakeInterruptedWorkflowOverFromRecordedRunner()
    {
        await StartWorkflowAsync(SingleStage(checks: []));
        var originalRunnerId = _runnerId!;
        var originalRunner = Grains.GetGrain<IRunnerGrain>(originalRunnerId);
        var first = Assert.Single(
            (await Dispatch.PollAsync(originalRunnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
        await originalRunner.UnregisterAsync();

        var otherRunnerId = $"other-recovery-runner-{Guid.NewGuid():N}";
        var otherRunner = Grains.GetGrain<IRunnerGrain>(otherRunnerId);
        await otherRunner.RegisterAsync(new RunnerInfo(
            otherRunnerId,
            ["spec/*"],
            "other-host",
            TestProjectId(_workflowId!)));

        Assert.Empty((await Dispatch.PollAsync(otherRunnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(originalRunnerId, run.Assignment?.WorkerId);
        Assert.Equal(first.WorkId, run.CurrentStage().Tasks.Single().WorkId);

        await otherRunner.UnregisterAsync();
    }


    [Fact]
    public async Task Dispatch_MissingAgent_PersistsAgentNotFoundOnWorkflowActionAttemptAndFailure()
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
        var claimed = await workflow.ClaimNextAsync(runnerId, "test-generation");
        Assert.Null(claimed);

        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(WorkflowActionAttemptStatus.Failed, task.Status);
        Assert.Equal("agent_not_found", task.Error?.Code);
        Assert.Equal("agent_not_found", run.Failure?.Error?.Code);
        Assert.Contains("reviewer", run.Failure?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispatch_ArchivedAgent_PersistsAgentNotFoundOnWorkflowActionAttemptAndFailure()
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
        var claimed = await workflow.ClaimNextAsync(runnerId, "test-generation");
        Assert.Null(claimed);

        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        var task = Assert.Single(run.CurrentStage().Tasks);
        Assert.Equal(WorkflowActionAttemptStatus.Failed, task.Status);
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

        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([key], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

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

        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [key], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

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

        var dispatch = Assert.Single((await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration))).Dispatches);

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
        await InsertStatusRowAsync($"{prefix}-blocked", "Running", runnerA, activeWork: false);
        await InsertStatusRowAsync($"{prefix}-mismatched-active-worker", "Running", runnerA, activeWorkerId: runnerB);
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

        var resp = await Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));

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
            var poll = Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));
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
                new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration),
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
            var poll = Dispatch.PollAsync(runnerId, new RunnerPollRequest([], [], ProcessGeneration: TestRunnerGenerationExtensions.ProcessGeneration));
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


    private async Task InsertStatusRowAsync(
        string workflowRunId,
        string status,
        string runnerId,
        bool activeWork = true,
        string? activeWorkerId = null)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        var run = WorkflowRun.Create(workflowRunId, new WorkflowDefinition([
            new StageDefinition("build", [new TaskDefinition("task-1", "Task 1", "spec/task")], [])
        ]), DateTimeOffset.UnixEpoch);
        run.Stages.Clear();
        run.Stages.Add(new StageRun
        {
            Id = "build", Attempt = 1, Initialized = true, RequiresApproval = false,
            Status = StageRunStatus.Running,
            Tasks = { new WorkflowActionAttempt
            {
                Id = "task-1", DefinitionId = "task-1", Attempt = 1, Title = "Task 1",
                Status = status == "Running" ? WorkflowActionAttemptStatus.Running : WorkflowActionAttemptStatus.Pending,
                WorkerId = runnerId,
            } },
        });
        run.CurrentStageId = "build";
        run.Status = Enum.Parse<WorkflowRunStatus>(status);
        run.Assignment = new WorkflowAssignment(runnerId, TestTime.UtcNow);
        var projection = WorkflowRunWorkProjectionBuilder.Build(run);
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId, State = JSON.Serialize(run),
            ActiveWorkId = activeWork ? projection.ActiveWorkId : null,
            ActiveWorkerId = activeWork ? activeWorkerId ?? projection.ActiveWorkerId : null,
            AttentionStatus = null,
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
            Id = id, ProjectId = projectId, Name = agentName, Status = AgentStatus.Archived,
            State = JsonSerializer.Serialize(new Mohist.Server.Agent.Domain.Agent
            {
                Id = id, ProjectId = projectId, Name = agentName, Status = AgentStatus.Archived,
            }, JSON.Options),
        });
        await db.SaveChangesAsync();
    }
}
