using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Orleans.Core.Internal;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public class AgentSessionStopClaimRecoverySpecs : AgentJobGrainTestSupport
{
    public AgentSessionStopClaimRecoverySpecs(AgentJobGrainFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task TerminalFactAfterReactivationReleasesPersistedStopClaim()
    {
        var sessionId = $"session-522-stop-claim-{Guid.NewGuid():N}";
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

        const string turnId = "turn-stop-claim";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-stop-claim",
            turnId,
            "follow up",
            "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);
        var claim = await session.ClaimTurnStopAsync(turnId);
        Assert.True(claim.CanDispatch);

        await session.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $"{{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"{turnId}\",\"stopOperationId\":\"{claim.OperationId}\"}}") },
            "runtime-1"));

        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
    }

    [Fact]
    public async Task TerminalFactReleasesClaimThatWasNeverDispatched()
    {
        var sessionId = $"session-522-undispatched-stop-{Guid.NewGuid():N}";
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

        const string turnId = "turn-undispatched-stop";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-undispatched-stop",
            turnId,
            "follow up",
            "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);
        Assert.True((await session.ClaimTurnStopAsync(turnId)).CanDispatch);

        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $"{{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"{turnId}\"}}") },
            "runtime-1"));

        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
    }

    [Fact]
    public async Task UnconfirmedStopFactSettlesClaimWithoutAdmittingAnotherTurn()
    {
        var sessionId = $"session-522-stop-unknown-{Guid.NewGuid():N}";
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

        const string turnId = "turn-stop-unknown";
        await session.RecordFollowupTurnAsync(new RecordFollowupTurnCommand(
            "input-stop-unknown",
            turnId,
            "follow up",
            "generic-followup"));
        await session.MarkTurnExecutingAsync(turnId);
        var claim = await session.ClaimTurnStopAsync(turnId);
        Assert.True(claim.CanDispatch);

        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $"{{\"activity\":\"unknown\",\"status\":\"failed\",\"turnId\":\"{turnId}\",\"stopOperationId\":\"{claim.OperationId}\"}}") },
            "runtime-1"));

        await Assert.ThrowsAsync<SessionActivityUnknownException>(session.BeginFollowupAsync);

        await session.AppendRuntimeEventsAsync(new AppendAgentSessionRuntimeEventsCommand(
            new[] { new AgentSessionRuntimeEventInput(
                RuntimeEventTypes.SessionActivity,
                $"{{\"activity\":\"idle\",\"status\":\"completed\",\"turnId\":\"{turnId}\"}}") },
            "runtime-1"));

        var reservation = await session.BeginFollowupAsync();
        Assert.True(reservation.StartsIdleTurn);
        await session.AbandonFollowupAsync(reservation.OperationId!);
    }
}
