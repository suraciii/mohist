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

namespace Mohist.Server.L0Tests.Specs.Sessions;

public partial class AgentSessionFollowupConcurrencySpecs
{
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

        var cancelled = await session.StopQueuedTurnAsync(accepted.TurnId!);

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

}
