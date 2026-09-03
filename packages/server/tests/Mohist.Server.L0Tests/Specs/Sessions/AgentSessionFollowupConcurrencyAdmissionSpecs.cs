using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans;
using Orleans.Core.Internal;
using Orleans.Runtime;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Sessions;

/// <summary>
/// Issue-520 T-002 acceptance contract: a follow-up that would start
/// a new execution on an idle AgentSession honours the per-agent
/// concurrency gate introduced by T-001. Covers the granted-under,
/// rejected-at, release-on-turn-end, busy-session, and
/// shared-launch-and-followup scenarios in one focused grain
/// collection so they run on a dedicated controlled Orleans cluster without
/// the full HTTP integration stack.
/// </summary>
[Collection("AgentSessionFollowupConcurrency")]
[Trait("level", "L0")]
public partial class AgentSessionFollowupConcurrencySpecs
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
    public async Task LimitBecomingUnbounded_GrantsExistingFollowupWaiter()
    {
        var projectId = $"followup-unbounded-{Guid.NewGuid():N}";
        var agentId = $"agent-{Guid.NewGuid():N}";
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 1);
        var session = await _fixture.OpenGenericAgentSessionAsync(projectId, agentId);
        var gate = Gate(projectId, agentId);

        Assert.Equal(
            AgentConcurrencyAcquireResult.Granted,
            await gate.AcquireAsync(
                projectId,
                agentId,
                "launch:active",
                "job-active",
                AgentConcurrencyPermitOwnerKind.Job));
        var queued = await session.BeginFollowupAsync();
        Assert.False(queued.ConcurrencyPermitHeld);

        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: null);
        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        await gate.ReceiveReminder(
            "agent-concurrency-reconciliation",
            new TickStatus(now, TimeSpan.FromSeconds(30), now));

        var snapshot = await gate.GetSnapshotAsync();
        var permit = Assert.Single(
            snapshot.ActivePermits,
            candidate => candidate.OwnerId == session.GetPrimaryKeyString());
        Assert.DoesNotContain(
            snapshot.Waiters,
            waiter => waiter.OwnerId == session.GetPrimaryKeyString());

        var lease = Assert.Single(_fixture.StateStore.State?.Status.PendingFollowups!);
        Assert.Equal(permit.PermitId, lease.ConcurrencyPermitId);
        Assert.Equal("dispatch-pending", lease.ConcurrencyGateStatus);
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

}
