using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Specs.Agent.Grain;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.Workflow.Grains;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Runner.Grain;

/// <summary>
/// Concurrency characteristic specs for <see cref="RunnerGrain"/>.
/// Verifies that the authority grain's owned domain and persisted state
/// remain internally consistent when lifecycle and poll operations are
/// issued against the same activation without broad reentrancy. Each
/// scenario prepares the grain in a valid lifecycle phase, fires
/// concurrent calls, and asserts only on the final settled state — the
/// allowed complete serialized outcomes — without depending on
/// scheduler order or interleaving timing.
/// </summary>
[Collection("AgentJobGrain")]
public class RunnerGrainConcurrencySpecs : IAsyncLifetime
{
    private readonly AgentJobGrainFixture _fixture;

    public RunnerGrainConcurrencySpecs(AgentJobGrainFixture fixture)
    {
        _fixture = fixture;
    }

    public ValueTask InitializeAsync()
    {
        _fixture.DispatchObserver.Reset();
        _fixture.RunnerAssignmentObserver.Reset();
        _fixture.CloseoutObserver.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IGrainFactory Grains => _fixture.Grains;
    private IServiceProvider Services => _fixture.Cluster.GetSiloServiceProvider(null);

    private async Task<(string RunnerId, string ProjectId)> RegisterRunnerAsync(
        string runnerId,
        string? projectId = null,
        int maxWorkflowSlots = RunnerCapacity.DefaultMaxWorkflowSlots)
    {
        await ClearBacklogAsync();

        var pid = projectId ?? $"runner-conc-project-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*"],
            "runner-conc-host",
            pid));
        if (maxWorkflowSlots != RunnerCapacity.DefaultMaxWorkflowSlots)
        {
            await runner.UpdateAsync(maxWorkflowSlots);
        }
        return (runnerId, pid);
    }

    private async Task ClearBacklogAsync()
    {
        await ClearGlobalRunnerRegistryAsync();

        var management = Grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);
    }

    private async Task ClearGlobalRunnerRegistryAsync()
    {
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var ids = await registry.ListRunnerIdsAsync();
        foreach (var id in ids)
            await registry.UnregisterAsync(id);
    }

    private static AgentJobInput MakeInput(string prompt, string projectId, string workspacePath = "/tmp/agent-job") =>
        new(Prompt: prompt, WorkspacePath: workspacePath, ProjectId: projectId);

    private static async Task<Exception?> CatchAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<Exception?> CatchAsync<T>(Task<T> task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private async Task<RunnerDefinitionStore> DefinitionStoreAsync()
    {
        await using var scope = Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<RunnerDefinitionStore>();
    }

    private async Task<RunnerWorkRow?> FindRunnerWorkAsync(
        string runnerId,
        string ownerKind,
        string ownerId,
        string workId)
    {
        await using var scope = Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MohistDbContext>();
        return await db.RunnerWorks
            .Where(r =>
                r.RunnerId == runnerId &&
                r.OwnerKind == ownerKind &&
                r.OwnerId == ownerId &&
                r.WorkId == workId)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentTryBeginPollAsync_FromIdle_AdmitsOnlyOne()
    {
        var (runnerId, _) = await RegisterRunnerAsync($"poll-conc-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var admissions = await Task.WhenAll(
            runner.TryBeginPollAsync(),
            runner.TryBeginPollAsync());

        Assert.Single(admissions, admission => admission.Admitted);
        Assert.Single(admissions, admission => !admission.Admitted);

        await runner.EndPollAsync();
    }

    [Fact]
    public async Task ConcurrentUpdateAndHeartbeatRepair_FromRegistered_SettleToConsistentState()
    {
        var (runnerId, projectId) = await RegisterRunnerAsync($"update-repair-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var results = await Task.WhenAll(
            CatchAsync(runner.UpdateAsync(2)),
            CatchAsync(runner.HeartbeatRepairAsync(new RunnerInfo(
                runnerId,
                ["spec/*"],
                "updated-host",
                projectId,
                CoderModels: ["openai/gpt-4"]))));

        Assert.All(results, r => Assert.Null(r));

        var runtime = await runner.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Online, runtime.Status);
        Assert.Equal(2, await runner.GetSlotsAsync());

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Equal("updated-host", info!.Hostname);
        Assert.NotNull(info.CoderModels);
        Assert.Equal(["openai/gpt-4"], info.CoderModels!);

        var definition = await DefinitionStoreAsync();
        Assert.Equal(2, await definition.GetOrInitAsync(runnerId));
    }

    [Fact]
    public async Task ConcurrentUnregisterAndRegister_FromRegistered_SettleToOneSerializedOutcome()
    {
        var runnerId = $"unregister-register-{Guid.NewGuid():N}";
        var (runnerId2, projectId) = await RegisterRunnerAsync(runnerId);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId2);

        var results = await Task.WhenAll(
            CatchAsync(runner.UnregisterAsync()),
            CatchAsync(runner.RegisterAsync(new RunnerInfo(
                runnerId,
                ["spec/*"],
                "re-register-host",
                projectId))));

        Assert.All(results, r => Assert.Null(r));

        var runtime = await runner.GetRuntimeStateAsync();
        var info = await runner.GetInfoAsync();

        if (runtime.Status == RunnerStatus.Offline)
        {
            Assert.Null(info);
        }
        else
        {
            Assert.Equal(RunnerStatus.Online, runtime.Status);
            Assert.NotNull(info);
            Assert.Equal("re-register-host", info!.Hostname);
        }
    }

    [Fact]
    public async Task ConcurrentPollAndLifecycle_FromRegistered_OnlyOnePollAdmittedAndStateConsistent()
    {
        var (runnerId, projectId) = await RegisterRunnerAsync($"poll-lifecycle-{Guid.NewGuid():N}", maxWorkflowSlots: 2);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var admission = await runner.TryBeginPollAsync();
        Assert.True(admission.Admitted);

        var results = await Task.WhenAll(
            CatchAsync(runner.UpdateAsync(1)),
            CatchAsync(runner.HeartbeatRepairAsync(new RunnerInfo(
                runnerId,
                ["spec/*"],
                "poll-host",
                projectId,
                CoderModels: ["anthropic/claude"]))),
            CatchAsync(runner.TryBeginPollAsync()));

        Assert.All(results, r => Assert.Null(r));
        Assert.Equal(1, await runner.GetSlotsAsync());
        Assert.Equal(RunnerStatus.Online, (await runner.GetRuntimeStateAsync()).Status);

        var info = await runner.GetInfoAsync();
        Assert.NotNull(info);
        Assert.Equal("poll-host", info!.Hostname);

        await runner.EndPollAsync();

        var nextAdmission = await runner.TryBeginPollAsync();
        Assert.True(nextAdmission.Admitted);
        Assert.Equal(1, nextAdmission.Slots);
        await runner.EndPollAsync();
    }

    [Fact]
    public async Task HandleTimeoutAsync_DuringAgentJobAssignment_NoDeadlock_RunnerOffline_AssignmentRejected_CloseoutCompleted()
    {
        var (runnerId, projectId) = await RegisterRunnerAsync($"deadlock-runner-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var jobKey = $"deadlock-agent-job-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(jobKey);
        var input = MakeInput("deadlock", projectId, "/tmp/deadlock");
        var options = Services.GetRequiredService<IOptions<AgentJobOptions>>().Value;
        var dispatchRetryBound = options.DispatchRetryBound;

        options.DispatchRetryBound = TimeSpan.FromMinutes(5);
        try
        {
            _fixture.DispatchObserver.FailRunnerAccepted = true;
            await job.SubmitAsync(input);
            await _fixture.DispatchObserver.WaitForRunnerAcceptedAsync();

            var prepared = await job.GetRuntimeSnapshotAsync();
            Assert.Equal(AgentJobStatus.Pending, prepared.Status);
            Assert.Equal(runnerId, prepared.RunnerId);
            Assert.False(string.IsNullOrWhiteSpace(prepared.CurrentWorkId));
            var workId = prepared.CurrentWorkId!;
            Assert.Contains((await runner.GetRuntimeStateAsync()).ActiveWorks,
                work => work.OwnerId == jobKey && work.WorkId == workId);

            _fixture.DispatchObserver.FailRunnerAccepted = false;
            _fixture.RunnerAssignmentObserver.BlockAssignmentAdmission();
            var retry = job.EnsureSubmittedAsync(input);
            await _fixture.RunnerAssignmentObserver.WaitForAssignmentAdmissionAsync();

            _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(3));
            await _fixture.CloseoutObserver.WaitForAgentJobCloseoutStartingAsync();

            _fixture.RunnerAssignmentObserver.ReleaseAssignmentAdmission();
            await retry;

            await runner.UpdateAsync(RunnerCapacity.DefaultMaxWorkflowSlots);
            var terminal = await job.GetTerminalResultAsync();

            var runtime = await runner.GetRuntimeStateAsync();
            Assert.Equal(RunnerStatus.Offline, runtime.Status);
            Assert.Empty(runtime.ActiveWorks);
            Assert.Equal(AgentJobStatus.Failed, terminal.Status);
            Assert.Equal("runner-lost", terminal.FailureReason);
            var rejected = await job.GetRuntimeSnapshotAsync();
            Assert.Null(rejected.RunnerId);
            Assert.Null(rejected.CurrentWorkId);

            var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.AgentJob, jobKey, workId);
            Assert.NotNull(row);
            Assert.Equal("failed", row!.Status);
            Assert.Equal("runner-lost", row.Reason);
        }
        finally
        {
            _fixture.DispatchObserver.FailRunnerAccepted = false;
            _fixture.RunnerAssignmentObserver.ReleaseAssignmentAdmission();
            options.DispatchRetryBound = dispatchRetryBound;
        }
    }

    [Fact]
    public async Task ConcurrentAssignAgentJobAsync_RespectsSingleSlotCapacity()
    {
        var (runnerId, projectId) = await RegisterRunnerAsync(
            $"agent-job-capacity-conc-{Guid.NewGuid():N}",
            maxWorkflowSlots: 1);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var agentJobIdA = $"agent-job-conc-a-{Guid.NewGuid():N}";
        var agentJobIdB = $"agent-job-conc-b-{Guid.NewGuid():N}";
        var workIdA = $"agent-work-conc-a-{Guid.NewGuid():N}";
        var workIdB = $"agent-work-conc-b-{Guid.NewGuid():N}";

        var assignments = await Task.WhenAll(
            runner.AssignAgentJobAsync(new WorkDispatch(
                WorkflowRunId: string.Empty,
                WorkId: workIdA,
                AgentJobId: agentJobIdA,
                OwnerKind: WorkDispatchOwnerKinds.AgentJob)),
            runner.AssignAgentJobAsync(new WorkDispatch(
                WorkflowRunId: string.Empty,
                WorkId: workIdB,
                AgentJobId: agentJobIdB,
                OwnerKind: WorkDispatchOwnerKinds.AgentJob)));

        var accepted = assignments.Where(r => r.Status == RunnerWorkAssignmentStatus.Assigned).ToList();
        var rejected = assignments.Where(r => r.Status == RunnerWorkAssignmentStatus.Rejected).ToList();

        Assert.Single(accepted);
        Assert.Single(rejected);
        Assert.Equal("capacity-exhausted", rejected[0].Reason);

        var runtime = await runner.GetRuntimeStateAsync();
        Assert.Single(runtime.ActiveWorks);
        var acceptedWorkId = assignments[0].Status == RunnerWorkAssignmentStatus.Assigned
            ? workIdA
            : workIdB;
        var acceptedAgentJobId = assignments[0].Status == RunnerWorkAssignmentStatus.Assigned
            ? agentJobIdA
            : agentJobIdB;
        Assert.Contains(runtime.ActiveWorks, w => w.WorkId == acceptedWorkId);

        await runner.DeactivateForTestAsync();
        await Grains.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

        var reactivated = Grains.GetGrain<IRunnerGrain>(runnerId);
        var reactivatedRuntime = await reactivated.GetRuntimeStateAsync();
        var reactivatedWork = Assert.Single(reactivatedRuntime.ActiveWorks);
        Assert.Equal(acceptedWorkId, reactivatedWork.WorkId);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, reactivatedWork.OwnerKind);
        Assert.Equal(acceptedAgentJobId, reactivatedWork.OwnerId);

        var persistedWork = await FindRunnerWorkAsync(
            runnerId,
            WorkDispatchOwnerKinds.AgentJob,
            acceptedAgentJobId,
            acceptedWorkId);
        Assert.NotNull(persistedWork);
        Assert.Equal("outstanding", persistedWork!.Status);
    }

    [Fact]
    public async Task PollAdmittedAfterAssignmentStarts_RejectsAssignmentWithoutPersistingWork()
    {
        var (runnerId, _) = await RegisterRunnerAsync($"assignment-poll-race-{Guid.NewGuid():N}");
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var agentJobId = $"assignment-poll-job-{Guid.NewGuid():N}";
        var workId = $"assignment-poll-work-{Guid.NewGuid():N}";
        var dispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: workId,
            AgentJobId: agentJobId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        _fixture.RunnerAssignmentObserver.BlockAssignmentAdmission();
        var assignment = runner.AssignAgentJobAsync(dispatch);
        await _fixture.RunnerAssignmentObserver.WaitForAssignmentAdmissionAsync();

        var admission = await runner.TryBeginPollAsync();
        Assert.True(admission.Admitted);

        try
        {
            _fixture.RunnerAssignmentObserver.ReleaseAssignmentAdmission();
            var result = await assignment;

            Assert.Equal(RunnerWorkAssignmentStatus.Rejected, result.Status);
            Assert.Equal("runner-reconciling", result.Reason);
            Assert.Empty((await runner.GetRuntimeStateAsync()).ActiveWorks);
            Assert.Null(await FindRunnerWorkAsync(
                runnerId,
                WorkDispatchOwnerKinds.AgentJob,
                agentJobId,
                workId));
        }
        finally
        {
            _fixture.RunnerAssignmentObserver.ReleaseAssignmentAdmission();
            await runner.EndPollAsync();
        }
    }

    [Fact]
    public async Task ReconcileAgentJobsAsync_DuringRealAgentJobRetry_ReleasesLifecycleGate()
    {
        var (runnerId, projectId) = await RegisterRunnerAsync(
            $"reconcile-deadlock-{Guid.NewGuid():N}",
            maxWorkflowSlots: 2);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var agentJobId = $"reconcile-job-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(agentJobId);
        var input = MakeInput("reconcile", projectId, "/tmp/reconcile-deadlock");
        var options = Services.GetRequiredService<IOptions<AgentJobOptions>>().Value;
        var dispatchRetryBound = options.DispatchRetryBound;
        var dispatchBackoffInitial = options.DispatchBackoffInitial;
        var dispatchBackoffCap = options.DispatchBackoffCap;

        options.DispatchRetryBound = TimeSpan.FromMinutes(5);
        options.DispatchBackoffInitial = TimeSpan.FromMinutes(1);
        options.DispatchBackoffCap = TimeSpan.FromMinutes(1);

        try
        {
            _fixture.DispatchObserver.FailRunnerAccepted = true;
            await job.SubmitAsync(input);
            await _fixture.DispatchObserver.WaitForRunnerAcceptedAsync();

            var prepared = await job.GetRuntimeSnapshotAsync();
            Assert.Equal(AgentJobStatus.Pending, prepared.Status);
            Assert.Equal(runnerId, prepared.RunnerId);
            Assert.False(string.IsNullOrWhiteSpace(prepared.CurrentWorkId));
            var workId = prepared.CurrentWorkId!;

            _fixture.DispatchObserver.FailRunnerAccepted = false;
            _fixture.RunnerAssignmentObserver.BlockAssignmentAdmission();
            var retry = job.EnsureSubmittedAsync(input);
            await _fixture.RunnerAssignmentObserver.WaitForAssignmentAdmissionAsync();

            var admission = await runner.TryBeginPollAsync();
            Assert.True(admission.Admitted);
            try
            {
                var reconcile = runner.ReconcileAgentJobsAsync([]);
                _fixture.RunnerAssignmentObserver.ReleaseAssignmentAdmission();

                await Task.WhenAll(retry, reconcile);

                var pollState = await reconcile;
                Assert.Equal(1, pollState.ActiveCount);
                Assert.NotNull(pollState.Dispatch);
                Assert.Equal(workId, pollState.Dispatch!.WorkId);
            }
            finally
            {
                _fixture.RunnerAssignmentObserver.ReleaseAssignmentAdmission();
                await runner.EndPollAsync();
            }

            var settled = await job.GetRuntimeSnapshotAsync();
            Assert.Equal(AgentJobStatus.Pending, settled.Status);
            Assert.Equal(runnerId, settled.RunnerId);
            Assert.Equal(workId, settled.CurrentWorkId);
            var runtime = await runner.GetRuntimeStateAsync();
            var work = Assert.Single(runtime.ActiveWorks, active => active.OwnerId == agentJobId);
            Assert.Equal(workId, work.WorkId);
        }
        finally
        {
            _fixture.DispatchObserver.FailRunnerAccepted = false;
            _fixture.RunnerAssignmentObserver.ReleaseAssignmentAdmission();
            options.DispatchRetryBound = dispatchRetryBound;
            options.DispatchBackoffInitial = dispatchBackoffInitial;
            options.DispatchBackoffCap = dispatchBackoffCap;
        }
    }

    [Fact]
    public async Task ConcurrentReportAndAssignment_PreserveRunnerStateAndWorkLedgerAcrossReactivation()
    {
        var (runnerId, _) = await RegisterRunnerAsync(
            $"report-assignment-race-{Guid.NewGuid():N}",
            maxWorkflowSlots: 2);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var reportedAgentJobId = $"reported-job-{Guid.NewGuid():N}";
        var reportedWorkId = $"reported-work-{Guid.NewGuid():N}";
        var reportedDispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: reportedWorkId,
            AgentJobId: reportedAgentJobId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);
        Assert.Equal(
            RunnerWorkAssignmentStatus.Assigned,
            (await runner.AssignAgentJobAsync(reportedDispatch)).Status);
        await Grains.GetGrain<IAgentJobGrain>(reportedAgentJobId)
            .AssignRunnerAsync(runnerId, reportedWorkId);

        var assignedAgentJobId = $"assigned-job-{Guid.NewGuid():N}";
        var assignedWorkId = $"assigned-work-{Guid.NewGuid():N}";
        var assignedDispatch = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: assignedWorkId,
            AgentJobId: assignedAgentJobId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);

        var report = runner.ReportAgentJobResultAsync(
            reportedAgentJobId,
            reportedWorkId,
            new WorkResult("completed"));
        var assignment = runner.AssignAgentJobAsync(assignedDispatch);
        await Task.WhenAll(report, assignment);

        Assert.True((await report).Tracked);
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, (await assignment).Status);

        await runner.DeactivateForTestAsync();
        await Grains.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero);

        var reactivated = Grains.GetGrain<IRunnerGrain>(runnerId);
        var activeWork = Assert.Single((await reactivated.GetRuntimeStateAsync()).ActiveWorks);
        Assert.Equal(assignedWorkId, activeWork.WorkId);
        Assert.Equal(assignedAgentJobId, activeWork.OwnerId);

        var completedRow = await FindRunnerWorkAsync(
            runnerId,
            WorkDispatchOwnerKinds.AgentJob,
            reportedAgentJobId,
            reportedWorkId);
        Assert.NotNull(completedRow);
        Assert.Equal("completed", completedRow!.Status);

        var outstandingRow = await FindRunnerWorkAsync(
            runnerId,
            WorkDispatchOwnerKinds.AgentJob,
            assignedAgentJobId,
            assignedWorkId);
        Assert.NotNull(outstandingRow);
        Assert.Equal("outstanding", outstandingRow!.Status);
    }
}
