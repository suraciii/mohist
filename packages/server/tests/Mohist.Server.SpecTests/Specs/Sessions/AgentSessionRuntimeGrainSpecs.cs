using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

public sealed class AgentSessionRuntimeGrainSpecs : IClassFixture<AgentSessionGrainFixture>
{
    private readonly AgentSessionGrainFixture _fixture;

    public AgentSessionRuntimeGrainSpecs(AgentSessionGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task OpenAndAttach_StampRuntimeOnBindingAndLineage()
    {
        var grain = NewGrain();

        await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "opencode", WorkDir: "/work"));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-session-1"));

        Assert.Equal("opencode", _fixture.StateStore.State!.Runtime.Runtime);
        Assert.Equal("opencode", Assert.Single(_fixture.StateStore.State.Status.RuntimeSessionLineage!).Runtime);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Open_ExistingLegacySessionDoesNotBackfillRuntime()
    {
        var sessionId = $"runtime-grain-{Guid.NewGuid():N}";
        var legacy = AgentSession.Create(
            sessionId,
            runnerId: string.Empty,
            workDir: "/work",
            now: _fixture.TimeProvider.GetUtcNow().UtcDateTime,
            runtime: null);
        await _fixture.StateStore.SaveAsync(sessionId, legacy);

        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .OpenAsync(new OpenAgentSessionCommand("runner-1", "opencode"));

        Assert.Null(_fixture.StateStore.State!.Runtime.Runtime);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Compact_MissingBindingThrowsRuntimeSessionMissing()
    {
        var grain = NewGrain();
        var opened = await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "opencode"));

        var exception = await Assert.ThrowsAsync<RuntimeSessionMissingException>(() =>
            grain.CompactAsync(new CompactAgentSessionCommand("replacement-session")));

        Assert.Equal(opened.Id, exception.SessionId);
        Assert.Contains(opened.Id, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Reset", exception.Message, StringComparison.Ordinal);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task Reset_UnregisteredRuntimeThrowsBeforeActiveConflict()
    {
        var grain = NewGrain();
        var opened = await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "acp"));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("legacy-runtime-session"));

        var exception = await Assert.ThrowsAsync<RuntimeSessionMissingException>(() =>
            grain.ResetAsync(new ResetAgentSessionCommand("replacement-session")));

        Assert.Equal(opened.Id, exception.SessionId);
        Assert.Equal("legacy-runtime-session", exception.RuntimeSessionId);
        Assert.Equal("acp", exception.Runtime);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task RuntimeBinding_RemainsAvailableAfterReactivation()
    {
        var grain = NewGrain();
        await grain.OpenAsync(new OpenAgentSessionCommand("runner-1", "opencode", WorkDir: "/work"));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-session-1"));

        await grain.DeactivateForTestAsync();
        await grain.EnsureRuntimeSessionPresentAsync();

        Assert.Equal("opencode", _fixture.StateStore.State!.Runtime.Runtime);
        Assert.Equal("runtime-session-1", _fixture.StateStore.State.Status.AgentRuntimeSessionId);
        Assert.Equal("runner-1", _fixture.StateStore.State.Runtime.RunnerId);
        Assert.Equal("/work", _fixture.StateStore.State.Runtime.WorkDir);
    }

    private IAgentSessionGrain NewGrain() =>
        _fixture.Grains.GetGrain<IAgentSessionGrain>($"runtime-grain-{Guid.NewGuid():N}");
}
