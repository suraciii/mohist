using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Grains;
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
        Assert.True(reservation.ConcurrencyPermitHeld,
            "An idle follow-up on an untracked (null) MaxConcurrentRuns still acquires no permit but the flag tracks whether one was held; verify intent is null/no-permit when the gate does not apply.");

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
    public async Task BeginFollowupAsync_IdleSession_AtLimit_RejectsWithRetryableReason()
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

        var thrower = async () => await session.BeginFollowupAsync();
        var ex = await Assert.ThrowsAsync<FollowupConcurrencyLimitException>(thrower);
        Assert.Equal(agentId, ex.AgentId);
        Assert.Contains(session.GetPrimaryKeyString(), ex.SessionId);

        // No input/lease is persisted by the failed attempt; the gate
        // remained saturated by the launch-shaped caller alone.
        var info = await session.GetAsync();
        Assert.NotNull(info);
        var persisted = _fixture.StateStore.State?.Status.PendingFollowups;
        Assert.True(persisted is null or { Count: 0 });

        // After the throw, releasing the launcher-shaped permit lets a
        // retry succeed with the same idempotent identity.
        await gate.ReleaseAsync(projectId, agentId, "launch:job-1");
        var retry = await session.BeginFollowupAsync();
        Assert.True(retry.ConcurrencyPermitHeld);
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
        // follow-up-shaped permit; the third attempt (a follow-up) must be
        // rejected at the limit.
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
        var thrower = async () => await secondSession.BeginFollowupAsync();
        var ex = await Assert.ThrowsAsync<FollowupConcurrencyLimitException>(thrower);
        Assert.Equal(agentId, ex.AgentId);

        // Raising the limit transparently lets the previously-rejected
        // follow-up through, verifying the same-grain authority.
        await _fixture.SeedAgentAsync(projectId, agentId, maxConcurrentRuns: 4);
        var retry = await secondSession.BeginFollowupAsync();
        Assert.True(retry.ConcurrencyPermitHeld);
        Assert.Equal(3, await gate.GetActiveCountAsync());
    }

    private static async Task DeactivateAsync(IAgentSessionGrain grain)
    {
        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();
        await grain.GetAsync();
    }
}
