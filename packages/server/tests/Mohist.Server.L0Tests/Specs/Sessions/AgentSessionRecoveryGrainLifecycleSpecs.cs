using System.Text.Json;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Sessions;

public sealed partial class AgentSessionRecoveryGrainSpecs
{
    [Fact]
    public async Task ManagerCredentialExpiry_CreatesOneRecoveryTurnFromInitialSlackProvenance()
    {
        var sessionId = $"manager-expiry-{Guid.NewGuid():N}";
        var initialProvenance = new AgentSessionInputProvenance(
            "slack",
            "workspace-1",
            "conversation-1",
            "thread-1",
            "member-1",
            "message-1",
            "connection-1",
            "thread-1");
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            "runner-1",
            "opencode",
            WorkDir: "/work",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = SlackDeliveryOwnerIds.ManagerProjectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "manager-agent",
            })));
        await grain.EnsureInitialLaunchAsync(new EnsureInitialLaunchCommand(
            "initial-input",
            "initial-turn",
            "manager request",
            "agent-launch",
            "manager-job",
            Provenance: initialProvenance));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-1"));
        await grain.MarkInitialTurnTerminalAsync("manager-job", AgentTurnStatus.Completed, null);

        var followup = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            "continue",
            "agent-session-followup",
            "manager-followup-1",
            Provenance: initialProvenance));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $$"""{"activity":"unknown","status":"unknown","reason":"manager-credential-expired","failureCategory":"unknown","operationId":"{{followup.OperationId}}","turnId":"{{followup.TurnId}}"}""" ) },
            "runtime-1",
            SessionTurnId: followup.TurnId));

        await grain.EnsureManagerCredentialExpiryRecoveryAsync();
        await grain.EnsureManagerCredentialExpiryRecoveryAsync();

        var state = Assert.IsType<AgentSession>(await _fixture.StateStore.LoadAsync(sessionId));
        var recoveryInput = Assert.Single(
            state.Status.Inputs!,
            input => input.Id == $"manager-recovery-input:{sessionId}");
        var recoveryTurn = Assert.Single(
            state.Status.Turns!,
            turn => turn.Id == $"manager-recovery-turn:{sessionId}");
        Assert.Equal(AgentTurnStatus.Queued, recoveryTurn.Status);
        Assert.Equal(initialProvenance, recoveryInput.Provenance);
        Assert.Equal("manager-recovery:manager-credential-expired", recoveryInput.Source);
        Assert.Single(state.Status.Inputs!, input => input.Id == recoveryInput.Id);
        Assert.Single(state.Status.Turns!, turn => turn.Id == recoveryTurn.Id);
    }

    [Fact]
    public async Task Compact_AfterFollowupTurnTerminal_AllowsRecovery()
    {
        // The single-step AcceptFollowupAsync persists a SessionInput
        // and an AgentTurn that progress queued → executing → terminal.
        // The non-terminal follow-up turn blocks Compact/Reset; the
        // Idle session.activity event for the matching operationId
        // marks the turn terminal and unblocks recovery.
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-followup");

        var accept = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "follow up text",
            Source: "agent-session-followup",
            IdempotencyKey: "followup-1"));
        Assert.NotNull(accept.InputId);
        Assert.NotNull(accept.TurnId);

        // The non-terminal follow-up turn blocks Compact until the
        // session.activity idle event for the operationId marks it
        // terminal.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CompactAsync(new CompactAgentSessionCommand(Summary: "summary")));

        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $$"""{"activity":"idle","operationId":"{{accept.OperationId}}","status":"completed"}""") },
            "runtime-followup"));
        await persistence.WaitAsync();

        var state = Assert.IsType<AgentSession>(await _fixture.StateStore.LoadAsync(sessionId));
        Assert.Equal(AgentSessionActivity.Idle, state.Status.Activity);
        Assert.Empty(state.Status.PendingFollowups ?? []);
        var result = await grain.CompactAsync(new CompactAgentSessionCommand(Summary: "summary"));
        Assert.Equal(sessionId, result.Id);
        Assert.True(result.WasCompacted);
    }

    [Fact]
    public async Task Compact_AfterFollowupTurnTerminalisedByIdle_LeasesCleared()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-lost-followup");
        var accept = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "follow up text",
            Source: "agent-session-followup",
            IdempotencyKey: "followup-1"));
        var persistence = grain.PersistenceCheckpoint(_fixture.Persistence);
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $$"""{"activity":"idle","operationId":"{{accept.OperationId}}","status":"completed"}""") },
            "runtime-lost-followup"));
        await persistence.WaitAsync();
        var result = await grain.CompactAsync(new CompactAgentSessionCommand(Summary: "available"));

        Assert.True(result.WasCompacted);
        Assert.Empty((await _fixture.StateStore.LoadAsync(sessionId))!.Status.PendingFollowups!);
    }

    [Fact]
    public async Task Compact_AfterSessionActivityIdle_IsImmediatelyAvailable()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-closed");
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionActivity, "{\"activity\":\"idle\"}") },
            "runtime-closed"));

        var result = await grain.CompactAsync(new CompactAgentSessionCommand(Summary: "available"));

        Assert.True(result.WasCompacted);
    }

    [Fact]
    public async Task PendingFollowupTurn_ConcurrentAcceptsAreAcceptedAndQueued()
    {
        // Following the D8 reconciliation: an idle follow-up no longer
        // rejects a concurrent follow-up as a conflicting in-progress
        // operation. Two accepts on the same idle session both persist
        // inputs and a queued turn (the second joins the same turn,
        // since neither is yet executing).
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-followup-operations");
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var first = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "first follow up",
            Source: "agent-session-followup",
            IdempotencyKey: "followup-1"));
        var second = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "second follow up",
            Source: "agent-session-followup",
            IdempotencyKey: "followup-2"));

        Assert.NotEqual(first.InputId, second.InputId);
        Assert.False(first.AlreadyAccepted);
        Assert.False(second.AlreadyAccepted);

        // The single queued turn now carries both inputs (the second
        // join rule for a queued follow-up turn).
        Assert.Equal(first.TurnId, second.TurnId);

        var state = Assert.IsType<AgentSession>(await _fixture.StateStore.LoadAsync(sessionId));
        var turn = Assert.Single(state.Status.Turns!);
        Assert.Equal(2, turn.InputIds.Count);
        Assert.Contains(first.InputId, turn.InputIds);
        Assert.Contains(second.InputId, turn.InputIds);

        // The non-terminal follow-up turn still blocks Compact;
        // settling the turn via the idle activity event frees it.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CompactAsync(new CompactAgentSessionCommand(Summary: "summary")));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $$"""{"activity":"idle","operationId":"{{first.OperationId}}","status":"completed"}""") },
            "runtime-followup-operations"));

        var result = await grain.CompactAsync(new CompactAgentSessionCommand(Summary: "available"));
        Assert.True(result.WasCompacted);
    }
}
