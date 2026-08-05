using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public sealed class SessionTreeLifecycleRecoverySpecs
{
    private readonly AgentJobGrainFixture _fixture;

    public SessionTreeLifecycleRecoverySpecs(AgentJobGrainFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AttachAndDetachRecoveryWindowsReplayExactTupleWithoutRevisionReuse()
    {
        var projectId = $"tree-crash-windows-{Guid.NewGuid():N}";
        var parentId = $"tree-crash-parent-{Guid.NewGuid():N}";
        var childId = $"tree-crash-child-{Guid.NewGuid():N}";
        await OpenSessionAsync(projectId, parentId, "root-agent");
        var child = await OpenSessionAsync(projectId, childId, "child-agent");
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var command = new ReserveSessionTreeLinkCommand(
            projectId,
            "edge-crash-window",
            parentId,
            childId,
            "/workspace",
            "runner-1",
            "opencode",
            null,
            "command-crash-window",
            "job-crash-window");

        await fence.ReserveAsync(command);
        var attachBegin = await fence.BeginFinalizeAsync(command.CommandId, command.EdgeId);
        Assert.Equal(1, attachBegin.Revision);
        Assert.Equal(attachBegin, await fence.BeginFinalizeAsync(command.CommandId, command.EdgeId));
        var attachApplied = await child.ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
            command.CommandId,
            command.EdgeId,
            parentId,
            "root-agent",
            command.ChildLaunchJobId!,
            attachBegin.Revision,
            command.ExpectedWorkDir,
            command.ExpectedRunnerId,
            command.ExpectedRuntime,
            command.ExpectedRuntimeSessionId));
        Assert.Equal(SessionTreeAttachMutationState.Attached, attachApplied.State);
        var attachAck = await fence.AcknowledgeFinalizeAsync(attachApplied.Receipt!);
        Assert.Equal(LinkReservationState.Reserved, attachAck.State);
        Assert.Equal(attachAck, await fence.AcknowledgeFinalizeAsync(attachApplied.Receipt!));
        Assert.Equal(0, (await fence.GetAsync()).GraphRevision);
        Assert.Equal(
            LinkReservationState.Attached,
            (await fence.CommitFinalizeAsync(command.CommandId, command.EdgeId, attachBegin.Revision)).State);
        Assert.Equal(
            LinkReservationState.Attached,
            (await fence.CommitFinalizeAsync(command.CommandId, command.EdgeId, attachBegin.Revision)).State);

        var detachBegin = await fence.BeginDetachAsync(new BeginSessionTreeDetachCommand(
            projectId,
            command.EdgeId,
            parentId,
            childId,
            "command-crash-detach",
            command.ChildLaunchJobId!,
            attachBegin.Revision));
        Assert.Equal(2, detachBegin.Revision);
        var detachApplied = await child.ApplyParentLinkDetachAsync(new ApplyParentLinkDetachCommand(
            command.EdgeId,
            parentId,
            command.ChildLaunchJobId!,
            detachBegin.Revision,
            "command-crash-detach",
            childId,
            attachBegin.Revision));
        var detachAck = await fence.AcknowledgeDetachAsync(detachApplied.Receipt!);
        Assert.Equal(SessionTreeDetachMutationState.Acknowledged, detachAck.State);
        Assert.Equal(detachAck, await fence.AcknowledgeDetachAsync(detachApplied.Receipt!));
        Assert.Equal(
            SessionTreeDetachMutationState.Detached,
            (await fence.CommitDetachAsync("command-crash-detach", command.EdgeId, detachBegin.Revision)).State);
        Assert.Equal(
            SessionTreeDetachMutationState.Detached,
            (await fence.CommitDetachAsync("command-crash-detach", command.EdgeId, detachBegin.Revision)).State);
        Assert.Equal(2, (await fence.GetAsync()).GraphRevision);
    }

    private async Task<IAgentSessionGrain> OpenSessionAsync(
        string projectId,
        string sessionId,
        string agentId)
    {
        var session = _fixture.Grains.GetGrain<IAgentSessionGrain>(sessionId);
        await session.OpenAsync(new OpenAgentSessionCommand(
            "runner-1",
            "opencode",
            "/workspace",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = agentId,
                [GenericAgentSessionMetadata.AgentName] = agentId,
            })));
        return session;
    }
}
