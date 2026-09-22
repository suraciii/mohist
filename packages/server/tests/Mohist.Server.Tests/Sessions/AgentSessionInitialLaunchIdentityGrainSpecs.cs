using System.Text.Json;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.Tests.Sessions;

[Collection("AgentSessionGrainComponent")]
[Trait("level", "L0")]
public sealed class AgentSessionInitialLaunchIdentityGrainSpecs
{
    private readonly AgentSessionGrainFixture _fixture;

    public AgentSessionInitialLaunchIdentityGrainSpecs(AgentSessionGrainFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public async Task EnsureInitialLaunch_MatchingIdentity_ReplaysIdentically()
    {
        var (grain, sessionId) = await CreateSessionAsync("initial-launch-replay", "project-1", "workflow-agent");
        var command = LaunchCommand("initial-input", "initial-turn", "initial prompt", "job-1", "project-1", "workflow-agent");

        var first = await grain.EnsureInitialLaunchAsync(command);
        var replay = await grain.EnsureInitialLaunchAsync(command with { Prompt = "initial prompt" });

        Assert.False(first.AlreadyPersisted);
        Assert.True(replay.AlreadyPersisted);
        Assert.Equal(first.InputId, replay.InputId);
        Assert.Equal(first.TurnId, replay.TurnId);
        var state = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(state);
        Assert.Single(state!.Status.Inputs!);
        Assert.Single(state.Status.Turns!);
    }

    [Fact]
    public async Task EnsureInitialLaunch_ForeignAgent_PrecedesSameKeyReplayWithoutMutation()
    {
        var (grain, sessionId) = await CreateSessionAsync("initial-launch-foreign-agent", "project-1", "workflow-agent");
        await grain.EnsureInitialLaunchAsync(
            LaunchCommand("replay-input", "replay-turn", "initial prompt", "job-1", "project-1", "workflow-agent"));

        var before = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(before);
        var serializedBefore = JsonSerializer.Serialize(before);
        var eventCount = _fixture.StateStore.Events.Count;

        var mismatch = await Assert.ThrowsAsync<AgentSessionIdentityMismatchException>(() =>
            grain.EnsureInitialLaunchAsync(
                LaunchCommand("replay-input", "replay-turn", "initial prompt", "job-1", "project-1", "foreign-agent")));

        Assert.Equal("workflow-agent", mismatch.ActualAgentId);
        Assert.Equal("foreign-agent", mismatch.ExpectedAgentId);
        var after = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(after);
        Assert.Equal(serializedBefore, JsonSerializer.Serialize(after));
        Assert.Equal(eventCount, _fixture.StateStore.Events.Count);
    }

    [Fact]
    public async Task EnsureInitialLaunch_ForeignProjectWithSameBuiltinAgentId_PrecedesNewAppendWithoutMutation()
    {
        var builtinAgentId = "builtin:reviewer";
        var (grain, sessionId) = await CreateSessionAsync("initial-launch-foreign-project", "project-1", builtinAgentId);

        var before = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(before);
        var serializedBefore = JsonSerializer.Serialize(before!);
        var eventCount = _fixture.StateStore.Events.Count;

        var mismatch = await Assert.ThrowsAsync<AgentSessionIdentityMismatchException>(() =>
            grain.EnsureInitialLaunchAsync(
                LaunchCommand("new-input", "new-turn", "new prompt", "job-2", "project-2", builtinAgentId)));

        Assert.Equal("project-1", mismatch.ActualProjectId);
        Assert.Equal("project-2", mismatch.ExpectedProjectId);
        Assert.Equal(builtinAgentId, mismatch.ActualAgentId);
        var after = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(after);
        Assert.Equal(serializedBefore, JsonSerializer.Serialize(after));
        Assert.Empty(after!.Status.Inputs ?? []);
        Assert.Empty(after.Status.Turns ?? []);
        Assert.Empty(after.Status.PendingFollowups ?? []);
        Assert.Equal(eventCount, _fixture.StateStore.Events.Count);
    }

    [Fact]
    public async Task EnsureInitialLaunch_MissingOrPartialIdentityMetadata_IsRejectedWithoutMutation()
    {
        var (grain, sessionId) = await CreateSessionAsync("initial-launch-missing-identity", "project-1", "workflow-agent");

        var before = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(before);
        var serializedBefore = JsonSerializer.Serialize(before!);
        var eventCount = _fixture.StateStore.Events.Count;

        var bare = LaunchCommand("bare-input", "bare-turn", "bare prompt", "job-1", "project-1", "workflow-agent")
            with { Metadata = null };
        await Assert.ThrowsAsync<ArgumentException>(() => grain.EnsureInitialLaunchAsync(bare));

        var halfSpecified = LaunchCommand("half-input", "half-turn", "half prompt", "job-1", "project-1", "workflow-agent")
            with
            {
                Metadata = new AgentSessionMetadata(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["mohist.io/project-id"] = "project-1",
                        ["mohist.io/source-kind"] = "workflow",
                        ["mohist.io/source-id"] = "workflow-1",
                        ["mohist.io/session-name"] = "build",
                        ["mohist.io/agent-id"] = " ",
                    }),
            };
        await Assert.ThrowsAsync<ArgumentException>(() => grain.EnsureInitialLaunchAsync(halfSpecified));

        var after = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(after);
        Assert.Equal(serializedBefore, JsonSerializer.Serialize(after!));
        Assert.Equal(eventCount, _fixture.StateStore.Events.Count);
    }

    [Fact]
    public async Task EnsureInitialLaunch_PersistedSessionWithoutAgentIdentity_RejectsAppendWithoutMutation()
    {
        var sessionId = $"initial-launch-incomplete-{Guid.NewGuid():N}";
        var incomplete = new AgentSession
        {
            Id = sessionId,
            Metadata = new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", "project-1")
                .WithLabel("mohist.io/source-kind", "workflow")
                .WithLabel("mohist.io/source-id", "workflow-1")
                .WithLabel("mohist.io/session-name", "build"),
            Runtime = new AgentSessionRuntime("runner-1", "/work", "opencode"),
            Status = AgentSessionStatusSnapshot.Created(_fixture.TimeProvider.GetUtcNow().UtcDateTime),
        };
        await _fixture.StateStore.SaveAsync(sessionId, incomplete);
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);

        var serializedBefore = JsonSerializer.Serialize(await _fixture.StateStore.LoadAsync(sessionId));

        var mismatch = await Assert.ThrowsAsync<AgentSessionIdentityMismatchException>(() =>
            grain.EnsureInitialLaunchAsync(
                LaunchCommand("incomplete-input", "incomplete-turn", "initial prompt", "job-1", "project-1", "workflow-agent")));

        Assert.Null(mismatch.ActualAgentId);
        Assert.Equal("workflow-agent", mismatch.ExpectedAgentId);
        var after = await _fixture.StateStore.LoadAsync(sessionId);
        Assert.NotNull(after);
        Assert.Empty(after!.Status.Inputs ?? []);
        Assert.Empty(after.Status.Turns ?? []);
        Assert.Equal(serializedBefore, JsonSerializer.Serialize(after));
    }

    private async Task<(IAgentSessionGrain Grain, string SessionId)> CreateSessionAsync(
        string sessionName,
        string projectId,
        string agentId)
    {
        var sessionId = $"initial-launch-identity-{Guid.NewGuid():N}";
        var grain = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.OpenAsync(new OpenAgentSessionCommand(
            "runner-1",
            "opencode",
            WorkDir: "/work",
            Metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", projectId)
                .WithLabel("mohist.io/source-kind", "workflow")
                .WithLabel("mohist.io/source-id", "workflow-1")
                .WithLabel("mohist.io/session-name", sessionName)
                .WithLabel("mohist.io/agent-id", agentId)));
        return (grain, sessionId);
    }

    private static EnsureInitialLaunchCommand LaunchCommand(
        string inputId,
        string turnId,
        string prompt,
        string jobId,
        string projectId,
        string agentId) =>
        new(
            InputId: inputId,
            TurnId: turnId,
            Prompt: prompt,
            Source: "workflow",
            JobId: jobId,
            Metadata: new AgentSessionMetadata()
                .WithLabel("mohist.io/project-id", projectId)
                .WithLabel("mohist.io/source-kind", "workflow")
                .WithLabel("mohist.io/source-id", "workflow-1")
                .WithLabel("mohist.io/session-name", "build")
                .WithLabel("mohist.io/agent-id", agentId));
}
