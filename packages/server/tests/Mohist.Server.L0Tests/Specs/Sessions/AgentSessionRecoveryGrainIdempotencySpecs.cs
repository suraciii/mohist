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
}
