using System.Text.Json;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

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

    [Theory]
    [InlineData(SessionCommandKind.Compact)]
    [InlineData(SessionCommandKind.Reset)]
    public async Task OmittedKey_IsRejectedWithoutReservingOrChangingState(SessionCommandKind command)
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync($"runtime-omitted-{command.ToString().ToLowerInvariant()}");
        var eventsBefore = _fixture.StateStore.Events.Count;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.PrepareSessionCommandAsync(command, "test-generation", " "));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            grain.GetCompletedRecoveryAsync(command, ""));

        var state = Assert.IsType<AgentSession>(await _fixture.StateStore.LoadAsync(sessionId));
        Assert.Null(state.Status.PendingReset);
        Assert.Equal(eventsBefore, _fixture.StateStore.Events.Count);
    }

    [Fact]
    public async Task CallerSuppliedKey_NamesTheOperationAndADifferentKeyStartsANewIntent()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-explicit-legacy");
        var first = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation", "legacy");
        await grain.AdmitSessionCommandEffectAsync(first.OperationId, "test-generation");
        var firstKey = (await _fixture.StateStore.LoadAsync(sessionId))!
            .Status.PendingReset!.IdempotencyKey;
        Assert.Equal("legacy", firstKey);

        await grain.CompleteCompactAsync(new CompleteCompactAgentSessionCommand(first.OperationId, "test-generation", Summary: "first"));

        Assert.NotNull(await grain.GetCompletedRecoveryAsync(SessionCommandKind.Compact, "legacy"));

        var second = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact, "test-generation", "second-key");
        var secondKey = (await _fixture.StateStore.LoadAsync(sessionId))!
            .Status.PendingReset!.IdempotencyKey;
        Assert.Equal("second-key", secondKey);
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
