using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Tests.Workflow;
using Mohist.Server.TestSupport;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Mohist.Server.Tests.Runner.Grain;

[Collection("RunnerGrain")]
[Trait("level", "L1")]
public class RunnerFailureSpecs : WorkflowGrainSpecs
{
    public RunnerFailureSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RunnerUnregistersWithInFlightWork_FailsItAsRunnerLost()
    {
        var workflow = await StartWorkflowAsync(SingleStage());
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(WorkflowActionAttemptStatus.Failed, Assert.Single(run.Stages.Single().Tasks).Status);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Fact]
    public async Task RunnerUnregistersWithoutOutstandingWork_DoesNotFailAlreadyCompletedWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, work.WorkId, "completed");

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();

        Assert.Equal("Completed", await workflow.GetRunStatusAsync());
        var run = await LoadRunAsync(_workflowId!);
        Assert.Equal(WorkflowActionAttemptStatus.Completed, Assert.Single(run.Stages.Single().Tasks).Status);
    }

    [Fact]
    public async Task Heartbeat_RefreshesPresenceWhilePollIsGated_AndPreventsRunnerCloseout()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        await runner.BeginDrainAsync();
        var before = await runner.GetRuntimeStateAsync();
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await runner.HeartbeatAsync();

        var afterHeartbeat = await runner.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Online, afterHeartbeat.Status);
        Assert.Equal(before.LastHeartbeatAt.AddMinutes(1), afterHeartbeat.LastHeartbeatAt);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1.5));
        var afterOriginalPresenceInterval = await runner.GetRuntimeStateAsync();
        Assert.Equal(RunnerStatus.Online, afterOriginalPresenceInterval.Status);
        Assert.Equal(work.WorkId, await workflow.GetCurrentWorkIdAsync());
        Assert.Equal("Running", await workflow.GetRunStatusAsync());
    }

    [Fact]
    public async Task Heartbeat_WithOnlineRunner_PreservesRunningTask()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).HeartbeatAsync();

        Assert.Equal("Running", await workflow.GetRunStatusAsync());
        Assert.Equal(work.WorkId, await workflow.GetCurrentWorkIdAsync());
    }

    [Fact]
    public async Task PresenceLease_PersistsAbsoluteExpiry_AndReactivationKeepsRemainingTime()
    {
        var runnerId = $"runner-lease-reactivation-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var storage = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IGrainStorage>();
        var registeredAt = _fixture.TimeProvider.GetUtcNow();

        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "lease-host", "test-project"));
        var stored = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), stored);
        Assert.Equal(registeredAt.AddMinutes(2), stored.State.PresenceLeaseExpiresAt);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await TestLifecycle.DeactivateAndWait(runner, Grains);
        var reactivated = Grains.GetGrain<IRunnerGrain>(runnerId);
        Assert.True(await reactivated.IsPresenceLeaseActiveAsync());

        stored = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", reactivated.GetGrainId(), stored);
        Assert.Equal(registeredAt.AddMinutes(2), stored.State.PresenceLeaseExpiresAt);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await reactivated.IsPresenceLeaseActiveAsync());
    }

    [Fact]
    public async Task Unregister_RetainsLatestPresenceObservationAfterClearingLease()
    {
        var runnerId = $"runner-presence-observation-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var storage = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IGrainStorage>();
        var registeredAt = _fixture.TimeProvider.GetUtcNow();

        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "presence-host", "test-project"));
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(30));
        await runner.HeartbeatAsync();
        var observedAt = _fixture.TimeProvider.GetUtcNow();

        await runner.UnregisterAsync();

        var stored = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), stored);
        Assert.Equal(observedAt, stored.State.LastPresenceAt);
        Assert.Null(stored.State.PresenceLeaseExpiresAt);
        Assert.Equal(observedAt, (await runner.GetRuntimeStateAsync()).LastHeartbeatAt);
        Assert.NotEqual(registeredAt, observedAt);
    }

    [Fact]
    public async Task ExpiredRegistryIndex_IsExcludedFromEligibilityBeforeCleanup()
    {
        var runnerId = $"runner-expired-index-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var registry = Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var storage = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IGrainStorage>();
        await storage.WriteStateAsync("runner", runner.GetGrainId(), new GrainState<RunnerState>
        {
            State = new RunnerState
            {
                LastKnownInfo = new RunnerInfo(runnerId, ["spec/*"], "expired-host", "test-project"),
                CurrentProcessGeneration = TestRunnerGenerationExtensions.ProcessGeneration,
                PresenceLeaseExpiresAt = _fixture.TimeProvider.GetUtcNow().AddMinutes(-1),
            },
        });

        // Activate the expired authority first; it converges offline and does
        // not arm the low-latency timer. Then model lagging volatile cleanup by
        // reinserting the stale index row.
        Assert.False(await runner.IsPresenceLeaseActiveAsync());
        await registry.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "expired-host", "test-project"));

        Assert.Contains(await registry.ListAllAsync(), item => item.RunnerId == runnerId);
        Assert.DoesNotContain(
            await registry.ListEligibleRunnersAsync("test-project"),
            item => item.RunnerId == runnerId);
    }

    [Fact]
    public async Task RunnerLoss_FirstOwnerFailure_RetainsGenerationAndReminderRetryCompletes()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var storage = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IGrainStorage>();
        var failures = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<ReportPersistenceFailureProbe>();
        failures.FailNextWorkflowReport(work.WorkflowRunId, work.WorkId);

        await runner.UnregisterAsync();

        var afterFailure = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), afterFailure);
        Assert.Equal(TestRunnerGenerationExtensions.ProcessGeneration, afterFailure.State.ClosingProcessGeneration);
        Assert.Equal("Running", await workflow.GetRunStatusAsync());

        await runner.AsReference<IRemindable>().ReceiveReminder("presence", default);

        var afterRetry = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), afterRetry);
        Assert.Null(afterRetry.State.ClosingProcessGeneration);
        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Fact]
    public async Task RunnerLoss_FirstOwnerFailure_RedrivesAfterRunnerReactivation()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var storage = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IGrainStorage>();
        _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<ReportPersistenceFailureProbe>()
            .FailNextWorkflowReport(work.WorkflowRunId, work.WorkId);

        await runner.UnregisterAsync();
        await TestLifecycle.DeactivateAndWait(runner, Grains);
        var reactivated = Grains.GetGrain<IRunnerGrain>(runnerId);
        _ = await reactivated.GetRuntimeStateAsync();

        var state = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", reactivated.GetGrainId(), state);
        Assert.Null(state.State.ClosingProcessGeneration);
        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
    }

    [Fact]
    public async Task LegacyRunnerStateWithoutLease_RemainsOfflineAndIneligible()
    {
        var runnerId = $"runner-legacy-lease-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var storage = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IGrainStorage>();
        await storage.WriteStateAsync("runner", runner.GetGrainId(), new GrainState<RunnerState>
        {
            State = new RunnerState
            {
                LastKnownInfo = new RunnerInfo(runnerId, ["spec/*"], "legacy-host", "test-project"),
            },
        });

        Assert.False(await runner.IsPresenceLeaseActiveAsync());
        Assert.DoesNotContain(
            await Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global)
                .ListEligibleRunnersAsync("test-project"),
            item => item.RunnerId == runnerId);
    }

    [Fact]
    public async Task RunnerUnregisters_WithPausedRunActiveWork_FailsItAsRunnerLost()
    {
        // Pausing deliberately leaves the executing Action running, so the
        // run's own status cannot exempt its claim from generation closeout.
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();
        await workflow.PauseAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).UnregisterAsync();

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = Assert.Single(run.Stages.Single().Tasks);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(WorkflowActionAttemptStatus.Failed, task.Status);
        Assert.Equal(work.WorkId, task.WorkId);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Fact]
    public async Task PausedRun_ReplacementGeneration_SettlesOnlyTheLostGenerationWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();
        await workflow.PauseAsync();

        // An unrelated run keeps its claim under its own Runner.
        await StartWorkflowAsync(SingleStage(checks: []));
        var otherRunnerId = _runnerId!;
        var (otherWork, _) = await PollWorkAnyAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(
            RunnerInfoFor(runnerId, work.WorkflowRunId),
            ReplacementGeneration);

        var lost = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Failed, lost.Status);
        Assert.Equal(WorkflowActionAttemptStatus.Failed, Assert.Single(lost.Stages.Single().Tasks).Status);
        Assert.Equal("runner-lost", lost.Failure?.Message);

        var untouched = await LoadRunAsync(otherWork.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Running, untouched.Status);
        var untouchedTask = Assert.Single(untouched.Stages.Single().Tasks);
        Assert.Equal(WorkflowActionAttemptStatus.Running, untouchedTask.Status);
        Assert.Equal(otherWork.WorkId, untouchedTask.WorkId);
        Assert.Equal(otherRunnerId, untouchedTask.WorkerId);
    }

    [Fact]
    public async Task PausedRun_SameGenerationReconnect_PreservesActiveWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();
        await workflow.PauseAsync();

        await Grains.GetGrain<IRunnerGrain>(runnerId).RegisterAsync(
            RunnerInfoFor(runnerId, work.WorkflowRunId),
            TestRunnerGenerationExtensions.ProcessGeneration);

        var run = await LoadRunAsync(work.WorkflowRunId);
        var task = Assert.Single(run.Stages.Single().Tasks);
        Assert.Equal(WorkflowRunStatus.Paused, run.Status);
        Assert.Equal(WorkflowActionAttemptStatus.Running, task.Status);
        Assert.Equal(work.WorkId, task.WorkId);
        Assert.Equal(TestRunnerGenerationExtensions.ProcessGeneration, task.ProcessGeneration);
        Assert.Null(run.Failure);
    }

    [Fact]
    public async Task SupersededGenerationOrphan_WithoutClosingMarker_IsFailedOnActivation_ThenRetrySucceeds()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();

        // The owner's rerun guard still protects the stage while the orphan
        // holds its active work, marker or not.
        var rejected = await workflow.RerunFromStageAsync("build");
        Assert.False(rejected.Success);
        Assert.Equal("active_work_in_range", rejected.Code);

        await StageSupersededClaimAsync(runnerId);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        _ = await runner.GetRuntimeStateAsync();

        var settled = await LoadRunAsync(work.WorkflowRunId);
        var failedAttempt = Assert.Single(settled.Stages.Single().Tasks);
        Assert.Equal(WorkflowRunStatus.Failed, settled.Status);
        Assert.Equal(work.ActionAttemptId, failedAttempt.Id);
        Assert.Equal(WorkflowActionAttemptStatus.Failed, failedAttempt.Status);
        Assert.Equal("runner-lost", settled.Failure?.Message);

        // Ordinary task retry continues the same lineage: the lost attempt is
        // kept for history and a successor attempt takes its place.
        await workflow.RetryAsync();

        var retried = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Ready, retried.Status);
        Assert.Null(retried.Failure);
        var retriedAttempt = Assert.Single(retried.Stages.Single().Tasks, task => task.Status == WorkflowActionAttemptStatus.Pending);
        Assert.NotEqual(failedAttempt.Id, retriedAttempt.Id);
        Assert.Contains(retried.Stages.Single().Tasks, task => task.Id == failedAttempt.Id);

        var (redispatched, _) = await PollWorkAnyAsync();
        Assert.Equal(work.WorkflowRunId, redispatched.WorkflowRunId);
        Assert.Equal(retriedAttempt.Id, redispatched.ActionAttemptId);

        // Arbitration runs at every activation; the re-claimed work belongs to
        // the current generation and must survive the next pass.
        await TestLifecycle.DeactivateAndWait(runner, Grains);
        _ = await Grains.GetGrain<IRunnerGrain>(runnerId).GetRuntimeStateAsync();

        var reclaimed = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Running, reclaimed.Status);
        var reclaimedTask = Assert.Single(reclaimed.Stages.Single().Tasks, task => task.Status == WorkflowActionAttemptStatus.Running);
        Assert.Equal(redispatched.WorkId, reclaimedTask.WorkId);
        Assert.Equal(TestRunnerGenerationExtensions.ProcessGeneration, reclaimedTask.ProcessGeneration);
    }

    [Fact]
    public async Task SupersededGenerationOrphan_WhenSettlementFails_KeepsTheObligationUntilTheReminderRetries()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();
        await StageSupersededClaimAsync(runnerId);

        _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<ReportPersistenceFailureProbe>()
            .FailNextWorkflowReport(work.WorkflowRunId, work.WorkId);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        _ = await runner.GetRuntimeStateAsync();

        var storage = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IGrainStorage>();
        var afterFailure = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), afterFailure);
        Assert.Equal(TestRunnerGenerationExtensions.ProcessGeneration, afterFailure.State.ClosingProcessGeneration);
        Assert.Equal("Running", await workflow.GetRunStatusAsync());

        await runner.AsReference<IRemindable>().ReceiveReminder("presence", default);

        var afterRetry = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), afterRetry);
        Assert.Null(afterRetry.State.ClosingProcessGeneration);
        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        Assert.Equal("runner-lost", (await LoadRunAsync(work.WorkflowRunId)).Failure?.Message);
    }

    [Fact]
    public async Task RetainedCloseoutForAnOlderGeneration_StillSettlesCurrentGenerationWork()
    {
        var workflow = await StartWorkflowWithoutRunnerAsync(SingleStage(checks: []));
        var projectId = TestProjectId(_workflowId!);
        var runnerId = $"runner-retained-closeout-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(RunnerInfoFor(runnerId, _workflowId!), ReplacementGeneration);

        // The current generation owns this work.
        await workflow.AssignWorkerAsync(runnerId);
        var dispatch = Services.GetRequiredService<Mohist.Server.Runner.Services.DispatchService>();
        var claimed = Assert.Single((await dispatch.PollAsync(
            runnerId,
            DispatchTestExtensions.ReadyPollRequestForGeneration(ReplacementGeneration))).Dispatches);

        // An older generation still owns this one: a claim that survived a
        // completed closeout, which is what the paused-work defect left behind.
        var staleRunId = $"wf-stale-{Guid.NewGuid():N}";
        var staleWorkflow = Grains.GetGrain<IWorkflowGrain>(staleRunId);
        await SeedWorkflowTemplateAsync(staleRunId, SingleStage(checks: []), projectId);
        await staleWorkflow.StartAsync(TestInput(projectId));
        await staleWorkflow.AssignWorkerAsync(runnerId);
        await staleWorkflow.ClaimNextAsync(runnerId, "older-generation");
        var staleClaim = Assert.Single((await LoadRunAsync(staleRunId)).Stages.Single().Tasks);
        Assert.Equal("older-generation", staleClaim.ProcessGeneration);

        _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<ReportPersistenceFailureProbe>()
            .FailNextWorkflowReport(staleRunId, staleClaim.WorkId!);

        // The same-generation registration arbitrates the stale claim and
        // cannot settle it, so the closeout obligation is retained for that
        // older generation while the current one keeps its own claim.
        await runner.RegisterAsync(RunnerInfoFor(runnerId, _workflowId!), ReplacementGeneration);

        var storage = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IGrainStorage>();
        var retained = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), retained);
        Assert.Equal("older-generation", retained.State.ClosingProcessGeneration);
        Assert.Equal("Running", await workflow.GetRunStatusAsync());

        // Runner loss closes out the current generation too, not only the
        // retained obligation recorded for the older one.
        await runner.UnregisterAsync();

        var settled = await LoadRunAsync(claimed.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Failed, settled.Status);
        Assert.Equal("runner-lost", settled.Failure?.Message);
        Assert.Equal(claimed.ActionAttemptId, Assert.Single(settled.Stages.Single().Tasks).Id);

        var staleSettled = await LoadRunAsync(staleRunId);
        Assert.Equal(WorkflowRunStatus.Failed, staleSettled.Status);
        Assert.Equal("runner-lost", staleSettled.Failure?.Message);

        var afterCloseout = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), afterCloseout);
        Assert.Null(afterCloseout.State.ClosingProcessGeneration);
    }

    [Fact]
    public async Task AdministrativeRemoval_RetriesAllWorkflowGenerationsAfterReload()
    {
        var workflow = await StartWorkflowWithoutRunnerAsync(SingleStage(checks: []));
        var projectId = TestProjectId(_workflowId!);
        var runnerId = $"runner-removal-closeout-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await IssueRunnerCredentialAsync(runnerId);
        await runner.RegisterAsync(RunnerInfoFor(runnerId, _workflowId!), ReplacementGeneration);

        await workflow.AssignWorkerAsync(runnerId);
        var dispatch = Services.GetRequiredService<Mohist.Server.Runner.Services.DispatchService>();
        var current = Assert.Single((await dispatch.PollAsync(
            runnerId,
            DispatchTestExtensions.ReadyPollRequestForGeneration(ReplacementGeneration))).Dispatches);

        var staleRunId = $"wf-removal-stale-{Guid.NewGuid():N}";
        var staleWorkflow = Grains.GetGrain<IWorkflowGrain>(staleRunId);
        await SeedWorkflowTemplateAsync(staleRunId, SingleStage(checks: []), projectId);
        await staleWorkflow.StartAsync(TestInput(projectId));
        await staleWorkflow.AssignWorkerAsync(runnerId);
        await staleWorkflow.ClaimNextAsync(runnerId, "older-generation");
        var staleClaim = Assert.Single((await LoadRunAsync(staleRunId)).Stages.Single().Tasks);

        var failures = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<ReportPersistenceFailureProbe>();
        failures.FailNextWorkflowReport(staleRunId, staleClaim.WorkId!);
        await runner.RegisterAsync(RunnerInfoFor(runnerId, _workflowId!), ReplacementGeneration);

        var storage = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IGrainStorage>();
        var retained = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), retained);
        Assert.Equal("older-generation", retained.State.ClosingProcessGeneration);

        failures.FailNextWorkflowReport(current.WorkflowRunId, current.WorkId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RevokeExecutionAuthorityAsync(_fixture.TimeProvider.GetUtcNow()));
        Assert.Equal("Running", await workflow.GetRunStatusAsync());
        var pending = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), pending);
        Assert.Equal(
            RunnerAdministrativeRemovalPhase.AuthorityFenced,
            pending.State.AdministrativeRemoval!.Phase);
        Assert.Equal("older-generation", pending.State.ClosingProcessGeneration);

        await TestLifecycle.DeactivateAndWait(runner, Grains);
        runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        _ = await runner.GetRuntimeStateAsync();

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        Assert.Equal("runner-lost", (await LoadRunAsync(current.WorkflowRunId)).Failure?.Message);
        Assert.Equal("Failed", await staleWorkflow.GetRunStatusAsync());
        Assert.Equal("runner-lost", (await LoadRunAsync(staleRunId)).Failure?.Message);

        var completed = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), completed);
        Assert.Equal(
            RunnerAdministrativeRemovalPhase.Completed,
            completed.State.AdministrativeRemoval!.Phase);
        Assert.Null(completed.State.ClosingProcessGeneration);
    }

    [Fact]
    public async Task SupersededGenerationOrphan_WhenDiscoveryCannotRun_RetriesOnThePresenceReminder()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();
        await StageSupersededClaimAsync(runnerId);

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var readFailures = Services.GetRequiredService<WorkflowRunReadFailureProbe>();
        var reminders = Services.GetRequiredService<IReminderTable>();
        var storage = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IGrainStorage>();

        // The arbitration discovers owners through an owner query. When that
        // query cannot run, no claim can be named and no durable obligation
        // exists, so the retry itself must survive the failure.
        await RemovePresenceReminderAsync(runner, reminders);
        readFailures.FailNextWorkflowRunRead();
        _ = await runner.GetRuntimeStateAsync();

        var failedCommand = readFailures.FailedCommand;
        Assert.NotNull(failedCommand);
        Assert.Contains("ActiveWorkerId", failedCommand, StringComparison.Ordinal);

        var undecided = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), undecided);
        Assert.Null(undecided.State.ClosingProcessGeneration);
        Assert.NotNull(await reminders.ReadRow(runner.GetGrainId(), "presence"));

        // The lost generation still owns the work.
        Assert.Equal("Running", await workflow.GetRunStatusAsync());

        // No activation and no registration: the presence reminder alone
        // carries the undecided arbitration to completion.
        await runner.AsReference<IRemindable>().ReceiveReminder("presence", default);

        var settled = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(WorkflowRunStatus.Failed, settled.Status);
        Assert.Equal("runner-lost", settled.Failure?.Message);
        Assert.Equal(work.ActionAttemptId, Assert.Single(settled.Stages.Single().Tasks).Id);
        Assert.Null(await reminders.ReadRow(runner.GetGrainId(), "presence"));
    }

    [Fact]
    public async Task SupersededGenerationOrphan_WhenAReportLandedFirst_IsNotFailed()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var runnerId = _runnerId!;
        var (work, _) = await PollWorkAnyAsync();
        await StageSupersededClaimAsync(runnerId);

        // The report path owns settlement: a result that landed is not a lost
        // claim, so arbitration must not fabricate a failure for it.
        await ReportAsync(runnerId, work.WorkId, "completed");

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        _ = await runner.GetRuntimeStateAsync();

        Assert.Equal("Completed", await workflow.GetRunStatusAsync());
        var completed = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(WorkflowActionAttemptStatus.Completed, Assert.Single(completed.Stages.Single().Tasks).Status);
        Assert.Null(completed.Failure);

        var storage = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IGrainStorage>();
        var state = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), state);
        Assert.Null(state.State.ClosingProcessGeneration);
    }

    private async Task IssueRunnerCredentialAsync(string runnerId)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var credential = await scope.ServiceProvider.GetRequiredService<ICredentialStore>()
            .CreateRunnerCredentialAsync(MohistPrincipal.AdminPrincipalId, runnerId);
        Assert.NotNull(credential);
    }

    private const string ReplacementGeneration = "replacement-generation";

    private RunnerInfo RunnerInfoFor(string runnerId, string workflowRunId) =>
        new(runnerId, ["spec/*"], "test-host", TestProjectId(workflowRunId),
            ConnectionGeneration: DispatchTestExtensions.ConnectionGeneration);

    /// <summary>
    /// Drops the presence reminder the earlier registration left behind, so a
    /// spec observes the obligation this Runner arms for itself.
    /// </summary>
    private static async Task RemovePresenceReminderAsync(IRunnerGrain runner, IReminderTable reminders)
    {
        var row = await reminders.ReadRow(runner.GetGrainId(), "presence");
        if (row is not null)
            Assert.True(await reminders.RemoveRow(runner.GetGrainId(), "presence", row.ETag));
    }

    /// <summary>
    /// Leaves the Runner in the state the paused-work closeout defect produced:
    /// a completed closeout cleared its marker while the run's active claim
    /// survived, and the replacement generation is already the one the Server
    /// considers authoritative.
    /// </summary>
    private async Task StageSupersededClaimAsync(string runnerId)
    {
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await TestLifecycle.DeactivateAndWait(runner, Grains);
        var storage = _fixture.Cluster.GetSiloServiceProvider(null).GetRequiredService<IGrainStorage>();
        var state = new GrainState<RunnerState>();
        await storage.ReadStateAsync("runner", runner.GetGrainId(), state);
        state.State.CurrentProcessGeneration = ReplacementGeneration;
        state.State.PendingProcessGeneration = null;
        state.State.ClosingProcessGeneration = null;
        state.State.PresenceLeaseExpiresAt = null;
        await storage.WriteStateAsync("runner", runner.GetGrainId(), state);
    }
}
