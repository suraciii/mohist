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

namespace Mohist.Server.Tests.Sessions;

public partial class AgentSessionFollowupConcurrencySpecs
{
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

    private static async Task DeactivateAsync(IAgentSessionGrain grain)
    {
        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();
        await grain.GetAsync();
    }
}
