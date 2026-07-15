using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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
        var lineage = Assert.Single(state.Status.RuntimeSessionLineage!);
        Assert.Equal("runtime-before-compact", lineage.AgentRuntimeSessionId);
        Assert.Equal("opencode", lineage.Runtime);
        Assert.Equal(sessionId, result.Id);

        var recoveryEvents = _fixture.StateStore.Events.Skip(eventCountBefore).ToArray();
        Assert.IsType<AgentSessionContextCompacted>(Assert.Single(recoveryEvents).Value);
        Assert.DoesNotContain(recoveryEvents, candidate => candidate.Value is AgentSessionRuntimeBound);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Reset_CurrentExpectedBinding_AppliesReplacementAndAppendsLineage()
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
        Assert.Collection(
            state.Status.RuntimeSessionLineage!,
            entry => Assert.Equal("runtime-before-reset", entry.AgentRuntimeSessionId),
            entry => Assert.Equal("runtime-after-reset", entry.AgentRuntimeSessionId));
        Assert.Equal(sessionId, result.Id);

        var recoveryEvent = Assert.Single(_fixture.StateStore.Events.Skip(eventCountBefore));
        var runtimeBound = Assert.IsType<AgentSessionRuntimeBound>(recoveryEvent.Value);
        Assert.Equal("runtime-after-reset", runtimeBound.AgentRuntimeSessionId);
        Assert.Equal("runtime-before-reset", runtimeBound.PreviousAgentRuntimeSessionId);
        Assert.DoesNotContain(
            _fixture.StateStore.Events.Skip(eventCountBefore),
            candidate => candidate.Value is AgentSessionContextCompacted);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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
        Assert.Collection(
            state.Status.RuntimeSessionLineage!,
            entry => Assert.Equal("runtime-original", entry.AgentRuntimeSessionId),
            entry => Assert.Equal("runtime-current", entry.AgentRuntimeSessionId));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Reset_ConcurrentBeginsReuseOneReservationAndPermitOneCompletion()
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
        await Assert.ThrowsAsync<StaleRuntimeSessionBindingException>(() => grain.CompleteResetAsync(
            new CompleteResetAgentSessionCommand(first.OperationId!, "unused-replacement", "opencode")));
        Assert.Equal("runtime-after-reset", (await grain.GetAsync())?.AgentSessionId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Compact_ConcurrentPreparationRejectsTheSecondOperation()
    {
        var (grain, sessionId) = await CreateAttachedSessionAsync("runtime-before-compact");

        var compact = await grain.PrepareSessionCommandAsync(SessionCommandKind.Compact);
        var exception = await Assert.ThrowsAsync<RecoveryOperationInProgressException>(() =>
            grain.PrepareSessionCommandAsync(SessionCommandKind.Compact));

        Assert.Equal(sessionId, exception.SessionId);
        Assert.Equal("compact", exception.Operation);
        await grain.AbandonResetAsync(compact.OperationId!);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task CompactAndReset_ActiveSession_ReturnIdenticalConflictWithoutMutation()
    {
        var sessionId = $"recovery-grain-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(OpenCommand());
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-active"));
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
        Assert.Equal("runtime-active", Assert.Single(_fixture.StateStore.State.Status.RuntimeSessionLineage!).AgentRuntimeSessionId);
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
