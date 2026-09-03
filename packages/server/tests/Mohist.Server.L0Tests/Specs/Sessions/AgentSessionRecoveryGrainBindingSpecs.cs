using System.Text.Json;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.L0Tests.Specs.Sessions;

[Collection("AgentSessionGrainL0")]
public sealed partial class AgentSessionRecoveryGrainSpecs
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
    public async Task MissingRecovery_SealedQueuedFollowup_RebindsBeforeInputSubmission()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-missing-before-followup");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "continue after recovery",
            Source: "agent-session-followup",
            IdempotencyKey: "recover-queued-followup"));
        var dispatch = await grain.BeginNextFollowupDispatchAsync();
        Assert.Equal(accepted.TurnId, dispatch?.TurnId);
        var beforeRecovery = Assert.IsType<AgentSession>(await _fixture.StateStore.LoadAsync(sessionId));
        var pending = Assert.Single(beforeRecovery.Status.PendingFollowups!);
        Assert.Equal(accepted.TurnId, pending.TurnId);
        Assert.True(pending.Dispatching);
        Assert.True(pending.PayloadSealed);
        Assert.Equal(AgentSessionActivity.Idle, beforeRecovery.Status.Activity);

        var recovered = await grain.RecoverMissingRuntimeSessionAsync(new RecoverMissingRuntimeSessionCommand(
            "runner-1",
            "opencode",
            "runtime-missing-before-followup",
            "runtime-replacement-before-followup",
            accepted.TurnId));

        Assert.Equal("runtime-replacement-before-followup", recovered.AgentSessionId);
        Assert.Equal(AgentTurnStatus.Queued, (await grain.ListTurnsAsync()).Single().Status);
    }

    [Fact]
    public async Task MissingRecovery_ExecutingFollowup_RejectsWithoutChangingBinding()
    {
        var (grain, _) = await CreateAttachedSessionAsync("runtime-active-followup");
        var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "already submitted",
            Source: "agent-session-followup",
            IdempotencyKey: "active-followup"));
        var dispatch = await grain.BeginNextFollowupDispatchAsync();
        await grain.MarkFollowupTurnExecutingAsync(dispatch!.OperationId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            grain.RecoverMissingRuntimeSessionAsync(new RecoverMissingRuntimeSessionCommand(
                "runner-1",
                "opencode",
                "runtime-active-followup",
                "runtime-must-not-apply",
                accepted.TurnId)));

        Assert.Equal("runtime-active-followup", (await grain.GetAsync())?.AgentSessionId);
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
