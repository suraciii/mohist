using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Capacity;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Runner.Domain;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Mohist.Server.Tests.Workflow;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.Tests.Agent.Grain;

/// <summary>
/// Job-owner derived Agent-capacity admission proofs: the claim precedes
/// Runner election in one storage transaction, uncertain or conflicting
/// commits never authorize a stale effect, timestamps survive reassignment,
/// and Session-local order holds a later launch back without failing it.
/// </summary>
public partial class AgentJobGrainSpecs
{
    private AgentCapacitySnapshot CapacitySnapshot(string projectId, string agentId = "agent-test")
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentCapacityStore>();
        return store.ReadAsync(projectId, [agentId]).GetAwaiter().GetResult()[agentId];
    }

    private async Task<AgentJobLedgerRecord> LoadLedgerAsync(string jobKey)
    {
        using var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
        var record = await store.LoadLedgerAsync(jobKey);
        return record ?? throw new InvalidOperationException($"AgentJob ledger row {jobKey} is missing.");
    }

    private async Task DeleteAgentDefinitionAsync(string projectId, string agentId)
    {
        await using var db = GrainTestConfig.CreateDbContext(_fixture.ConnectionString);
        var row = await db.Agents.FindAsync(GrainKey.Agent(projectId, agentId));
        if (row is null)
            return;
        db.Agents.Remove(row);
        await db.SaveChangesAsync();
    }

    private async Task<string> RegisterBareRunnerAsync(string projectId, string runnerId)
    {
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(
            runnerId,
            ["spec/*", AgentExecutionSources.Version1Capability],
            "agent-job-host",
            projectId,
            ConnectionGeneration: CapabilityFenceConnection,
            RuntimeCatalogs: CapabilityCatalogTestHelpers.Create()));
        await WaitForAsync(
            () => runner.GetRuntimeStateAsync(),
            state => state.Status == RunnerStatus.Online,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            $"runner {runnerId} is online");
        await WaitForAsync(
            () => Grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).ListEligibleRunnersAsync(projectId),
            runners => runners.Any(info => string.Equals(info.RunnerId, runnerId, StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(25),
            $"runner {runnerId} is eligible for project {projectId}");
        return runnerId;
    }

    [Fact]
    public async Task CapacityClaim_NoRunnerOnline_ClaimsFirstAndBoundsFromTheClaim()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-job-claim-norunner-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: 1);
        var jobKey = $"agent-job-claim-norunner-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("no runner yet", projectId));

        var claimTime = _fixture.TimeProvider.GetUtcNow();
        var record = await LoadLedgerAsync(jobKey);
        var state = JSON.Deserialize<AgentJobState>(record.StateJson)!;
        // The capacity claim happened before any Runner election, at the
        // fixed injected clock, and fixed both timestamps together.
        Assert.Equal(AgentJobStatus.Pending, state.Status);
        Assert.Null(record.AssignedRunnerId);
        Assert.Equal(claimTime, state.CapacityClaimedAt);
        Assert.Equal(claimTime, record.ReadySince);
        Assert.Equal(claimTime, state.ReadySince);
        Assert.Equal(1, CapacitySnapshot(projectId).Occupied);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(4));
        await job.CheckTimeoutsAsync();
        Assert.Equal(AgentJobStatus.Pending, await job.GetStatusAsync());

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(2));
        await job.CheckTimeoutsAsync();

        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal(AgentJobFailureReasons.RunnerUnavailable, terminal.FailureReason);
        Assert.Equal(0, CapacitySnapshot(projectId).Occupied);
    }

    [Fact]
    public async Task CapacityClaim_UnclaimedPendingJob_DoesNotTripTheBound()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-job-claim-unclaimed-{Guid.NewGuid():N}";
        // No seeded definition and no runner: the accepted work stays
        // Pending with dispatch-pending evidence and no capacity claim, so
        // the availability bound never starts.
        var jobKey = $"agent-job-claim-unclaimed-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        await job.SubmitAsync(MakeInput("identity not repairable yet", projectId));

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(6));
        await job.CheckTimeoutsAsync();

        var record = await LoadLedgerAsync(jobKey);
        var state = JSON.Deserialize<AgentJobState>(record.StateJson)!;
        Assert.Equal(AgentJobStatus.Pending, state.Status);
        Assert.Null(state.CapacityClaimedAt);
        Assert.Equal(AgentAvailabilityWaitReasons.DispatchPending, state.WaitingReason);
    }

    [Fact]
    public async Task CapacityClaim_ConflictingRevision_ReloadsFreshRowThenClaimsSafely()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-job-claim-conflict-{Guid.NewGuid():N}";
        var jobKey = $"agent-job-claim-conflict-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        // Without a definition the first evaluation cannot claim, so the
        // row keeps only the accepted submission.
        await job.SubmitAsync(MakeInput("claim behind a moved row", projectId));
        Assert.Null(JSON.Deserialize<AgentJobState>((await LoadLedgerAsync(jobKey)).StateJson)!.CapacityClaimedAt);

        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: 1);

        // Move the row behind the grain's cached ledger revision.
        AgentJobLedgerRecord moved;
        using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAgentJobStore>();
            var current = await store.LoadLedgerAsync(jobKey)
                ?? throw new InvalidOperationException($"AgentJob ledger row {jobKey} is missing.");
            moved = await store.SaveLedgerAsync(current);
        }

        await job.CheckTimeoutsAsync();

        // The conflicting claim reloaded the fresh row and never wrote the
        // stale cache over it.
        var afterConflict = await LoadLedgerAsync(jobKey);
        Assert.Equal(moved.Revision, afterConflict.Revision);
        var afterState = JSON.Deserialize<AgentJobState>(afterConflict.StateJson)!;
        Assert.Null(afterState.CapacityClaimedAt);

        // The next evaluation retries from the fresh revision and claims.
        await job.CheckTimeoutsAsync();
        var record = await LoadLedgerAsync(jobKey);
        var state = JSON.Deserialize<AgentJobState>(record.StateJson)!;
        Assert.Equal(_fixture.TimeProvider.GetUtcNow(), state.CapacityClaimedAt);
        Assert.Equal(record.ReadySince, state.CapacityClaimedAt);
        Assert.Equal(1, CapacitySnapshot(projectId).Occupied);
        Assert.Equal(AgentJobStatus.Pending, state.Status);
        Assert.Equal(AgentAvailabilityWaitReasons.NoOnlineRunner, state.WaitingReason);
    }

    [Fact]
    public async Task CapacityClaim_UncertainCommit_ReloadsCommittedClaimWithoutDoubleClaiming()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-job-claim-uncertain-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: 1);
        var jobKey = $"agent-job-claim-uncertain-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);

        _fixture.CapacityClaimUncertainty.Arm(jobKey);
        await job.SubmitAsync(MakeInput("commit then throw", projectId));

        // The throw surfaced no failure: the committed claim is on the row
        // and the owner reloaded it before any further effect.
        var committed = await LoadLedgerAsync(jobKey);
        var committedState = JSON.Deserialize<AgentJobState>(committed.StateJson)!;
        var claimTime = committedState.CapacityClaimedAt;
        Assert.NotNull(claimTime);
        Assert.Equal(AgentJobStatus.Pending, committedState.Status);
        Assert.Equal(1, CapacitySnapshot(projectId).Occupied);

        await job.CheckTimeoutsAsync();

        var after = await LoadLedgerAsync(jobKey);
        var afterState = JSON.Deserialize<AgentJobState>(after.StateJson)!;
        // The re-evaluation recognized the committed claim instead of
        // claiming a second slot or restamping the first.
        Assert.Equal(claimTime, afterState.CapacityClaimedAt);
        Assert.Equal(1, CapacitySnapshot(projectId).Occupied);
        Assert.Equal(AgentAvailabilityWaitReasons.NoOnlineRunner, afterState.WaitingReason);
    }

    [Fact]
    public async Task CapacityClaim_LoweredOrMissingDefinition_KeepsClaimsAndStaysPending()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-job-claim-limit-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: 2);
        var first = JobGrain($"agent-job-claim-limit-1-{Guid.NewGuid():N}");
        var second = JobGrain($"agent-job-claim-limit-2-{Guid.NewGuid():N}");
        var third = JobGrain($"agent-job-claim-limit-3-{Guid.NewGuid():N}");

        await first.SubmitAsync(MakeInput("occupy one", projectId));
        await second.SubmitAsync(MakeInput("occupy two", projectId));
        Assert.Equal(2, CapacitySnapshot(projectId).Occupied);

        // Lowering the live limit never cancels held claims.
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: 1);
        await third.SubmitAsync(MakeInput("wait behind the limit", projectId));
        var lowered = CapacitySnapshot(projectId);
        Assert.Equal(1, lowered.MaxConcurrentRuns);
        Assert.Equal(2, lowered.Occupied);
        Assert.Equal(AgentJobStatus.Pending, await third.GetStatusAsync());

        // Removing the definition is incomplete evidence, never anonymous
        // capacity: the waiting Job stays accepted Pending as
        // dispatch-pending, and the held claim stays idempotently readable.
        await DeleteAgentDefinitionAsync(projectId, "agent-test");
        await third.CheckTimeoutsAsync();
        var thirdRecord = await LoadLedgerAsync(third.GetPrimaryKeyString());
        var thirdState = JSON.Deserialize<AgentJobState>(thirdRecord.StateJson)!;
        Assert.Equal(AgentJobStatus.Pending, thirdState.Status);
        Assert.Null(thirdState.CapacityClaimedAt);
        Assert.Equal(AgentAvailabilityWaitReasons.DispatchPending, thirdState.WaitingReason);

        var firstRecord = await LoadLedgerAsync(first.GetPrimaryKeyString());
        using (var scope = _fixture.Cluster.GetSiloServiceProvider(null).CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAgentCapacityStore>();
            var retry = await store.ClaimJobAsync(
                first.GetPrimaryKeyString(),
                firstRecord.Revision);
            Assert.Equal(AgentCapacityClaimDisposition.AlreadyClaimed, retry.Disposition);
        }
    }

    [Fact]
    public async Task RunnerReassignment_PreservesTheFirstClaimTimestamps()
    {
        await ClearGlobalRunnerRegistryAsync();
        var projectId = $"agent-job-reassign-claim-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, "agent-test", maxConcurrentRuns: null);
        var runner1 = await RegisterBareRunnerAsync(projectId, $"agent-job-reassign-a-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-reassign-claim-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        var input = MakeInput("reassign me", projectId);

        await job.SubmitAsync(input);
        var claimed = await LoadLedgerAsync(jobKey);
        var claimedState = JSON.Deserialize<AgentJobState>(claimed.StateJson)!;
        var claimTime = claimedState.CapacityClaimedAt;
        Assert.NotNull(claimTime);
        Assert.Equal(claimTime, claimed.ReadySince);
        Assert.Equal(runner1, claimed.AssignedRunnerId);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await Grains.GetGrain<IRunnerGrain>(runner1).UnregisterAsync();
        var runner2 = await RegisterBareRunnerAsync(projectId, $"agent-job-reassign-b-{Guid.NewGuid():N}");
        await job.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        await job.EnsureSubmittedAsync(input);

        var snapshot = await job.GetRuntimeSnapshotAsync();
        Assert.Equal(runner2, snapshot.RunnerId);
        Assert.Equal(claimed.WorkId, snapshot.CurrentWorkId);
        var reassigned = await LoadLedgerAsync(jobKey);
        var reassignedState = JSON.Deserialize<AgentJobState>(reassigned.StateJson)!;
        // Reassignment and disconnect never restamp or clear the first
        // claim's fixed timestamps.
        Assert.Equal(claimTime, reassignedState.CapacityClaimedAt);
        Assert.Equal(claimTime, reassigned.ReadySince);
    }

    [Fact]
    public async Task UnknownExecution_OccupiesBeyondTheAvailabilityBound()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-unknown-occupy-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-unknown-occupy-{Guid.NewGuid():N}";
        var job = JobGrain(jobKey);
        await job.SubmitAsync(MakeInput("occupy while unknown", projectId));
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        Assert.Equal(1, CapacitySnapshot(projectId).Occupied);

        await job.MarkUnknownAsync("execution outcome is uncertain");

        Assert.Equal(AgentJobStatus.Unknown, await job.GetStatusAsync());
        Assert.Equal(1, CapacitySnapshot(projectId).Occupied);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(10));
        await job.CheckTimeoutsAsync();

        Assert.Equal(AgentJobStatus.Unknown, await job.GetStatusAsync());
        Assert.Equal(1, CapacitySnapshot(projectId).Occupied);
    }

    [Fact]
    public async Task TerminalCompletion_ReleasesDerivedOccupancy()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-terminal-occupy-{Guid.NewGuid():N}");
        var jobKey = $"agent-job-terminal-occupy-{Guid.NewGuid():N}";
        var sessionId = $"session-terminal-occupy-{Guid.NewGuid():N}";
        var inputId = $"input-terminal-occupy-{Guid.NewGuid():N}";
        var turnId = $"turn-terminal-occupy-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-terminal-occupy-{Guid.NewGuid():N}";
        await OpenJobSessionAsync(sessionId, projectId, jobKey, inputId, turnId, "release when terminal");
        var job = JobGrain(jobKey);
        await job.SubmitAsync(MakeInput("release when terminal", projectId) with
        {
            AgentSessionId = sessionId,
            InitialInputId = inputId,
            InitialTurnId = turnId,
            Runtime = "opencode",
        });
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        Assert.Equal(1, CapacitySnapshot(projectId).Occupied);

        var workId = (await job.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        Assert.True(await job.RecordRuntimeSessionBindingAsync(runnerId, workId, sessionId, runtimeSessionId));
        await job.ReportResultAsync(runnerId, workId, new WorkResult(
            "completed",
            "done",
            AgentSessionId: sessionId,
            AgentTurnId: turnId,
            Runtime: "opencode",
            RuntimeSessionId: runtimeSessionId));

        await WaitForStatusAsync(job, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));
        Assert.Equal(0, CapacitySnapshot(projectId).Occupied);
    }

    [Fact]
    public async Task LaterJobBehindEarlierOrdinaryTurn_WaitsWithoutFailingOrStarting()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-local-order-{Guid.NewGuid():N}");
        var sessionId = $"session-local-order-{Guid.NewGuid():N}";
        var jobKey = $"agent-job-local-order-{Guid.NewGuid():N}";
        var inputId = $"input-local-order-{Guid.NewGuid():N}";
        var turnId = $"turn-local-order-{Guid.NewGuid():N}";
        await OpenJobSessionAsync(sessionId, projectId, jobKey, inputId, turnId, "later launch", initialLaunch: false);
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);

        // An earlier ordinary queued Turn holds the Session's head.
        var earlierInputId = $"input-ordinary-{Guid.NewGuid():N}";
        var earlierTurnId = $"turn-ordinary-{Guid.NewGuid():N}";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            earlierInputId, earlierTurnId, "earlier ordinary follow up", "generic-followup"));
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            inputId,
            turnId,
            "later launch",
            "agent-launch",
            jobKey,
            Runtime: "opencode",
            WorkDir: "/tmp/agent-job-fixture",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));

        var job = JobGrain(jobKey);
        await job.SubmitAsync(MakeInput("later launch", projectId) with
        {
            AgentSessionId = sessionId,
            InitialInputId = inputId,
            InitialTurnId = turnId,
            Runtime = "opencode",
        });

        // The Job stays accepted Pending behind its Session's earlier Turn;
        // the launch neither fails nor runs its initial input.
        var waiting = await LoadLedgerAsync(jobKey);
        var waitingState = JSON.Deserialize<AgentJobState>(waiting.StateJson)!;
        Assert.Equal(AgentJobStatus.Pending, waitingState.Status);
        Assert.Null(waitingState.CapacityClaimedAt);
        Assert.Null(waitingState.InitialInputSubmission);
        Assert.Equal(AgentAvailabilityWaitReasons.CapacityFull, waitingState.WaitingReason);
        await job.CheckTimeoutsAsync();
        Assert.Equal(AgentJobStatus.Pending, await job.GetStatusAsync());

        // Once the earlier Turn settles, the Job claims and dispatches.
        await session.MarkTurnTerminalAsync(
            earlierTurnId,
            AgentTurnStatus.Completed,
            new AgentTurnResult("earlier settled", null, null, null, null));
        await job.CheckTimeoutsAsync();
        await WaitForStatusAsync(job, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var running = await LoadLedgerAsync(jobKey);
        var runningState = JSON.Deserialize<AgentJobState>(running.StateJson)!;
        Assert.NotNull(runningState.CapacityClaimedAt);
        // The claimed Job and its own initial Turn occupy one slot, not two.
        Assert.Equal(1, CapacitySnapshot(projectId).Occupied);
    }

    [Fact]
    public async Task LaterJobBehindEarlierClaimedJob_WaitsUntilTheEarlierJobSettles()
    {
        var (runnerId, projectId) = await RegisterAgentJobRunnerAsync(
            $"agent-job-two-local-{Guid.NewGuid():N}");
        var sessionId = $"session-two-local-{Guid.NewGuid():N}";
        var firstKey = $"agent-job-two-local-1-{Guid.NewGuid():N}";
        var secondKey = $"agent-job-two-local-2-{Guid.NewGuid():N}";
        var firstInput = $"input-two-local-1-{Guid.NewGuid():N}";
        var firstTurn = $"turn-two-local-1-{Guid.NewGuid():N}";
        var runtimeSessionId = $"runtime-two-local-{Guid.NewGuid():N}";
        await OpenJobSessionAsync(sessionId, projectId, firstKey, firstInput, firstTurn, "first launch");
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);

        var first = JobGrain(firstKey);
        await first.SubmitAsync(MakeInput("first launch", projectId) with
        {
            AgentSessionId = sessionId,
            InitialInputId = firstInput,
            InitialTurnId = firstTurn,
            Runtime = "opencode",
        });
        var firstClaim = await LoadLedgerAsync(firstKey);
        var firstClaimState = JSON.Deserialize<AgentJobState>(firstClaim.StateJson)!;
        Assert.NotNull(firstClaimState.CapacityClaimedAt);
        Assert.Equal(1, CapacitySnapshot(projectId).Occupied);

        var secondInput = $"input-two-local-2-{Guid.NewGuid():N}";
        var secondTurn = $"turn-two-local-2-{Guid.NewGuid():N}";
        await session.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            secondInput,
            secondTurn,
            "second launch",
            "agent-launch",
            secondKey,
            Runtime: "opencode",
            WorkDir: "/tmp/agent-job-fixture",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));
        var second = JobGrain(secondKey);
        await second.SubmitAsync(MakeInput("second launch", projectId) with
        {
            AgentSessionId = sessionId,
            InitialInputId = secondInput,
            InitialTurnId = secondTurn,
            Runtime = "opencode",
        });

        // The earlier Job's initial Turn is the Session's deliverable head,
        // so the later Job waits without throwing or looping.
        var waitingState = JSON.Deserialize<AgentJobState>((await LoadLedgerAsync(secondKey)).StateJson)!;
        Assert.Equal(AgentJobStatus.Pending, waitingState.Status);
        Assert.Null(waitingState.CapacityClaimedAt);
        await second.CheckTimeoutsAsync();
        Assert.Equal(AgentJobStatus.Pending, await second.GetStatusAsync());
        Assert.Equal(1, CapacitySnapshot(projectId).Occupied);

        // Settle the first Job; the second claims next.
        await WaitForStatusAsync(first, AgentJobStatus.Running, TimeSpan.FromSeconds(5));
        var firstWork = (await first.GetRuntimeSnapshotAsync()).CurrentWorkId!;
        Assert.True(await first.RecordRuntimeSessionBindingAsync(runnerId, firstWork, sessionId, runtimeSessionId));
        await first.ReportResultAsync(
            runnerId,
            firstWork,
            new WorkResult(
                "completed",
                "first done",
                AgentSessionId: sessionId,
                AgentTurnId: firstTurn,
                Runtime: "opencode",
                RuntimeSessionId: runtimeSessionId));
        await WaitForStatusAsync(first, AgentJobStatus.Completed, TimeSpan.FromSeconds(5));
        await second.CheckTimeoutsAsync();
        await WaitForStatusAsync(second, AgentJobStatus.Running, TimeSpan.FromSeconds(5));

        var secondRunning = JSON.Deserialize<AgentJobState>((await LoadLedgerAsync(secondKey)).StateJson)!;
        Assert.NotNull(secondRunning.CapacityClaimedAt);
        Assert.Equal(1, CapacitySnapshot(projectId).Occupied);
    }
}
