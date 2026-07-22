using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class AgentSessionRecoveryGrainSpecs : IClassFixture<AgentSessionGrainFixture>
{
    private readonly AgentSessionGrainFixture _fixture;

    public AgentSessionRecoveryGrainSpecs(AgentSessionGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task Compact_PreservesBindingAndLineageAndRecordsOnlyCompaction()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-before-compact");
        var eventCountBefore = _fixture.StateStore.Events.Count;

        var result = await grain.CompactAsync(new CompactAgentSessionCommand(Summary: "summary"));

        var state = Assert.IsType<AgentSession>(_fixture.StateStore.State);
        Assert.Equal(sessionId, state.Id);
        Assert.Equal("runtime-before-compact", state.Status.AgentRuntimeSessionId);
        Assert.Equal("opencode", state.Runtime.Runtime);
        Assert.Equal(sessionId, result.Id);

        var recoveryEvents = _fixture.StateStore.Events.Skip(eventCountBefore).ToArray();
        Assert.IsType<AgentSessionContextCompacted>(Assert.Single(recoveryEvents).Value);
        Assert.DoesNotContain(recoveryEvents, candidate => candidate.Value is AgentSessionRuntimeBound);
    }

    [Fact]
    public async Task Reset_CurrentExpectedBinding_AppliesReplacementAndWritesContextReset()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-before-reset");
        var eventCountBefore = _fixture.StateStore.Events.Count;

        var result = await grain.ResetAsync(new ResetAgentSessionCommand(
            ExpectedRuntimeSessionId: "runtime-before-reset",
            ReplacementRuntimeSessionId: "runtime-after-reset"));

        var state = Assert.IsType<AgentSession>(_fixture.StateStore.State);
        Assert.Equal(sessionId, state.Id);
        Assert.Equal("runtime-after-reset", state.Status.AgentRuntimeSessionId);
        Assert.Equal("opencode", state.Runtime.Runtime);
        Assert.Equal(sessionId, result.Id);

        var recoveryEvent = Assert.Single(_fixture.StateStore.Events.Skip(eventCountBefore));
        var runtimeBound = Assert.IsType<AgentSessionRuntimeBound>(recoveryEvent.Value);
        Assert.Equal("runtime-after-reset", runtimeBound.AgentRuntimeSessionId);
        var resetTranscript = Assert.Single(_fixture.TranscriptStore.Flushes);
        Assert.Equal("session.context_reset", resetTranscript.Parts.Single().Type);
        using var payload = JsonDocument.Parse(resetTranscript.Parts.Single().PayloadJson);
        Assert.Equal("reset", payload.RootElement.GetProperty("reason").GetString());
        Assert.True(payload.RootElement.GetProperty("observedAt").GetString() is not null);
        Assert.DoesNotContain("runtime-before-reset", resetTranscript.Parts.Single().PayloadJson);
        Assert.DoesNotContain("runtime-after-reset", resetTranscript.Parts.Single().PayloadJson);
        Assert.DoesNotContain(
            _fixture.StateStore.Events.Skip(eventCountBefore),
            candidate => candidate.Value is AgentSessionContextCompacted);
    }

    [Fact]
    public async Task Reset_StaleExpectedBinding_RejectsWithoutMutation()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-original");
        await grain.ResetAsync(new ResetAgentSessionCommand(
            ExpectedRuntimeSessionId: "runtime-original",
            ReplacementRuntimeSessionId: "runtime-current"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        var saveCountBefore = _fixture.StateStore.SaveCount;
        var eventCountBefore = _fixture.StateStore.Events.Count;

        var exception = await Assert.ThrowsAsync<StaleRuntimeSessionBindingException>(() =>
            grain.ResetAsync(new ResetAgentSessionCommand(
                ExpectedRuntimeSessionId: "runtime-original",
                ReplacementRuntimeSessionId: "runtime-must-not-apply")));

        Assert.Equal(sessionId, exception.SessionId);
        Assert.Equal("runtime-original", exception.ExpectedRuntimeSessionId);
        Assert.Equal("runtime-current", exception.ActualRuntimeSessionId);
        Assert.Contains(sessionId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("runtime-current", exception.Message, StringComparison.Ordinal);
        Assert.Equal(saveCountBefore, _fixture.StateStore.SaveCount);
        Assert.Equal(eventCountBefore, _fixture.StateStore.Events.Count);

        var state = Assert.IsType<AgentSession>(_fixture.StateStore.State);
        Assert.Equal("runtime-current", state.Status.AgentRuntimeSessionId);
    }

    [Fact]
    public async Task Reset_ConcurrentBeginsReuseOneReservationAndReturnOneCompletion()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-before-reset");

        var first = await grain.BeginResetAsync();
        var duplicate = await grain.BeginResetAsync();

        Assert.Equal(first.OperationId, duplicate.OperationId);
        Assert.Equal("runtime-before-reset", first.ExpectedRuntimeSessionId);
        Assert.Equal("opencode", first.Runtime);

        var result = await grain.CompleteResetAsync(new CompleteResetAgentSessionCommand(
            first.OperationId!,
            "runtime-after-reset",
            "opencode"));

        Assert.Equal(sessionId, result.Id);
        var duplicateCompletion = await grain.CompleteResetAsync(
            new CompleteResetAgentSessionCommand(first.OperationId!, "unused-replacement", "opencode"));
        Assert.Equal(result, duplicateCompletion);
        Assert.Equal("runtime-after-reset", (await grain.GetAsync())?.AgentSessionId);
    }

    [Fact]
    public async Task CompactAndReset_CompetingReservationsRejectTheSecondOperation()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-before-recovery");

        var compact = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact);
        var exception = await Assert.ThrowsAsync<RecoveryOperationInProgressException>(() => grain.BeginResetAsync());

        Assert.Equal(sessionId, exception.SessionId);
        Assert.Equal("compact", exception.Operation);
        await grain.AbandonResetAsync(compact.OperationId!);
        var reset = await grain.BeginResetAsync();
        Assert.Equal("runtime-before-recovery", reset.ExpectedRuntimeSessionId);
    }

    [Fact]
    public async Task Compact_ConcurrentPreparationReusesThePersistedOperation()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-before-compact");

        var compact = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact);
        var duplicate = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact);

        Assert.Equal(sessionId, compact.SessionId);
        Assert.Equal(compact.OperationId, duplicate.OperationId);

        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(compact.OperationId, Summary: "summary"));
        Assert.Equal(1, _fixture.StateStore.Events.Count(e => e.Value is AgentSessionContextCompacted));
    }

    [Fact]
    public async Task Compact_ReservationSurvivesReactivationAndCompletesOnlyOnce()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-before-compact");
        var reserved = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact);

        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();

        var reactivated = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var retry = await reactivated.PrepareSessionCommandAsync(SessionCommandKind.Compact);
        Assert.Equal(reserved.OperationId, retry.OperationId);

        await reactivated.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(retry.OperationId, Summary: "summary"));
        Assert.Single(_fixture.StateStore.Events, e => e.Value is AgentSessionContextCompacted);
    }

    [Fact]
    public async Task CompletedCompact_DoesNotBlockTheNextReset()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-completed-compact");
        var compact = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact);
        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(compact.OperationId, Summary: "summary"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var reset = await grain.BeginResetAsync();

        Assert.NotEqual(compact.OperationId, reset.OperationId);
        Assert.Equal("runtime-completed-compact", reset.ExpectedRuntimeSessionId);
    }

    [Fact]
    public async Task Compact_PostCommitFailure_ReactivationReturnsPersistedCompletionWithoutAnotherCompaction()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-post-commit");
        var request = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact);
        _fixture.StateStore.CommitThenThrowNext = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.CompleteCompactAsync(
            new CompleteCompactAgentSessionCommand(request.OperationId, Summary: "summary")));

        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();
        var reactivated = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);

        var completed = await reactivated.GetCompletedRecoveryAsync(SessionCommandKind.Compact);
        Assert.NotNull(completed);
        Assert.Equal(sessionId, completed!.Id);
        Assert.True(completed.WasCompacted);
        Assert.Single(_fixture.StateStore.Events, e => e.Value is AgentSessionContextCompacted);

        var replay = await reactivated.CompleteCompactAsync(
            new CompleteCompactAgentSessionCommand(request.OperationId, Summary: "different summary"));
        Assert.Equal(completed, replay);
        Assert.Single(_fixture.StateStore.Events, e => e.Value is AgentSessionContextCompacted);
    }

    [Fact]
    public async Task CompletedCompact_ReplaysOnlyItsIdempotencyKeyAndStartsANewOperationForAnotherKey()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-recovery-key");
        var first = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "compact-1");
        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(first.OperationId, Summary: "first"));

        Assert.NotNull(await grain.GetCompletedRecoveryAsync(SessionCommandKind.Compact, "compact-1"));
        _fixture.TimeProvider.Advance(AgentSessionJsonHelper.ActiveRuntimeEventWindow + TimeSpan.FromSeconds(1));
        var second = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "compact-2");
        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(second.OperationId, Summary: "second"));

        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(2, _fixture.StateStore.Events.Count(e => e.Value is AgentSessionContextCompacted));
    }

    [Fact]
    public async Task PendingReset_AcceptsAnotherIdempotencyKeyAndReplaysCompletionForBothKeys()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-pending-reset-key");
        var first = await grain.BeginResetAsync("reset-1");

        var joined = await grain.BeginResetAsync("reset-2");
        Assert.Equal(first.OperationId, joined.OperationId);

        await grain.CompleteResetAsync(new CompleteResetAgentSessionCommand(
            first.OperationId,
            "runtime-pending-reset-key-replacement",
            "opencode"));

        Assert.NotNull(await grain.GetCompletedRecoveryAsync(SessionCommandKind.Reset, "reset-1"));
        Assert.NotNull(await grain.GetCompletedRecoveryAsync(SessionCommandKind.Reset, "reset-2"));
    }

    [Fact]
    public async Task DelayedAttachAfterReset_CannotRestoreThePreviousRuntimeBinding()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-before-reset");
        await grain.ResetAsync(new ResetAgentSessionCommand(
            ExpectedRuntimeSessionId: "runtime-before-reset",
            ReplacementRuntimeSessionId: "runtime-after-reset"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => grain.AttachPhysicalSessionAsync(
            new AttachPhysicalSessionCommand("runtime-before-reset")));

        Assert.Contains("use Reset", exception.Message, StringComparison.Ordinal);
        Assert.Equal("runtime-after-reset", (await grain.GetAsync())?.AgentSessionId);
    }

    [Fact]
    public async Task CompactAndReset_ActiveSession_ReturnIdenticalConflictWithoutMutation()
    {
        var sessionId = $"recovery-grain-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(OpenCommand());
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-active"));
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionActivity, "{\"activity\":\"active\"}") },
            "runtime-active"));
        var saveCountBefore = _fixture.StateStore.SaveCount;
        var eventCountBefore = _fixture.StateStore.Events.Count;

        var compactException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CompactAsync(new CompactAgentSessionCommand(Summary: "summary")));
        var resetException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.ResetAsync(new ResetAgentSessionCommand(
                ExpectedRuntimeSessionId: "runtime-active",
                ReplacementRuntimeSessionId: "runtime-after-reset")));

        Assert.Equal(compactException.Message, resetException.Message);
        Assert.Contains(sessionId, compactException.Message, StringComparison.Ordinal);
        Assert.Equal(saveCountBefore, _fixture.StateStore.SaveCount);
        Assert.Equal(eventCountBefore, _fixture.StateStore.Events.Count);
        Assert.Equal("runtime-active", _fixture.StateStore.State!.Status.AgentRuntimeSessionId);
    }

    [Fact]
    public async Task Compact_AfterFollowupPromptRejected_ClearsLeaseViaFollowupFailedEvent()
    {
        // An idle follow-up reserves a lease (PendingFollowup) that blocks
        // Compact/Reset until the follow-up turn reaches a terminal outcome.
        // When the runner's prompt rejects, it emits session.followup_failed;
        // the grain must clear the lease so recovery is not permanently blocked
        // as session_active.
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-followup");

        var reservation = await grain.BeginFollowupAsync();
        Assert.NotNull(reservation.OperationId);
        await grain.ConfirmFollowupAsync(reservation.OperationId!);

        // The accepted lease blocks Compact while the follow-up turn is in flight.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.CompactAsync(new CompactAgentSessionCommand(Summary: "summary")));

        // The runner reports the prompt rejection via the established runtime
        // event path on the bound runtime session.
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[]
            {
                new AgentSessionRuntimeEventInput(
                    "session.followup_failed",
                    $$"""{"runtimeSessionId":"runtime-followup","source":"followup","operationId":"{{reservation.OperationId}}","status":"failed"}"""),
            },
            "runtime-followup"));

        // The rejection did not create a turn. Its matching lease is cleared
        // without extending the activity window, so recovery is immediate.
        var state = Assert.IsType<AgentSession>(_fixture.StateStore.State);
        Assert.Null(state.Status.PendingFollowup);
        var now = _fixture.TimeProvider.GetUtcNow().UtcDateTime;
        Assert.Equal("inactive", AgentSessionJsonHelper.StatusName(state, now));
        var result = await grain.CompactAsync(new CompactAgentSessionCommand(Summary: "summary"));
        Assert.Equal(sessionId, result.Id);
        Assert.True(result.WasCompacted);
    }

    [Fact]
    public async Task Compact_AfterAcceptedFollowupIsLost_ExpiresTheLeaseWithoutSynthesizingATerminalEvent()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-lost-followup");
        var followup = await grain.BeginFollowupAsync();
        await grain.ConfirmFollowupAsync(followup.OperationId!);
        _fixture.TimeProvider.Advance(AgentSessionJsonHelper.ActiveRuntimeEventWindow + TimeSpan.FromSeconds(1));

        var result = await grain.CompactAsync(new CompactAgentSessionCommand(Summary: "available"));

        Assert.True(result.WasCompacted);
        Assert.Empty(_fixture.StateStore.State!.Status.PendingFollowups!);
    }

    [Fact]
    public async Task Compact_AfterSessionClosed_IsImmediatelyAvailable()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-closed");
        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionClosed, "{\"status\":\"completed\"}") },
            "runtime-closed"));

        var result = await grain.CompactAsync(new CompactAgentSessionCommand(Summary: "available"));

        Assert.True(result.WasCompacted);
    }

    [Fact]
    public async Task PendingIdleFollowup_RejectsDuplicateDeliveryUntilItsMatchingFailureArrives()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-followup-operations");
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        var first = await grain.BeginFollowupAsync();
        await Assert.ThrowsAsync<FollowupOperationInProgressException>(() => grain.BeginFollowupAsync());
        await grain.ConfirmFollowupAsync(first.OperationId!);
        var second = await grain.BeginFollowupAsync();
        await grain.ConfirmFollowupAsync(second.OperationId!);

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionFollowupFailed,
                $$"""{"operationId":"{{first.OperationId}}","status":"failed","failureReason":"first failed"}""") },
            "runtime-followup-operations"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.CompactAsync(new CompactAgentSessionCommand(Summary: "still running")));

        await grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionFollowupCompleted,
                $$"""{"operationId":"{{second.OperationId}}","status":"completed"}""") },
            "runtime-followup-operations"));

        var result = await grain.CompactAsync(new CompactAgentSessionCommand(Summary: "available"));
        Assert.True(result.WasCompacted);
        Assert.Equal(2, _fixture.TranscriptStore.Flushes.Count(flush =>
            flush.Parts.Any(part => part.Type == TranscriptPartTypes.Status)));
    }

    private async Task<(IAgentSessionGrain Grain, string SessionId)> CreateAttachedSessionAsync(string runtimeSessionId)
    {
        var sessionId = $"recovery-grain-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(OpenCommand());
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));
        return (grain, sessionId);
    }

    private static OpenAgentSessionCommand OpenCommand() => new(
        "runner-1",
        "opencode",
        WorkDir: "/work",
        Metadata: new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "build"));
}
