using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public class AgentTurnActivityLifecycleSpecs : AgentJobGrainTestSupport
{
    public AgentTurnActivityLifecycleSpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task TerminalSessionActivity_ForEarlierTurnDoesNotChangeCurrentTurnOrActivity()
    {
        var sessionId = $"session-522-stale-activity-{Guid.NewGuid():N}";
        var projectId = $"project-522-{Guid.NewGuid():N}";
        var session = Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            RunnerId: string.Empty,
            AgentRuntime: "opencode",
            WorkDir: "/tmp/turn-522",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "agent-test",
            })));
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand("runtime-1", WorkDir: "/tmp/turn-522"));
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(6));

        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand("input-a", "turn-a", "first follow up", "generic-followup"));
        await session.MarkTurnExecutingAsync("turn-a");
        await session.MarkTurnTerminalAsync("turn-a", AgentTurnStatus.Completed, null);
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand("input-b", "turn-b", "second follow up", "generic-followup"));
        await session.MarkTurnExecutingAsync("turn-b");
        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                "{\"activity\":\"unknown\",\"status\":\"failed\",\"turnId\":\"turn-a\",\"source\":\"cancel\"}") },
            "runtime-1"));

        var turns = await session.ListTurnsAsync();
        Assert.Equal(AgentTurnStatus.Completed, Assert.Single(turns, turn => turn.Id == "turn-a").Status);
        Assert.Equal(AgentTurnStatus.Executing, Assert.Single(turns, turn => turn.Id == "turn-b").Status);
        Assert.Equal("active", (await session.GetAsync())!.Status);
    }
}
