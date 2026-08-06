using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public sealed class SessionTreeLifecycleSpecs
{
    private readonly AgentJobGrainFixture _fixture;

    public SessionTreeLifecycleSpecs(AgentJobGrainFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DetachReceiptMismatchFailsClosed_AndReplayPublishesExactlyOnce()
    {
        var projectId = $"tree-detach-replay-{Guid.NewGuid():N}";
        var parentId = $"tree-parent-{Guid.NewGuid():N}";
        var childId = $"tree-child-{Guid.NewGuid():N}";
        await OpenSessionAsync(projectId, parentId, "root-agent");
        var child = await OpenSessionAsync(projectId, childId, "child-agent");
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        await AttachAsync(fence, child, projectId, parentId, childId, "edge-detach-replay", "command-detach-replay", "job-detach-replay");

        var begin = new BeginSessionTreeDetachCommand(
            projectId,
            "edge-detach-replay",
            parentId,
            childId,
            "command-detach-replay",
            "job-detach-replay",
            1);
        var firstBegin = await fence.BeginDetachAsync(begin);
        Assert.Equal(SessionTreeDetachMutationState.Pending, firstBegin.State);
        Assert.Equal(firstBegin, await fence.BeginDetachAsync(begin));

        var exactReceipt = new SessionTreeDetachReceipt(
            begin.CommandId,
            begin.EdgeId,
            begin.ParentSessionId,
            begin.ChildSessionId,
            firstBegin.Revision,
            begin.ChildLaunchJobId,
            begin.ExpectedAttachedRevision);
        foreach (var wrongReceipt in new[]
        {
            exactReceipt with { CommandId = "wrong-command" },
            exactReceipt with { EdgeId = "wrong-edge" },
            exactReceipt with { ParentSessionId = "wrong-parent" },
            exactReceipt with { ChildSessionId = "wrong-child" },
            exactReceipt with { ChildLaunchJobId = "wrong-job" },
            exactReceipt with { Revision = firstBegin.Revision + 1 },
            exactReceipt with { ExpectedAttachedRevision = begin.ExpectedAttachedRevision + 1 },
        })
        {
            var wrong = await fence.AcknowledgeDetachAsync(wrongReceipt);
            Assert.Equal(SessionTreeDetachMutationState.ReconciliationRequired, wrong.State);
        }
        var afterMismatch = await fence.GetAsync();
        Assert.True(afterMismatch.ReconciliationRequired);
        var exactAfterMismatch = await fence.AcknowledgeDetachAsync(exactReceipt);
        Assert.Equal(SessionTreeDetachMutationState.ReconciliationRequired, exactAfterMismatch.State);
        var commitAfterMismatch = await fence.CommitDetachAsync(
            begin.CommandId,
            begin.EdgeId,
            firstBegin.Revision);
        Assert.Equal(SessionTreeDetachMutationState.ReconciliationRequired, commitAfterMismatch.State);
        Assert.Equal(afterMismatch.GraphRevision, (await fence.GetAsync()).GraphRevision);
    }

    [Fact]
    public async Task ManualDetachKeepsBarrierUntilExactCommit_AndCommitReplayDoesNotAdvanceGraph()
    {
        var projectId = $"tree-manual-detach-{Guid.NewGuid():N}";
        var parentId = $"tree-manual-parent-{Guid.NewGuid():N}";
        var childId = $"tree-manual-child-{Guid.NewGuid():N}";
        await OpenSessionAsync(projectId, parentId, "root-agent");
        var child = await OpenSessionAsync(projectId, childId, "child-agent");
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        await AttachAsync(fence, child, projectId, parentId, childId, "edge-manual-child", "command-manual-child", "job-manual-child");

        var detach = new BeginSessionTreeDetachCommand(
            projectId,
            "edge-manual-child",
            parentId,
            childId,
            "command-manual-detach",
            "job-manual-child",
            1);
        var begun = await fence.BeginDetachAsync(detach);
        Assert.Equal(SessionTreeDetachMutationState.Pending, begun.State);

        var second = new ReserveSessionTreeLinkCommand(
            projectId,
            "edge-manual-reserved",
            parentId,
            "child-manual-reserved",
            "/workspace",
            "runner-1",
            "opencode",
            "runtime-session",
            "command-manual-reserved",
            "job-manual-reserved",
            "root-agent",
            1,
            SessionTreeExpectedLinkState.Absent);
        Assert.Equal(LinkReservationState.Reserved, (await fence.ReserveAsync(second)).State);
        var blockedAttach = await fence.BeginFinalizeAsync(
            second.CommandId,
            second.EdgeId,
            new SessionTreeBindingUseReceipt(
                "receipt-manual-reserved",
                projectId,
                second.CommandId,
                second.EdgeId,
                parentId,
                second.ExpectedWorkDir,
                second.ExpectedRunnerId,
                second.ExpectedRuntime,
                second.ExpectedRuntimeSessionId,
                second.ExpectedBindingEpoch!.Value,
                ParentAgentId: second.ParentAgentId!));
        Assert.Equal(LinkReservationState.Reserved, blockedAttach.State);
        Assert.Equal("session_tree_mutation_busy", blockedAttach.RejectionReason);

        var blockedStop = await fence.BeginStopSnapshotAsync(new BeginSessionTreeStopSnapshotCommand(
            projectId,
            parentId,
            "operation-manual-barrier",
            "stop-input:manual-barrier",
            "fingerprint:manual-barrier"));
        Assert.Equal(SessionTreeStopSnapshotDisposition.Blocked, blockedStop.Disposition);
        Assert.Equal("session_tree_mutation_pending", blockedStop.RejectionReason);

        var applied = await child.ApplyParentLinkDetachAsync(
            DetachCommand(
                detach.CommandId,
                detach.EdgeId,
                detach.ChildSessionId,
                detach.ParentSessionId,
                detach.ChildLaunchJobId,
                begun.Revision));
        var acknowledged = await fence.AcknowledgeDetachAsync(applied.Receipt!);
        Assert.Equal(SessionTreeDetachMutationState.Acknowledged, acknowledged.State);
        var committed = await fence.CommitDetachAsync(detach.CommandId, detach.EdgeId, begun.Revision);
        Assert.Equal(SessionTreeDetachMutationState.Detached, committed.State);
        Assert.Equal(committed, await fence.CommitDetachAsync(detach.CommandId, detach.EdgeId, begun.Revision));
        Assert.Equal(2, (await fence.GetAsync()).GraphRevision);
    }

    [Fact]
    public async Task StopSnapshotReadsDurableSourceFacts_AndDetachAfterPublishCannotChangeThem()
    {
        var projectId = $"tree-stop-snapshot-{Guid.NewGuid():N}";
        var parentId = $"tree-stop-parent-{Guid.NewGuid():N}";
        var childId = $"tree-stop-child-{Guid.NewGuid():N}";
        await OpenSessionAsync(projectId, parentId, "root-agent");
        var child = await OpenSessionAsync(projectId, childId, "child-agent");
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        await AttachAsync(fence, child, projectId, parentId, childId, "edge-stop-child", "command-stop-child", "job-stop-child");

        var command = new BeginSessionTreeStopSnapshotCommand(
            projectId,
            parentId,
            "operation-stop-snapshot",
            "stop-input:operation-stop-snapshot",
            "fingerprint:operation-stop-snapshot");
        var started = await fence.BeginStopSnapshotAsync(command);
        Assert.Equal(SessionTreeStopSnapshotDisposition.Started, started.Disposition);
        Assert.Equal(1, started.Snapshot!.GraphRevision);
        Assert.Equal(
            new[] { parentId, childId },
            started.Snapshot.Membership.Select(item => item.SessionId).ToArray());
        var childTarget = Assert.Single(started.Snapshot.Targets, item => item.SessionId == childId);
        Assert.Equal("runner-1", childTarget.RunnerId);
        Assert.Equal("opencode", childTarget.Runtime);
        Assert.Equal("/workspace", childTarget.WorkDir);
        Assert.Equal(
            started.Snapshot.Membership.Select(item => item.SessionId).OrderBy(item => item),
            started.Snapshot.Targets.Select(item => item.SessionId).OrderBy(item => item));
        Assert.True((await fence.GetAsync()).ActiveTreeStop);

        var active = await fence.SetStopAdmissionAsync(
            command.OperationId,
            SessionTreeStopAdmissionOutcome.Running);
        Assert.True(active.Active);
        var blockedInside = await fence.ReserveAsync(new ReserveSessionTreeLinkCommand(
            projectId,
            "edge-stop-inside",
            parentId,
            "child-stop-inside",
            "/workspace",
            "runner-1",
            "opencode",
            null,
            "command-stop-inside",
            "job-stop-inside"));
        Assert.Equal("parent_tree_stop_in_progress", blockedInside.RejectionReason);
        var allowedOutside = await fence.ReserveAsync(new ReserveSessionTreeLinkCommand(
            projectId,
            "edge-stop-outside",
            "other-parent",
            "child-stop-outside",
            "/workspace",
            "runner-1",
            "opencode",
            null,
            "command-stop-outside",
            "job-stop-outside"));
        Assert.Equal(LinkReservationState.Reserved, allowedOutside.State);

        var detachBegin = await fence.BeginDetachAsync(new BeginSessionTreeDetachCommand(
            projectId,
            "edge-stop-child",
            parentId,
            childId,
            "command-stop-detach",
            "job-stop-child",
            1));
        Assert.Equal(SessionTreeDetachMutationState.Pending, detachBegin.State);
        var detachApplied = await child.ApplyParentLinkDetachAsync(new ApplyParentLinkDetachCommand(
            "edge-stop-child",
            parentId,
            "job-stop-child",
            detachBegin.Revision,
            "command-stop-detach",
            childId,
            1));
        Assert.Equal(SessionTreeDetachMutationState.Detached, detachApplied.State);
        var detachAck = await fence.AcknowledgeDetachAsync(detachApplied.Receipt!);
        Assert.Equal(SessionTreeDetachMutationState.Acknowledged, detachAck.State);
        var detachCommitted = await fence.CommitDetachAsync(
            "command-stop-detach",
            "edge-stop-child",
            detachBegin.Revision);
        Assert.Equal(SessionTreeDetachMutationState.Detached, detachCommitted.State);
        Assert.Equal(
            detachCommitted,
            await fence.CommitDetachAsync("command-stop-detach", "edge-stop-child", detachBegin.Revision));
        Assert.Equal(2, (await fence.GetAsync()).GraphRevision);
        var replay = await fence.BeginStopSnapshotAsync(command);
        Assert.Equal(SessionTreeStopSnapshotDisposition.Replayed, replay.Disposition);
        Assert.Equal(started.Snapshot.Membership, replay.Snapshot!.Membership);
        Assert.Equal(started.Snapshot.Targets, replay.Snapshot.Targets);

        var terminal = await fence.SetStopAdmissionAsync(
            command.OperationId,
            SessionTreeStopAdmissionOutcome.Completed);
        Assert.False(terminal.Active);
    }

    [Fact]
    public async Task MaterializingSnapshotRejectsReservations_ThenReplaysFromTheSameSource()
    {
        var projectId = $"tree-stop-materializing-{Guid.NewGuid():N}";
        var rootId = $"tree-stop-materializing-root-{Guid.NewGuid():N}";
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var command = new BeginSessionTreeStopSnapshotCommand(
            projectId,
            rootId,
            "operation-materializing",
            "stop-input:materializing",
            "fingerprint:materializing");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fence.BeginStopSnapshotAsync(command));
        var materializing = await fence.GetAsync();
        Assert.Equal(SessionTreeStopSnapshotPhase.Materializing, Assert.Single(materializing.StopSnapshots!).Phase);

        var exception = await Assert.ThrowsAsync<AgentSpawnValidationPendingException>(() =>
            fence.ReserveAsync(new ReserveSessionTreeLinkCommand(
                projectId,
                "edge-materializing",
                "other-parent",
                "child-materializing",
                "/workspace",
                "runner-1",
                "opencode",
                null,
                "command-materializing",
                "job-materializing")));
        Assert.Equal("stop_snapshot_materializing", exception.Reason);

        await OpenSessionAsync(projectId, rootId, "root-agent");
        var recovered = await fence.BeginStopSnapshotAsync(command);
        Assert.Equal(SessionTreeStopSnapshotDisposition.Started, recovered.Disposition);
        Assert.Equal(SessionTreeStopSnapshotPhase.Frozen, recovered.Snapshot!.Phase);
        var replay = await fence.BeginStopSnapshotAsync(command);
        Assert.Equal(SessionTreeStopSnapshotDisposition.Replayed, replay.Disposition);
        Assert.Equal(recovered.Snapshot.ProjectId, replay.Snapshot!.ProjectId);
        Assert.Equal(recovered.Snapshot.GraphRevision, replay.Snapshot.GraphRevision);
        Assert.Equal(recovered.Snapshot.Membership, replay.Snapshot.Membership);
        Assert.Equal(recovered.Snapshot.Targets, replay.Snapshot.Targets);
    }

    [Fact]
    public async Task StopSnapshotRejectsReservedMembershipParent_AndKeepsOtherParentReservation()
    {
        var projectId = $"tree-stop-reservation-{Guid.NewGuid():N}";
        var rootId = $"tree-stop-root-{Guid.NewGuid():N}";
        await OpenSessionAsync(projectId, rootId, "root-agent");
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var inside = await fence.ReserveAsync(new ReserveSessionTreeLinkCommand(
            projectId,
            "edge-inside",
            rootId,
            "child-inside",
            "/workspace",
            "runner-1",
            "opencode",
            null,
            "command-inside",
            "job-inside"));
        var outside = await fence.ReserveAsync(new ReserveSessionTreeLinkCommand(
            projectId,
            "edge-outside",
            "other-parent",
            "child-outside",
            "/workspace",
            "runner-1",
            "opencode",
            null,
            "command-outside",
            "job-outside"));
        Assert.Equal(LinkReservationState.Reserved, inside.State);
        Assert.Equal(LinkReservationState.Reserved, outside.State);

        var started = await fence.BeginStopSnapshotAsync(new BeginSessionTreeStopSnapshotCommand(
            projectId,
            rootId,
            "operation-reservation",
            "stop-input:reservation",
            "fingerprint:reservation"));
        Assert.Equal(SessionTreeStopSnapshotDisposition.Started, started.Disposition);
        var state = await fence.GetAsync();
        Assert.Equal(
            LinkReservationState.Rejected,
            state.Reservations!.Single(item => item.EdgeId == "edge-inside").State);
        Assert.Equal(
            "parent_tree_stop_in_progress",
            state.Reservations!.Single(item => item.EdgeId == "edge-inside").RejectionReason);
        Assert.Equal(
            LinkReservationState.Reserved,
            state.Reservations!.Single(item => item.EdgeId == "edge-outside").State);
    }

    private async Task<IAgentSessionGrain> OpenChildAsync(string suffix)
    {
        var childId = $"session-child-{suffix}";
        var child = await OpenSessionAsync("project-lifecycle", childId, "agent-lifecycle");
        var attached = await child.ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
            $"attach-{suffix}",
            $"edge-{suffix}",
            "parent",
            "parent-agent",
            $"job-{suffix}",
            1,
            "/workspace",
            "runner-1",
            "opencode",
            "runtime-session",
            "project-lifecycle",
            1,
            "standalone-receipt",
            SessionTreeExpectedLinkState.Absent));
        Assert.Equal(SessionTreeAttachMutationState.Attached, attached.State);
        return child;
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
        await session.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "runtime-session",
            ExpectedRunnerId: "runner-1",
            ExpectedRuntime: "opencode"));
        return session;
    }

    private async Task AttachAsync(
        ISessionTreeMutationFenceGrain fence,
        IAgentSessionGrain child,
        string projectId,
        string parentId,
        string childId,
        string edgeId,
        string commandId,
        string jobId)
    {
        var reserved = await fence.ReserveAsync(new ReserveSessionTreeLinkCommand(
            projectId,
            edgeId,
            parentId,
            childId,
            "/workspace",
            "runner-1",
            "opencode",
            "runtime-session",
            commandId,
            jobId,
            "root-agent",
            1,
            SessionTreeExpectedLinkState.Absent));
        Assert.Equal(LinkReservationState.Reserved, reserved.State);
        var bindingResult = await _fixture.Grains.GetGrain<IAgentSessionGrain>(parentId)
            .AcquireChildAttachBindingAsync(new AcquireChildAttachBindingCommand(
                projectId,
                commandId,
                edgeId,
                parentId,
                "/workspace",
                "runner-1",
                "opencode",
                "runtime-session",
                1,
                "root-agent"));
        Assert.NotNull(bindingResult.Receipt);
        var binding = bindingResult.Receipt!;
        var begun = await fence.BeginFinalizeAsync(commandId, edgeId, binding);
        var attached = await child.ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
            commandId,
            edgeId,
            parentId,
            "root-agent",
            jobId,
            begun.Revision,
            "/workspace",
            "runner-1",
            "opencode",
            "runtime-session",
            projectId,
            binding.BindingEpoch,
            binding.ReceiptId,
            SessionTreeExpectedLinkState.Absent));
        Assert.Equal(SessionTreeAttachMutationState.Attached, attached.State);
        var acknowledged = await fence.AcknowledgeFinalizeAsync(attached.Receipt!);
        Assert.True(
            !acknowledged.ReconciliationRequired && acknowledged.State == LinkReservationState.Reserved,
            $"state={acknowledged.State}; reason={acknowledged.RejectionReason}; reconciliation={acknowledged.ReconciliationRequired}");
        var committed = await fence.CommitFinalizeAsync(commandId, edgeId, begun.Revision);
        Assert.True(
            committed.State == LinkReservationState.Attached,
            $"state={committed.State}; reason={committed.RejectionReason}; reconciliation={committed.ReconciliationRequired}");
    }

    private static ApplyParentLinkDetachCommand DetachCommand(
        string commandId,
        string edgeId,
        string childSessionId,
        string parentSessionId,
        string jobId,
        long revision) =>
        new(edgeId, parentSessionId, jobId, revision, commandId, childSessionId, 1);
}
