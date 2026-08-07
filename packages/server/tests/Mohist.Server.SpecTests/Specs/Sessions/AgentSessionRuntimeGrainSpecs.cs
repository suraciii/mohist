using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.TestSupport;
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

    [Fact]
    public async Task OpenAndAttach_StampRuntimeOnBindingAndLineage()
    {
        var grain = NewGrain();

        await grain.OpenAsync(OpenCommand());
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-session-1"));

        Assert.Equal("opencode", _fixture.StateStore.State!.Runtime.Runtime);
    }

    [Fact]
    public async Task Open_ExistingLegacySessionDoesNotBackfillRuntime()
    {
        var sessionId = $"runtime-grain-{Guid.NewGuid():N}";
        var legacy = AgentSession.Create(
            sessionId,
            runnerId: string.Empty,
            workDir: "/work",
            metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", "project-1")
                .WithLabel("mohist.io/source-kind", "workflow")
                .WithLabel("mohist.io/source-id", "workflow-1")
                .WithLabel("mohist.io/session-name", "build"),
            now: _fixture.TimeProvider.GetUtcNow().UtcDateTime,
            runtime: null);
        await _fixture.StateStore.SaveAsync(sessionId, legacy);

        await _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId)
            .OpenAsync(new OpenAgentSessionCommand("runner-1", "opencode"));

        Assert.Null(_fixture.StateStore.State!.Runtime.Runtime);
    }

    [Fact]
    public async Task Compact_MissingBindingThrowsRuntimeSessionMissing()
    {
        var grain = NewGrain();
        var opened = await grain.OpenAsync(OpenCommand());

        var exception = await Assert.ThrowsAsync<RuntimeSessionMissingException>(() =>
            grain.CompactAsync(new CompactAgentSessionCommand()));

        Assert.Equal(opened.Id, exception.SessionId);
        Assert.Contains(opened.Id, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Reset", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_UnregisteredRuntimeEstablishesReplacementBinding()
    {
        var grain = NewGrain();
        var opened = await grain.OpenAsync(OpenCommand("acp"));
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("legacy-runtime-session"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        var result = await grain.ResetAsync(new ResetAgentSessionCommand(
            ExpectedRuntimeSessionId: "legacy-runtime-session",
            ReplacementRuntimeSessionId: "replacement-session"));

        Assert.Equal(opened.Id, result.Id);
        var rebound = await grain.GetAsync();
        Assert.Equal("replacement-session", rebound?.AgentSessionId);
        Assert.Equal("opencode", rebound?.Runtime);
    }

    [Fact]
    public async Task RuntimeBinding_RemainsAvailableAfterReactivation()
    {
        var grain = NewGrain();
        await grain.OpenAsync(OpenCommand());
        await grain.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-session-1"));

        await TestLifecycle.Deactivate(grain);
        await grain.EnsureRuntimeSessionPresentAsync();

        Assert.Equal("opencode", _fixture.StateStore.State!.Runtime.Runtime);
        Assert.Equal("runtime-session-1", _fixture.StateStore.State.Status.AgentRuntimeSessionId);
        Assert.Equal("runner-1", _fixture.StateStore.State.Runtime.RunnerId);
        Assert.Equal("/work", _fixture.StateStore.State.Runtime.WorkDir);
    }

    private IAgentSessionGrain NewGrain() =>
        _fixture.Grains.GetGrain<IAgentSessionGrain>($"runtime-grain-{Guid.NewGuid():N}");

    private static OpenAgentSessionCommand OpenCommand(string runtime = "opencode") => new(
        "runner-1",
        runtime,
        WorkDir: "/work",
        Metadata: new AgentSessionMetadata()
            .WithLabel("mohist.io/project-id", "project-1")
            .WithLabel("mohist.io/source-kind", "workflow")
            .WithLabel("mohist.io/source-id", "workflow-1")
            .WithLabel("mohist.io/session-name", "build"));
}
