using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class AgentSessionReconnectRecoveryGrainSpecs : IClassFixture<AgentSessionGrainFixture>
{
    private readonly AgentSessionGrainFixture _fixture;

    public AgentSessionReconnectRecoveryGrainSpecs(AgentSessionGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task ReconcileMissingBinding_UnknownSession_SettlesIdleAndRebindsAtomically()
    {
        var grain = await CreateAttachedSessionAsync("runtime-missing-on-reconnect");
        await RecordUnknownUsageAsync(grain, "runtime-missing-on-reconnect");
        var saveCountBefore = _fixture.StateStore.SaveCount;

        var reconciled = await grain.ReconcileMissingBindingAsync(new ReconcileMissingBindingCommand(
            "runner-1", "opencode", "runtime-missing-on-reconnect", "runtime-replacement"));

        Assert.True(_fixture.StateStore.SaveCount > saveCountBefore);
        Assert.Equal("runtime-replacement", reconciled.AgentSessionId);
        Assert.Equal("idle", reconciled.Status);
        Assert.Equal(10, reconciled.InputTokens);
        Assert.Null(reconciled.ContextWindowUsed);
        Assert.Null(reconciled.ContextWindowSize);
        Assert.Single(_fixture.StateStore.Events, candidate => candidate.Value is AgentSessionRuntimeBound bound
            && bound.AgentRuntimeSessionId == "runtime-replacement");
    }

    [Fact]
    public async Task ReconcileMissingBinding_StaleExpectedBinding_PreservesStateTranscriptAndUsage()
    {
        var grain = await CreateAttachedSessionAsync("runtime-current-reconnect");
        await RecordUnknownUsageAsync(grain, "runtime-current-reconnect");
        var before = await grain.GetAsync();
        var saveCountBefore = _fixture.StateStore.SaveCount;
        var eventCountBefore = _fixture.StateStore.Events.Count;
        var transcriptCountBefore = _fixture.TranscriptStore.Flushes.Count;

        await Assert.ThrowsAsync<StaleRuntimeSessionBindingException>(() =>
            grain.ReconcileMissingBindingAsync(new ReconcileMissingBindingCommand(
                "runner-1", "opencode", "runtime-stale-reconnect", "runtime-candidate")));

        Assert.Equal(before, await grain.GetAsync());
        Assert.Equal(saveCountBefore, _fixture.StateStore.SaveCount);
        Assert.Equal(eventCountBefore, _fixture.StateStore.Events.Count);
        Assert.Equal(transcriptCountBefore, _fixture.TranscriptStore.Flushes.Count);
    }

    private async Task<IAgentSessionGrain> CreateAttachedSessionAsync(string runtimeSessionId)
    {
        var sessionId = $"reconnect-recovery-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            "runner-1",
            "opencode",
            WorkDir: "/work",
            Metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", "project-1")
                .WithLabel("mohist.io/source-kind", "workflow")
                .WithLabel("mohist.io/source-id", "workflow-1")
                .WithLabel("mohist.io/session-name", "build")));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(runtimeSessionId));
        return grain;
    }

    private static Task RecordUnknownUsageAsync(IAgentSessionGrain grain, string runtimeSessionId) =>
        grain.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[]
            {
                new AgentSessionRuntimeEventInput(RuntimeEventTypes.UsageUpdated, "{\"inputTokens\":10,\"contextWindowUsed\":100,\"contextWindowSize\":200}"),
                new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionActivity, "{\"activity\":\"unknown\"}")
            },
            runtimeSessionId));
}
