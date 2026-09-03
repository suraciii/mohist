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
}
