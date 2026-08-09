using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Orleans;
using Orleans.Core.Internal;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Issue-520 T-002 acceptance contract: a follow-up that would start
/// a new execution on an idle AgentSession honours the per-agent
/// concurrency gate introduced by T-001. Covers the granted-under,
/// rejected-at, release-on-turn-end, busy-session, and
/// shared-launch-and-followup scenarios in one focused grain
/// collection so they run on a dedicated InProcessTestCluster without
/// the full HTTP integration stack.
/// </summary>
[CollectionDefinition("AgentSessionFollowupConcurrency")]
public class AgentSessionFollowupConcurrencyCollection
    : ICollectionFixture<AgentSessionFollowupConcurrencyFixture>;

[Collection("AgentSessionFollowupConcurrency")]
public class AgentSessionFollowupConcurrencySpecs
{
    private readonly AgentSessionFollowupConcurrencyFixture _fixture;

    public AgentSessionFollowupConcurrencySpecs(AgentSessionFollowupConcurrencyFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    private IGrainFactory Grains => _fixture.Grains;
    private IAgentConcurrencyGrain Gate(string projectId, string agentId) =>
        Grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId));

    [Fact]
    public async Task BeginFollowupAsync_IdleSession_NoLimit_AcquiresPermit()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: null);
        var session = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);

        var reservation = await session.BeginFollowupAsync();

        Assert.NotNull(reservation.OperationId);
        Assert.False(reservation.ConcurrencyPermitHeld);

        // Default = null limit ⇒ acquire path returns Granted without
        // touching the in-memory concurrency state. No permits tracked.
        var gate = Gate(projectId, agentId);
        Assert.Equal(0, await gate.GetActiveCountAsync());
    }

    [Fact]
    public async Task BeginFollowupAsync_IdleSession_UnderLimit_AcquiresPermit()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 2);
        var session = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);
        var gate = Gate(projectId, agentId);

        var reservation = await session.BeginFollowupAsync();

        Assert.True(reservation.ConcurrencyPermitHeld);
        Assert.True(reservation.OperationId is not null);
        Assert.Equal(1, await gate.GetActiveCountAsync());
    }

    [Fact]
    public async Task BeginFollowupAsync_IdleSession_AtLimit_PersistsQueuedWaiter()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var session = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);
        var gate = Gate(projectId, agentId);

        // Saturate the gate with one permit from a separate (launch-shaped)
        // caller. The follow-up path must be subject to the same authority.
        var existing = await gate.AcquireAsync(
            projectId,
            agentId,
            "launch:job-1",
            "job-1",
            AgentConcurrencyPermitOwnerKind.Job);
        Assert.Equal(AgentConcurrencyAcquireResult.Granted, existing);
        Assert.Equal(1, await gate.GetActiveCountAsync());

        var reservation = await session.BeginFollowupAsync();
        Assert.False(reservation.ConcurrencyPermitHeld);
        var info = await session.GetAsync();
        Assert.NotNull(info);
        var persisted = _fixture.StateStore.State?.Status.PendingFollowups;
        var lease = Assert.Single(persisted!);
        Assert.Equal("queued", lease.ConcurrencyGateStatus);
        Assert.Equal(AgentAvailabilityWaitReasons.CapacityFull, lease.WaitingReason);
        Assert.Contains(
            await gate.GetWaitersAsync(),
            waiter => waiter.OwnerKind == AgentConcurrencyPermitOwnerKind.Followup
                && waiter.OwnerId == session.GetPrimaryKeyString());

        // Releasing the launcher-shaped permit grants the durable waiter.
        await gate.ReleaseAsync(projectId, agentId, "launch:job-1");
        Assert.Equal(1, await gate.GetActiveCountAsync());
        Assert.Empty(await gate.GetWaitersAsync());
    }

    [Fact]
    public async Task BeginFollowupAsync_BusySession_UnaffectedNoPermit()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var opened = await _fixture.OpenGenericAgentSessionWithRuntimeIdAsync(projectId, agentId);
        var session = opened.Grain;
        var gate = Gate(projectId, agentId);

        // Session is Idle by default; flip it to Active via a system event
        // so the follow-up path determines the gate does not apply.
        await session.AppendSystemEventsAsync(new AppendAgentSessionSystemEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionActivity,
                PayloadJson: "{\"activity\":\"active\"}"),
        }));

        var reservation = await session.BeginFollowupAsync();

        Assert.NotNull(reservation.OperationId);
        Assert.False(reservation.ConcurrencyPermitHeld,
            "Busy-session follow-up joins the active turn via per-session serial and must not acquire a permit.");
        Assert.Equal(0, await gate.GetActiveCountAsync());

        // Even with the per-agent gate at its limit, a busy-session
        // follow-up does not consult the gate: per-session serial is the
        // only authority for a follow-up that joins an already-active
        // session. Confirm and abandon the first follow-up before trying
        // a second one — the busy-session path is unaffected in both
        // directions: the gate is not acquired, and the gate being at the
        // limit does not produce a concurrency-limit rejection.
        await gate.AcquireAsync(
            projectId,
            agentId,
            "launch:busy",
            "job-busy",
            AgentConcurrencyPermitOwnerKind.Job);
        Assert.Equal(1, await gate.GetActiveCountAsync());

        await session.AbandonFollowupAsync(reservation.OperationId!);

        var secondReservation = await session.BeginFollowupAsync();
        Assert.False(secondReservation.ConcurrencyPermitHeld,
            "Busy-session follow-up must never consult the per-agent gate.");
        Assert.Equal(1, await gate.GetActiveCountAsync());
    }

    [Fact]
    public async Task BeginFollowupAsync_IdleSession_ReleasesPermitOnTurnEnd()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var opened = await _fixture.OpenGenericAgentSessionWithRuntimeIdAsync(projectId, agentId);
        var session = opened.Grain;
        var gate = Gate(projectId, agentId);

        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.ConcurrencyPermitHeld);
        Assert.Equal(1, await gate.GetActiveCountAsync());

        // Runner signals turn end with a session.activity idle matching the
        // follow-up's operation id; the lease is cleared and the per-agent
        // permit is released.
        await session.AppendSystemEventsAsync(new AppendAgentSessionSystemEventsCommand(new[]
        {
            new AgentSessionRuntimeEventInput(
                Type: RuntimeEventTypes.SessionActivity,
                PayloadJson: "{\"activity\":\"idle\",\"status\":\"completed\",\"operationId\":\"" + reservation.OperationId + "\"}"),
        }));
        await DeactivateAsync(session);

        Assert.Equal(0, await gate.GetActiveCountAsync());

        // After release the slot is reusable for the next follow-up.
        var retry = await session.BeginFollowupAsync();
        Assert.True(retry.ConcurrencyPermitHeld);
        Assert.Equal(1, await gate.GetActiveCountAsync());
    }

    [Fact]
    public async Task BeginFollowupAsync_ActiveLease_SurvivesConcurrencyReconciliation()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var session = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);
        var gate = Gate(projectId, agentId);

        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.ConcurrencyPermitHeld);

        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        await gate.ReceiveReminder(
            "agent-concurrency-reconciliation",
            new TickStatus(now, TimeSpan.FromSeconds(30), now));

        Assert.Equal(1, await gate.GetActiveCountAsync());
    }

    [Fact]
    public async Task Reconciliation_retains_a_grant_until_the_followup_lease_is_persisted()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var session = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);
        var gate = Gate(projectId, agentId);
        var token = $"followup:{session.GetPrimaryKeyString()}:race";

        Assert.Equal(
            AgentConcurrencyAcquireResult.Granted,
            await gate.AcquireAsync(
                projectId,
                agentId,
                token,
                session.GetPrimaryKeyString(),
                AgentConcurrencyPermitOwnerKind.Followup));

        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        await gate.ReceiveReminder(
            "agent-concurrency-reconciliation",
            new TickStatus(now, TimeSpan.FromSeconds(30), now));

        Assert.Equal(1, await gate.GetActiveCountAsync());

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        await gate.ReceiveReminder(
            "agent-concurrency-reconciliation",
            new TickStatus(now, TimeSpan.FromSeconds(30), now));

        Assert.Equal(0, await gate.GetActiveCountAsync());
    }

    [Fact]
    public async Task BeginFollowupAsync_AbandonLease_ReleasesPermit()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var session = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);
        var gate = Gate(projectId, agentId);

        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.ConcurrencyPermitHeld);
        Assert.Equal(1, await gate.GetActiveCountAsync());

        await session.AbandonFollowupAsync(reservation.OperationId!);

        Assert.Equal(0, await gate.GetActiveCountAsync());
    }

    [Fact]
    public async Task BeginFollowupAsync_SharedLimitAcrossLaunchAndFollowup()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 2);
        var gate = Gate(projectId, agentId);

        // The same per-agent gate enforces both the launch path and the
        // follow-up path. Saturate with one launch-shaped permit and one
        // follow-up-shaped permit; the third attempt becomes a durable
        // waiter instead of being rejected.
        var launchReservation = await gate.AcquireAsync(
            projectId,
            agentId,
            "launch:job-1",
            "job-1",
            AgentConcurrencyPermitOwnerKind.Job);
        Assert.Equal(AgentConcurrencyAcquireResult.Granted, launchReservation);

        var session = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);
        var followUpReservation = await session.BeginFollowupAsync();
        Assert.True(followUpReservation.ConcurrencyPermitHeld);
        Assert.Equal(2, await gate.GetActiveCountAsync());

        var secondSession = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);
        var queued = await secondSession.BeginFollowupAsync();
        Assert.False(queued.ConcurrencyPermitHeld);
        Assert.Contains(
            await gate.GetWaitersAsync(),
            waiter => waiter.OwnerKind == AgentConcurrencyPermitOwnerKind.Followup
                && waiter.OwnerId == secondSession.GetPrimaryKeyString());

        // Raising the limit transparently lets the previously-queued
        // follow-up through, verifying the same-grain authority.
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 4);
        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        await gate.ReceiveReminder(
            "agent-concurrency-reconciliation",
            new TickStatus(now, TimeSpan.FromSeconds(30), now));
        Assert.Equal(3, await gate.GetActiveCountAsync());
    }

    [Fact]
    public async Task FollowupDispatch_AtCapacity_PersistsQueuedTurnAndOriginalToken()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var session = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);
        var gate = Gate(projectId, agentId);

        Assert.Equal(
            AgentConcurrencyAcquireResult.Granted,
            await gate.AcquireAsync(projectId, agentId, "launch:active", "job-active", AgentConcurrencyPermitOwnerKind.Job));

        var accepted = await session.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "queued while full",
            Source: "agent-session-followup",
            IdempotencyKey: "queued-while-full"));

        Assert.Null(await session.BeginNextFollowupDispatchAsync());

        var state = await _fixture.StateStore.LoadAsync(session.GetPrimaryKeyString());
        var turn = Assert.Single(state!.Status.Turns!);
        Assert.Equal(AgentTurnStatus.Queued, turn.Status);
        var lease = Assert.Single(state.Status.PendingFollowups!);
        var token = $"followup:{session.GetPrimaryKeyString()}:{accepted.OperationId}";
        Assert.Equal(token, lease.ConcurrencyToken);
        Assert.Contains(
            await gate.GetWaitersAsync(),
            waiter => waiter.Token == token && waiter.OwnerKind == AgentConcurrencyPermitOwnerKind.Followup);
    }

    [Fact]
    public async Task FollowupDispatch_Release_GrantsTheOriginalQueuedToken()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var first = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);
        var second = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);
        var gate = Gate(projectId, agentId);

        var firstAccepted = await first.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first",
            Source: "agent-session-followup",
            IdempotencyKey: "first"));
        Assert.NotNull(await first.BeginNextFollowupDispatchAsync());

        var secondAccepted = await second.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second",
            Source: "agent-session-followup",
            IdempotencyKey: "second"));
        Assert.Null(await second.BeginNextFollowupDispatchAsync());

        var secondToken = $"followup:{second.GetPrimaryKeyString()}:{secondAccepted.OperationId}";
        await gate.ReleaseAsync(
            projectId,
            agentId,
            $"followup:{first.GetPrimaryKeyString()}:{firstAccepted.OperationId}");

        Assert.Empty(await gate.GetWaitersAsync());
        Assert.Contains(secondToken, await gate.GetActiveTokensAsync());
        Assert.NotNull(await second.BeginNextFollowupDispatchAsync());
    }

    [Fact]
    public async Task QueuedFollowupCancellation_RemovesWaiterWithoutReleasingOtherPermit()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var session = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);
        var gate = Gate(projectId, agentId);

        Assert.Equal(
            AgentConcurrencyAcquireResult.Granted,
            await gate.AcquireAsync(projectId, agentId, "launch:active", "job-active", AgentConcurrencyPermitOwnerKind.Job));

        var accepted = await session.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "cancel me while queued",
            Source: "agent-session-followup",
            IdempotencyKey: "cancel-while-queued"));
        Assert.Null(await session.BeginNextFollowupDispatchAsync());

        var cancelled = await session.CancelQueuedTurnAsync(accepted.TurnId!);

        Assert.True(cancelled.Cancelled);
        Assert.DoesNotContain(
            await gate.GetWaitersAsync(),
            waiter => waiter.Token == $"followup:{session.GetPrimaryKeyString()}:{accepted.OperationId}");
        Assert.Equal(1, await gate.GetActiveCountAsync());
        await gate.ReleaseAsync(projectId, agentId, "launch:active");
        Assert.Equal(0, await gate.GetActiveCountAsync());
    }

    [Fact]
    public async Task GateGrant_NotificationFailure_RetainsPermitAndDurableRetryRecord()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var gate = Gate(projectId, agentId);

        Assert.Equal(
            AgentConcurrencyAcquireResult.Granted,
            await gate.AcquireAsync(projectId, agentId, "launch:active", "job-active", AgentConcurrencyPermitOwnerKind.Job));
        Assert.Equal(
            AgentConcurrencyAcquireResult.Waiting,
            await gate.AcquireAsync(
                projectId,
                agentId,
                "followup:missing",
                "missing-session",
                AgentConcurrencyPermitOwnerKind.Followup,
                "followup:missing"));

        await gate.ReleaseAsync(projectId, agentId, "launch:active");

        var snapshot = await gate.GetSnapshotAsync();
        var permit = Assert.Single(snapshot.ActivePermits);
        Assert.Equal("followup:missing", permit.DispatchId);
        Assert.Equal(AgentConcurrencyPermitStatus.DispatchPending, permit.Status);
        var notification = Assert.Single(snapshot.PendingNotifications);
        Assert.Equal(permit.PermitId, notification.PermitId);
        Assert.True(notification.Attempts > 0);

        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        await gate.ReceiveReminder(
            "agent-concurrency-reconciliation",
            new TickStatus(now, TimeSpan.FromSeconds(30), now));
        var recovered = await gate.GetSnapshotAsync();
        Assert.Single(recovered.ActivePermits);
        Assert.Single(recovered.PendingNotifications);
        Assert.True(recovered.PendingNotifications[0].Attempts > notification.Attempts);
    }

    [Fact]
    public async Task StalePermitRelease_DoesNotRemoveNewGeneration()
    {
        var projectId = $"followup-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var gate = Gate(projectId, agentId);

        Assert.Equal(
            AgentConcurrencyAcquireResult.Granted,
            await gate.AcquireAsync(projectId, agentId, "same-token", "owner", AgentConcurrencyPermitOwnerKind.Job, "dispatch-1"));
        var first = await gate.GetPermitAsync("same-token");
        Assert.NotNull(first);
        await gate.ReleaseAsync(projectId, agentId, "same-token", first.PermitId, first.Generation);

        Assert.Equal(
            AgentConcurrencyAcquireResult.Granted,
            await gate.AcquireAsync(projectId, agentId, "same-token", "owner", AgentConcurrencyPermitOwnerKind.Job, "dispatch-2"));
        var second = await gate.GetPermitAsync("same-token");
        Assert.NotNull(second);
        Assert.NotEqual(first.PermitId, second.PermitId);

        await gate.ReleaseAsync(projectId, agentId, "same-token", first.PermitId, first.Generation);
        var current = await gate.GetPermitAsync("same-token");
        Assert.NotNull(current);
        Assert.Equal(second.PermitId, current.PermitId);
    }

    private static async Task DeactivateAsync(IAgentSessionGrain grain)
    {
        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();
        await grain.GetAsync();
    }
}
