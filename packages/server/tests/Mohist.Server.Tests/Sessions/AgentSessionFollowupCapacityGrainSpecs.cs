using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Capacity;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans;
using Orleans.Core.Internal;
using Orleans.GrainDirectory;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

/// <summary>
/// The Session owner's derived-capacity contract, proven against the real
/// AgentSessionStore and the real capacity store in the fixture's one
/// hermetic SQLite database with injected time: a queued follow-up claims its
/// slot inside its own serialized grain turn, the durable queued-work
/// reminder keeps that claim reachable across activation loss and
/// crash-before-dispatch, and no stale document, failed flush, or uncertain
/// commit can erase it. Nothing here may be proven by an always-admit fake.
/// </summary>
public sealed partial class AgentSessionFollowupGrainSpecs
{
    private async Task<(IAgentSessionGrain Grain, string SessionId)> CreateCapacitySessionAsync(
        string projectId,
        string agentId,
        int? maxConcurrentRuns,
        string runtimeSessionId)
    {
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns);
        var sessionId = $"capacity-owner-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            "runner-1",
            "opencode",
            WorkDir: "/work",
            Metadata: new AgentSessionMetadata()
                .WithLabel(AgentSessionQueryMetadataKeys.ProjectId, projectId)
                .WithLabel(AgentSessionQueryMetadataKeys.SourceKind, "agent-launch")
                .WithLabel(GenericAgentSessionMetadata.AgentId, agentId)
                .WithLabel(GenericAgentSessionMetadata.AgentName, "capacity-agent")));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId));
        return (grain, sessionId);
    }

    private static async Task DeactivateAsync(IAgentSessionGrain grain)
    {
        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();
        await grain.GetAsync();
    }

    private async Task<AgentSession> PersistedAsync(string sessionId) =>
        await _fixture.StateStore.LoadAsync(sessionId)
        ?? throw new InvalidOperationException($"Missing AgentSession {sessionId}.");

    private static AgentTurnRecord TurnOf(AgentSession session, string turnId) =>
        session.Status.Turns!.Single(turn => string.Equals(turn.Id, turnId, StringComparison.Ordinal));

    private async Task<int?> OccupiedAsync(string projectId, string agentId)
    {
        using var scope = _fixture.SiloServices.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentCapacityStore>();
        var snapshot = (await store.ReadAsync(projectId, [agentId]))[agentId];
        return snapshot.Occupied;
    }

    [Fact]
    public async Task AcceptedFollowup_OlderThanTheRetiredLeaseWindow_StaysQueuedAndClaimsOnce()
    {
        var projectId = $"capacity-lease-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        var (grain, sessionId) = await CreateCapacitySessionAsync(projectId, agentId, 1, "runtime-lease");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "waits far past the retired five-minute lease",
            Source: "agent-session-followup",
            IdempotencyKey: "lease-window"));

        // The retired expiry would have dropped this accepted follow-up here.
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(11));
        var waiting = await PersistedAsync(sessionId);
        Assert.Equal(AgentTurnStatus.Queued, TurnOf(waiting, accepted.TurnId).Status);
        Assert.Null(TurnOf(waiting, accepted.TurnId).CapacityClaimedAt);
        Assert.Contains(waiting.Status.PendingFollowups!, lease => lease.TurnId == accepted.TurnId);

        var dispatch = await grain.BeginNextFollowupDispatchAsync();
        Assert.NotNull(dispatch);
        Assert.Equal(accepted.TurnId, dispatch!.TurnId);
        Assert.Equal(accepted.InputId, dispatch.InputId);
        Assert.Equal(accepted.OperationId, dispatch.OperationId);

        var claimed = await PersistedAsync(sessionId);
        Assert.NotNull(TurnOf(claimed, accepted.TurnId).CapacityClaimedAt);
        Assert.Equal(1, await OccupiedAsync(projectId, agentId));
    }

    [Fact]
    public async Task QueuedWorkReminder_IsPrearmedBeforeAcceptanceAndRestoredOnActivation()
    {
        var projectId = $"capacity-reminder-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        var (grain, sessionId) = await CreateCapacitySessionAsync(projectId, agentId, null, "runtime-reminder");
        var reminders = _fixture.SiloServices.GetRequiredService<IReminderTable>();

        // Before acceptance there is no queued work, so no wake is armed.
        Assert.Null(await reminders.ReadRow(grain.GetGrainId(), "followup-queue"));

        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "durable wake",
            Source: "agent-session-followup",
            IdempotencyKey: "reminder-key"));

        // The wake must already be durable when the acceptance commits: a
        // crash between acceptance and dispatch still re-evaluates the queue.
        Assert.NotNull(await reminders.ReadRow(grain.GetGrainId(), "followup-queue"));

        await DeactivateAsync(grain);
        var reactivated = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await reactivated.GetAsync();
        Assert.NotNull(await reminders.ReadRow(reactivated.GetGrainId(), "followup-queue"));

        // The reminder wakes the established dispatcher; it neither dispatches
        // nor grants anything itself.
        _fixture.FollowupDispatch.Reset();
        await reactivated.AsReference<IRemindable>().ReceiveReminder("followup-queue", default);
        var request = Assert.Single(_fixture.FollowupDispatch.Requests);
        Assert.Equal(projectId, request.ProjectId);
        Assert.Equal(sessionId, request.SessionId);
        var stillQueued = await PersistedAsync(sessionId);
        Assert.Equal(AgentTurnStatus.Queued, TurnOf(stillQueued, accepted.TurnId).Status);
        Assert.False(IsDispatching(stillQueued, accepted.TurnId));

        // Draining the queue retires the wake instead of leaving it to fire.
        await reactivated.BeginNextFollowupDispatchAsync();
        await reactivated.MarkFollowupTurnExecutingAsync(accepted.OperationId);
        await reactivated.MarkFollowupTurnTerminalAsync(accepted.OperationId, AgentTurnStatus.Completed, null);
        _fixture.FollowupDispatch.Reset();
        await reactivated.AsReference<IRemindable>().ReceiveReminder("followup-queue", default);
        Assert.Empty(_fixture.FollowupDispatch.Requests);
        Assert.Null(await reminders.ReadRow(reactivated.GetGrainId(), "followup-queue"));
    }

    private static bool IsDispatching(AgentSession session, string turnId) =>
        session.Status.PendingFollowups!
            .Single(lease => string.Equals(lease.TurnId, turnId, StringComparison.Ordinal))
            .Dispatching;

    [Fact]
    public async Task ClaimedQueuedHead_SurvivesActivationLossAndDispatchesExactlyOnce()
    {
        var projectId = $"capacity-crash-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        var (grain, sessionId) = await CreateCapacitySessionAsync(projectId, agentId, 1, "runtime-crash");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "claimed before the crash",
            Source: "agent-session-followup",
            IdempotencyKey: "crash-key"));

        // Crash after the claim and the sealed dispatch, before delivery.
        var first = await grain.BeginNextFollowupDispatchAsync();
        Assert.NotNull(first);
        var claimedAt = TurnOf(await PersistedAsync(sessionId), accepted.TurnId).CapacityClaimedAt;
        Assert.NotNull(claimedAt);

        await DeactivateAsync(grain);
        var recovered = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await recovered.GetAsync();

        // The claimed head is still deliverable without re-entering the
        // cross-Session queue, and the existing claim is not a second slot.
        var second = await recovered.BeginNextFollowupDispatchAsync();
        Assert.NotNull(second);
        Assert.Equal(accepted.TurnId, second!.TurnId);
        Assert.Equal(accepted.OperationId, second.OperationId);
        Assert.Equal(accepted.InputId, second.InputId);
        var redelivered = await PersistedAsync(sessionId);
        Assert.Equal(claimedAt, TurnOf(redelivered, accepted.TurnId).CapacityClaimedAt);
        Assert.Equal(1, await OccupiedAsync(projectId, agentId));

        // Exactly one dispatch is in flight now.
        Assert.Null(await recovered.BeginNextFollowupDispatchAsync());
    }

    [Fact]
    public async Task FailedFlush_WritesNoClaimAndKeepsTheTurnQueued()
    {
        var projectId = $"capacity-flush-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        var (grain, sessionId) = await CreateCapacitySessionAsync(projectId, agentId, null, "runtime-flush");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "flush fails before the claim",
            Source: "agent-session-followup",
            IdempotencyKey: "flush-key"));

        _fixture.TranscriptStore.FailNextSave(
            sessionId,
            new InvalidOperationException("transcript unavailable"));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.MessageDelta,
                """{"text":"pending transcript part"}""") },
            "runtime-flush"));

        Assert.Null(await grain.BeginNextFollowupDispatchAsync());
        var persisted = await PersistedAsync(sessionId);
        Assert.Null(TurnOf(persisted, accepted.TurnId).CapacityClaimedAt);
        Assert.False(IsDispatching(persisted, accepted.TurnId));
        Assert.Equal(0, await OccupiedAsync(projectId, agentId));

        // The queued work is still reachable: the retry claims it.
        Assert.NotNull(await grain.BeginNextFollowupDispatchAsync());
        Assert.NotNull(TurnOf(await PersistedAsync(sessionId), accepted.TurnId).CapacityClaimedAt);
    }

    [Fact]
    public async Task CommitThenThrow_QuarantinesTheActivationWithoutLosingTheCommit()
    {
        var projectId = $"capacity-throw-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        var (grain, sessionId) = await CreateCapacitySessionAsync(projectId, agentId, null, "runtime-throw");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "claimed before the transport failed",
            Source: "agent-session-followup",
            IdempotencyKey: "throw-key"));

        // The capacity store commits the claim, the whole-document write
        // commits, and then the transport fails: the activation must not
        // continue, and the pre-claim document must not be rewritten.
        _fixture.StateStore.CommitThenThrowNextSave(sessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.BeginNextFollowupDispatchAsync());
        var committed = await PersistedAsync(sessionId);
        Assert.NotNull(TurnOf(committed, accepted.TurnId).CapacityClaimedAt);
        Assert.Equal(1, await OccupiedAsync(projectId, agentId));

        // The next activation loads the committed claim and continues from it
        // instead of stranding the queued work or re-entering the queue.
        var reloaded = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await reloaded.GetAsync();
        var recovered = await reloaded.BeginNextFollowupDispatchAsync();
        Assert.NotNull(recovered);
        Assert.Equal(accepted.OperationId, recovered!.OperationId);
        Assert.Equal(1, await OccupiedAsync(projectId, agentId));
        await reloaded.MarkFollowupTurnTerminalAsync(accepted.OperationId, AgentTurnStatus.Completed, null);
        Assert.Equal(0, await OccupiedAsync(projectId, agentId));
    }

    [Fact]
    public async Task StaleStateDocument_CannotOverwriteTheNewerClaim()
    {
        var projectId = $"capacity-stale-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        var (grain, sessionId) = await CreateCapacitySessionAsync(projectId, agentId, null, "runtime-stale");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "exact document claim",
            Source: "agent-session-followup",
            IdempotencyKey: "stale-key"));

        // The document the owner would have compared against before the claim.
        var staleToken = await _fixture.StateStore.ReadStateJsonAsync(sessionId);
        Assert.NotNull(staleToken);
        Assert.NotNull(await grain.BeginNextFollowupDispatchAsync());
        var claimedAt = TurnOf(await PersistedAsync(sessionId), accepted.TurnId).CapacityClaimedAt;
        Assert.NotNull(claimedAt);

        using var scope = _fixture.SiloServices.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentCapacityStore>();

        // Another writer presenting the superseded document fails closed, and
        // the committed claim survives the rejected compare-and-set.
        var stale = await store.ClaimTurnAsync(sessionId, staleToken!, accepted.TurnId);
        Assert.Equal(AgentCapacityClaimDisposition.Conflict, stale.Disposition);
        Assert.Equal(claimedAt, TurnOf(await PersistedAsync(sessionId), accepted.TurnId).CapacityClaimedAt);

        // A later whole-document write from the persist timer cannot erase it.
        await grain.MarkFollowupTurnExecutingAsync(accepted.OperationId);
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.MessageDelta,
                """{"text":"pending transcript part"}""") },
            "runtime-stale"));
        await persistence.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(claimedAt, TurnOf(await PersistedAsync(sessionId), accepted.TurnId).CapacityClaimedAt);
    }

    [Fact]
    public async Task CapacityFull_KeepsTheAcceptedTurnQueuedUntilASlotFrees()
    {
        var projectId = $"capacity-full-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        var (waiting, waitingSessionId) = await CreateCapacitySessionAsync(projectId, agentId, 1, "runtime-waiting");
        var (occupant, _) = await CreateCapacitySessionAsync(projectId, agentId, 1, "runtime-occupant");

        // The occupant wins the single slot on acceptance order, not on which
        // Session happens to ask first.
        var occupantTurn = await occupant.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "occupies the slot",
            Source: "agent-session-followup",
            IdempotencyKey: "occupant-key"));
        // Acceptance order decides the cross-Session queue, so the later
        // acceptance must be stamped later than the earlier one.
        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        var queued = await waiting.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "waits for a slot",
            Source: "agent-session-followup",
            IdempotencyKey: "waiting-key"));
        Assert.NotNull(await occupant.BeginNextFollowupDispatchAsync());
        await occupant.MarkFollowupTurnExecutingAsync(occupantTurn.OperationId);
        Assert.Equal(1, await OccupiedAsync(projectId, agentId));

        // The waiting Session's turn is admitted by no conclusion at all -
        // neither a false capacity-full nor a claim out of FIFO order.
        Assert.Null(await waiting.BeginNextFollowupDispatchAsync());
        var blocked = await PersistedAsync(waitingSessionId);
        Assert.Null(TurnOf(blocked, queued.TurnId).CapacityClaimedAt);
        Assert.Equal(AgentTurnStatus.Queued, TurnOf(blocked, queued.TurnId).Status);
        Assert.False(IsDispatching(blocked, queued.TurnId));

        await occupant.MarkFollowupTurnTerminalAsync(occupantTurn.OperationId, AgentTurnStatus.Completed, null);
        Assert.Equal(0, await OccupiedAsync(projectId, agentId));

        Assert.NotNull(await waiting.BeginNextFollowupDispatchAsync());
        Assert.NotNull(TurnOf(await PersistedAsync(waitingSessionId), queued.TurnId).CapacityClaimedAt);
    }

    [Fact]
    public async Task MissingAgentDefinition_KeepsAcceptedWorkQueuedWithoutACapacityFullConclusion()
    {
        var projectId = $"capacity-missing-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        var (grain, sessionId) = await CreateCapacitySessionAsync(projectId, agentId, null, "runtime-missing");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "no definition to admit against",
            Source: "agent-session-followup",
            IdempotencyKey: "missing-key"));

        await using var db = _fixture.CreateDbContext();
        var row = await db.Agents.SingleAsync(candidate => candidate.Id == GrainKey.Agent(projectId, agentId));
        db.Agents.Remove(row);
        await db.SaveChangesAsync();

        using var scope = _fixture.SiloServices.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentCapacityStore>();
        var snapshot = (await store.ReadAsync(projectId, [agentId]))[agentId];
        Assert.Equal(AgentCapacityEvidenceStatus.MissingDefinition, snapshot.EvidenceStatus);

        Assert.Null(await grain.BeginNextFollowupDispatchAsync());
        var persisted = await PersistedAsync(sessionId);
        Assert.Null(TurnOf(persisted, accepted.TurnId).CapacityClaimedAt);
        Assert.Equal(AgentTurnStatus.Queued, TurnOf(persisted, accepted.TurnId).Status);
        Assert.Equal(AgentSessionInputAcceptance.Accepted, accepted.InputAcceptance);
    }

    [Fact]
    public async Task InitialJobTurn_IsNotDoubleCountedByTheFollowupClaim()
    {
        var projectId = $"capacity-initial-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        var (grain, sessionId) = await CreateCapacitySessionAsync(projectId, agentId, 1, "runtime-initial");
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: "initial-input",
            TurnId: "initial-turn",
            Prompt: "initial prompt",
            Source: "agent-launch",
            JobId: "initial-job",
            Metadata: (await PersistedAsync(sessionId)).Metadata));
        await grain.MarkInitialTurnExecutingAsync("initial-job");
        var followup = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "queued behind the launch turn",
            Source: "agent-session-followup",
            IdempotencyKey: "initial-behind"));

        using var scope = _fixture.SiloServices.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentCapacityStore>();
        var snapshot = (await store.ReadAsync(projectId, [agentId]))[agentId];
        Assert.Equal(0, snapshot.Occupied);

        // The Job-owned initial Turn occupies through the launch Job, never a
        // second time through this Session, so the follow-up may claim the
        // single slot.
        await grain.MarkInitialTurnTerminalAsync("initial-job", AgentTurnStatus.Completed, null);
        Assert.NotNull(await grain.BeginNextFollowupDispatchAsync());
        Assert.Equal(1, await OccupiedAsync(projectId, agentId));
        Assert.Equal(followup.TurnId, TurnOf(await PersistedAsync(sessionId), followup.TurnId).Id);
    }

    [Fact]
    public async Task UnresolvedUnknown_BlocksTheClaimAndItsReclaimAfterConvergence()
    {
        var projectId = $"capacity-fence-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        var (grain, sessionId) = await CreateCapacitySessionAsync(projectId, agentId, null, "runtime-fence");
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            InputId: "fence-initial-input",
            TurnId: "fence-initial-turn",
            Prompt: "initial prompt",
            Source: "agent-launch",
            JobId: "fence-initial-job",
            Metadata: (await PersistedAsync(sessionId)).Metadata));
        await grain.MarkInitialTurnExecutingAsync("fence-initial-job");

        // An unresolved Unknown holds the Session: work accepted beside it
        // stays queued with no claim.
        var blocked = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "blocked by the unresolved unknown",
            Source: "agent-session-followup",
            IdempotencyKey: "fence-key"));
        await grain.MarkInitialTurnTerminalAsync("fence-initial-job", AgentTurnStatus.Unknown, null);
        Assert.Null(await grain.BeginNextFollowupDispatchAsync());
        Assert.Null(TurnOf(await PersistedAsync(sessionId), blocked.TurnId).CapacityClaimedAt);

        // Convergence is settled from durable Runner evidence, not inferred.
        var request = await grain.PrepareActivityProbeAsync("runner-1");
        Assert.True(await grain.ApplyActivityProbeAsync(
            new RunnerSessionActivityProbeResult(request!, RunnerSessionActivityObservations.Idle)));

        // Work accepted after convergence claims normally.
        var admitted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "accepted after convergence",
            Source: "agent-session-followup",
            IdempotencyKey: "fence-key-2"));
        Assert.NotNull(await grain.BeginNextFollowupDispatchAsync());
        Assert.NotNull(TurnOf(await PersistedAsync(sessionId), admitted.TurnId).CapacityClaimedAt);
    }

    [Fact]
    public async Task CancelledTurn_IsNotAClaimCandidateAndTheQueueContinues()
    {
        var projectId = $"capacity-superseded-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        var (grain, sessionId) = await CreateCapacitySessionAsync(projectId, agentId, null, "runtime-superseded");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "old context turn",
            Source: "agent-session-followup",
            IdempotencyKey: "superseded-key"));
        await grain.StopQueuedTurnAsync(accepted.TurnId);

        var cancelled = TurnOf(await PersistedAsync(sessionId), accepted.TurnId);
        Assert.Equal(AgentTurnStatus.Cancelled, cancelled.Status);

        // A cancelled Turn is neither eligible nor occupied, and a fresh
        // follow-up on the current context claims normally.
        Assert.Null(await grain.BeginFollowupDispatchForTurnAsync(accepted.TurnId));
        Assert.Equal(0, await OccupiedAsync(projectId, agentId));
        var next = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "current context turn",
            Source: "agent-session-followup",
            IdempotencyKey: "current-key"));
        Assert.NotNull(await grain.BeginNextFollowupDispatchAsync());
        Assert.NotNull(TurnOf(await PersistedAsync(sessionId), next.TurnId).CapacityClaimedAt);
    }
}
