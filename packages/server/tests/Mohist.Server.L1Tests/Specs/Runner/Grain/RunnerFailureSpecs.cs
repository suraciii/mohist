using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.L1Tests.Specs.Workflow;
using Mohist.Server.TestSupport;
using Orleans;
using Orleans.Storage;
using Xunit;

namespace Mohist.Server.L1Tests.Specs.Runner.Grain;

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
}
