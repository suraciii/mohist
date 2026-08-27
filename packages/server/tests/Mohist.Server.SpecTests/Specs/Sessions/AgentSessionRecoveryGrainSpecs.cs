using System.Text.Json;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
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

        var state = Assert.IsType<AgentSession>(await _fixture.StateStore.LoadAsync(sessionId));
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

        var state = Assert.IsType<AgentSession>(await _fixture.StateStore.LoadAsync(sessionId));
        Assert.Equal(sessionId, state.Id);
        Assert.Equal("runtime-after-reset", state.Status.AgentRuntimeSessionId);
        Assert.Equal("opencode", state.Runtime.Runtime);
        Assert.Equal(sessionId, result.Id);

        var recoveryEvent = Assert.Single(_fixture.StateStore.Events.Skip(eventCountBefore));
        var runtimeBound = Assert.IsType<AgentSessionRuntimeBound>(recoveryEvent.Value);
        Assert.Equal("runtime-after-reset", runtimeBound.AgentRuntimeSessionId);
        var resetTranscript = Assert.Single(
            _fixture.TranscriptStore.Flushes,
            flush => flush.Turn.SessionId == sessionId);
        Assert.Equal("session.context_reset", resetTranscript.Parts.Single().Type);
        Assert.Equal("runtime-after-reset", resetTranscript.Turn.RuntimeSessionId);
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

        var state = Assert.IsType<AgentSession>(await _fixture.StateStore.LoadAsync(sessionId));
        Assert.Equal("runtime-current", state.Status.AgentRuntimeSessionId);
    }

    [Fact]
    public async Task MissingRecovery_RebindsWithFullCasAndWritesContextReset()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-missing");

        var recovered = await grain.RecoverMissingRuntimeSessionAsync(new RecoverMissingRuntimeSessionCommand(
            "runner-1", "opencode", "runtime-missing", "runtime-replacement"));

        Assert.Equal("runtime-replacement", recovered.AgentSessionId);
        var transcript = Assert.Single(_fixture.TranscriptStore.Flushes, flush =>
            flush.Parts.Any(part => part.PayloadJson.Contains("missing-recovery", StringComparison.Ordinal)));
        using var payload = JsonDocument.Parse(transcript.Parts.Single(part =>
            part.PayloadJson.Contains("missing-recovery", StringComparison.Ordinal)).PayloadJson);
        Assert.Equal("missing-recovery", payload.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain("runtime-missing", transcript.Parts.Single().PayloadJson);
        Assert.DoesNotContain("runtime-replacement", transcript.Parts.Single().PayloadJson);
    }

    [Fact]
    public async Task MissingRecovery_StaleExpectedBindingRejectsCandidate()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-current");

        await Assert.ThrowsAsync<StaleRuntimeSessionBindingException>(() =>
            grain.RecoverMissingRuntimeSessionAsync(new RecoverMissingRuntimeSessionCommand(
                "runner-1", "opencode", "runtime-stale", "runtime-candidate")));

        Assert.Equal("runtime-current", (await grain.GetAsync())?.AgentSessionId);
    }

    [Fact]
    public async Task Reset_ConcurrentBeginsReuseOneReservationAndReturnOneCompletion()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-before-reset");

        var first = await grain.BeginResetAsync("test-generation");
        var duplicate = await grain.BeginResetAsync("test-generation");

        Assert.Equal(first.OperationId, duplicate.OperationId);
        Assert.Equal("runtime-before-reset", first.ExpectedRuntimeSessionId);
        Assert.Equal("opencode", first.Runtime);
        Assert.Equal(SessionCommandAdmissionOutcome.AdmittedNow,
            await grain.AdmitSessionCommandEffectAsync(first.OperationId!, "test-generation"));

        var result = await grain.CompleteResetAsync(new CompleteResetAgentSessionCommand(
            first.OperationId!,
            "runtime-after-reset",
            "opencode",
            "test-generation"));

        Assert.Equal(sessionId, result.Id);
        var duplicateCompletion = await grain.CompleteResetAsync(
            new CompleteResetAgentSessionCommand(first.OperationId!, "unused-replacement", "opencode", "test-generation"));
        Assert.Equal(result, duplicateCompletion);
        Assert.Equal("runtime-after-reset", (await grain.GetAsync())?.AgentSessionId);
    }

    [Fact]
    public async Task CompactAndReset_CompetingReservationsRejectTheSecondOperation()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-before-recovery");

        var compact = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation");
        var exception = await Assert.ThrowsAsync<RecoveryOperationInProgressException>(() => grain.BeginResetAsync("test-generation"));

        Assert.Equal(sessionId, exception.SessionId);
        Assert.Equal("compact", exception.Operation);
        await grain.AbandonResetAsync(compact.OperationId!);
        var reset = await grain.BeginResetAsync("test-generation");
        Assert.Equal("runtime-before-recovery", reset.ExpectedRuntimeSessionId);
    }

    [Fact]
    public async Task Compact_ConcurrentPreparationReusesThePersistedOperation()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-before-compact");

        var compact = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation");
        var duplicate = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation");

        Assert.Equal(sessionId, compact.SessionId);
        Assert.Equal(compact.OperationId, duplicate.OperationId);
        Assert.Equal(SessionCommandAdmissionOutcome.AdmittedNow,
            await grain.AdmitSessionCommandEffectAsync(compact.OperationId, "test-generation"));

        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(compact.OperationId, "test-generation", Summary: "summary"));
        Assert.Equal(1, _fixture.StateStore.Events.Count(e => e.Value is AgentSessionContextCompacted));
    }

    [Theory]
    [InlineData(SessionCommandKind.Compact)]
    [InlineData(SessionCommandKind.Reset)]
    public async Task CompletionWithoutEffectAdmissionIsRejected(SessionCommandKind command)
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-without-admission");
        var request = await grain.PrepareSessionCommandAsync(command, "test-generation");
        var effectsBefore = _fixture.StateStore.Events.Count(e =>
            e.Value is AgentSessionContextCompacted or AgentSessionRuntimeBound);

        await Assert.ThrowsAsync<StaleRuntimeSessionBindingException>(() => command == SessionCommandKind.Compact
            ? grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(
                request.OperationId,
                "test-generation",
                Summary: "must not apply"))
            : grain.CompleteResetAsync(new CompleteResetAgentSessionCommand(
                request.OperationId,
                "replacement-runtime",
                "opencode",
                "test-generation")));

        Assert.Equal(effectsBefore, _fixture.StateStore.Events.Count(e =>
            e.Value is AgentSessionContextCompacted or AgentSessionRuntimeBound));
    }

    [Fact]
    public async Task CompletionWithMismatchedAdmissionGenerationIsRejected()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-admission-generation");
        var request = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "generation-a");
        var compactionsBefore = _fixture.StateStore.Events.Count(e => e.Value is AgentSessionContextCompacted);

        Assert.Equal(SessionCommandAdmissionOutcome.Missing,
            await grain.AdmitSessionCommandEffectAsync(request.OperationId, "generation-b"));
        await Assert.ThrowsAsync<StaleRuntimeSessionBindingException>(() => grain.CompleteCompactAsync(
            new CompleteCompactAgentSessionCommand(request.OperationId, "generation-a", Summary: "must not apply")));

        Assert.Equal(compactionsBefore,
            _fixture.StateStore.Events.Count(e => e.Value is AgentSessionContextCompacted));
    }

    [Fact]
    public async Task Compact_AdmittedEffectSurvivesReactivationAndCannotBeAdmittedAgain()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-before-compact");
        var reserved = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation", "reactivation-operation");
        Assert.Equal(
            SessionCommandAdmissionOutcome.AdmittedNow,
            await grain.AdmitSessionCommandEffectAsync(reserved.OperationId, "test-generation"));

        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();

        var reactivated = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        var retry = await reactivated.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation", "reactivation-operation");
        Assert.Equal(reserved.OperationId, retry.OperationId);
        Assert.Equal(
            SessionCommandAdmissionOutcome.AlreadyAdmitted,
            await reactivated.AdmitSessionCommandEffectAsync(retry.OperationId, "test-generation"));

        await reactivated.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(retry.OperationId, "test-generation", Summary: "summary"));
        Assert.Single(_fixture.StateStore.Events, e => e.Value is AgentSessionContextCompacted);
    }

    [Fact]
    public async Task CompletedCompact_DoesNotBlockTheNextReset()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-completed-compact");
        var compact = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation");
        await grain.AdmitSessionCommandEffectAsync(compact.OperationId, "test-generation");
        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(compact.OperationId, "test-generation", Summary: "summary"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var reset = await grain.BeginResetAsync("test-generation");

        Assert.NotEqual(compact.OperationId, reset.OperationId);
        Assert.Equal("runtime-completed-compact", reset.ExpectedRuntimeSessionId);
    }

    [Fact]
    public async Task Compact_PostCommitFailure_ReactivationReturnsPersistedCompletionWithoutAnotherCompaction()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-post-commit");
        var request = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation", "post-commit-key");
        await grain.AdmitSessionCommandEffectAsync(request.OperationId, "test-generation");
        _fixture.StateStore.CommitThenThrowNextSave(sessionId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.CompleteCompactAsync(
            new CompleteCompactAgentSessionCommand(request.OperationId, "test-generation", Summary: "summary")));

        var management = grain.AsReference<IGrainManagementExtension>();
        await management.DeactivateOnIdle();
        var reactivated = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);

        var completed = await reactivated.GetCompletedRecoveryAsync(SessionCommandKind.Compact, "post-commit-key");
        Assert.NotNull(completed);
        Assert.Equal(sessionId, completed!.Id);
        Assert.True(completed.WasCompacted);
        Assert.Single(_fixture.StateStore.Events, e => e.Value is AgentSessionContextCompacted);

        var replay = await reactivated.CompleteCompactAsync(
            new CompleteCompactAgentSessionCommand(request.OperationId, "test-generation", Summary: "different summary"));
        Assert.Equal(completed, replay);
        Assert.Single(_fixture.StateStore.Events, e => e.Value is AgentSessionContextCompacted);
    }

    [Fact]
    public async Task CompletedCompact_ReplaysOnlyItsIdempotencyKeyAndStartsANewOperationForAnotherKey()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-recovery-key");
        var first = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation", "compact-1");
        await grain.AdmitSessionCommandEffectAsync(first.OperationId, "test-generation");
        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(first.OperationId, "test-generation", Summary: "first"));

        Assert.NotNull(await grain.GetCompletedRecoveryAsync(SessionCommandKind.Compact, "compact-1"));
        var second = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation", "compact-2");
        await grain.AdmitSessionCommandEffectAsync(second.OperationId, "test-generation");
        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(second.OperationId, "test-generation", Summary: "second"));

        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(2, _fixture.StateStore.Events.Count(e => e.Value is AgentSessionContextCompacted));
    }

    [Fact]
    public async Task CompletedCommands_RetainEachAdmittedOutcomeForExactReplay()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-admission-history");
        var first = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation", "compact-a");
        await grain.AdmitSessionCommandEffectAsync(first.OperationId, "test-generation");
        var firstOutcome = await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(
            first.OperationId,
            "test-generation",
            Summary: "first"));

        var second = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation", "compact-b");
        await grain.AdmitSessionCommandEffectAsync(second.OperationId, "test-generation");
        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(
            second.OperationId,
            "test-generation",
            Summary: "second"));

        Assert.Equal(firstOutcome, await grain.GetCompletedRecoveryAsync(SessionCommandKind.Compact, "compact-a"));
        Assert.Equal(first.OperationId, (await grain.PrepareSessionCommandAsync(
            SessionCommandKind.Compact,
            "test-generation",
            "compact-a")).OperationId);
    }

    [Fact]
    public async Task ChangedProcessGenerationClearsStalePendingButFencesOldCompletion()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-generation-replacement");
        var old = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "generation-a", "old-key");
        await grain.AdmitSessionCommandEffectAsync(old.OperationId, "generation-a");

        var replacement = await grain.PrepareSessionCommandAsync(
            SessionCommandKind.Compact,
            "generation-b",
            "new-key");

        Assert.NotEqual(old.OperationId, replacement.OperationId);
        await Assert.ThrowsAsync<StaleRuntimeSessionBindingException>(() => grain.CompleteCompactAsync(
            new CompleteCompactAgentSessionCommand(old.OperationId, "generation-a", Summary: "old")));
        Assert.Null(await grain.GetCompletedRecoveryAsync(SessionCommandKind.Compact, "old-key"));
        Assert.Equal(old.OperationId, (await grain.PrepareSessionCommandAsync(
            SessionCommandKind.Compact,
            "generation-b",
            "old-key")).OperationId);
    }

    [Fact]
    public async Task PendingReset_RejectsDifferentIdempotencyKeyWithoutChangingReservation()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-pending-reset-key");
        var first = await grain.BeginResetAsync("test-generation", "reset-1");

        await Assert.ThrowsAsync<RecoveryOperationInProgressException>(() => grain.BeginResetAsync("test-generation", "reset-2"));

        var state = Assert.IsType<AgentSession>(await _fixture.StateStore.LoadAsync(sessionId));
        Assert.Equal(first.OperationId, state.Status.PendingReset?.OperationId);
        Assert.Equal("reset-1", state.Status.PendingReset?.IdempotencyKey);
        Assert.Null(state.Status.PendingReset?.AdditionalIdempotencyKeys);
    }

    [Fact]
    public async Task DefaultKey_GeneratesUniqueIdempotencyKeyForEachOmittedCall()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-default-key");
        var first = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation");
        var firstKey = (await _fixture.StateStore.LoadAsync(sessionId))!
            .Status.PendingReset!.IdempotencyKey;
        await grain.AdmitSessionCommandEffectAsync(first.OperationId, "test-generation");

        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(first.OperationId, "test-generation", Summary: "first"));
        var second = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation");
        var secondKey = (await _fixture.StateStore.LoadAsync(sessionId))!
            .Status.PendingReset!.IdempotencyKey;

        Assert.NotNull(firstKey);
        Assert.NotNull(secondKey);
        Assert.NotEqual("legacy", firstKey);
        Assert.NotEqual("legacy", secondKey);
        Assert.NotEqual(firstKey, secondKey);
    }

    [Fact]
    public async Task DefaultKey_AfterCompletedRecovery_StartsNewOperationInsteadOfReplaying()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-default-no-replay");
        var first = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation");
        await grain.AdmitSessionCommandEffectAsync(first.OperationId, "test-generation");
        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(first.OperationId, "test-generation", Summary: "first"));

        Assert.Null(await grain.GetCompletedRecoveryAsync(SessionCommandKind.Compact));

        var second = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation");
        Assert.NotEqual(first.OperationId, second.OperationId);
    }

    [Fact]
    public async Task DefaultKey_ProducesItsOwnRecoveryEffectForEachOmittedCall()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-default-reset-effect");
        var eventCountBefore = _fixture.StateStore.Events.Count;
        var first = await grain.BeginResetAsync("test-generation");
        await grain.AdmitSessionCommandEffectAsync(first.OperationId!, "test-generation");
        await grain.CompleteResetAsync(new CompleteResetAgentSessionCommand(
            first.OperationId!,
            "runtime-default-reset-effect-replacement-1",
            "opencode",
            "test-generation"));

        var second = await grain.BeginResetAsync("test-generation");
        await grain.AdmitSessionCommandEffectAsync(second.OperationId!, "test-generation");
        await grain.CompleteResetAsync(new CompleteResetAgentSessionCommand(
            second.OperationId!,
            "runtime-default-reset-effect-replacement-2",
            "opencode",
            "test-generation"));

        var recoveryEvents = _fixture.StateStore.Events.Skip(eventCountBefore).ToArray();
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(2, recoveryEvents.Count(e => e.Value is AgentSessionRuntimeBound));
    }

    [Fact]
    public async Task ExplicitLegacyKey_IsTreatedAsOrdinaryCallerSuppliedKey()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-explicit-legacy");
        var first = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation", "legacy");
        await grain.AdmitSessionCommandEffectAsync(first.OperationId, "test-generation");
        var firstKey = (await _fixture.StateStore.LoadAsync(sessionId))!
            .Status.PendingReset!.IdempotencyKey;
        Assert.Equal("legacy", firstKey);
        await grain.AdmitSessionCommandEffectAsync(first.OperationId, "test-generation");

        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(first.OperationId, "test-generation", Summary: "first"));

        Assert.NotNull(await grain.GetCompletedRecoveryAsync(SessionCommandKind.Compact, "legacy"));
        Assert.Null(await grain.GetCompletedRecoveryAsync(SessionCommandKind.Compact));

        var second = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation");
        var secondKey = (await _fixture.StateStore.LoadAsync(sessionId))!
            .Status.PendingReset!.IdempotencyKey;
        Assert.NotEqual("legacy", secondKey);
        Assert.NotEqual(firstKey, secondKey);
        Assert.NotEqual(first.OperationId, second.OperationId);
    }

    [Fact]
    public async Task DelayedAttachAfterReset_CannotRestoreThePreviousRuntimeBinding()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-before-reset");
        await grain.ResetAsync(new ResetAgentSessionCommand(
            ExpectedRuntimeSessionId: "runtime-before-reset",
            ReplacementRuntimeSessionId: "runtime-after-reset"));

        var exception = await Assert.ThrowsAsync<StaleRuntimeSessionBindingException>(() => grain.AttachPhysicalSessionAsync(
            new AttachPhysicalSessionCommand(
                "runtime-before-reset",
                ExpectedRuntime: "opencode",
                ExpectedAgentSessionId: "runtime-before-reset",
                ExpectedRunnerId: "runner-1")));

        Assert.Contains("expected runtime session", exception.Message, StringComparison.Ordinal);
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
        Assert.Equal("runtime-active", (await _fixture.StateStore.LoadAsync(sessionId))!.Status.AgentRuntimeSessionId);
    }

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
            .WithLabel("mohist.io/source-kind", "agent-launch")
            .WithLabel("mohist.io/agent-id", "agent-1")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "build"));
}
