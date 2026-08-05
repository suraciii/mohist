using Mohist.Server.Agent.Grains;
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
    public async Task TerminalReportClaimAndDeliveryAreIdempotentAndKeepTheFirstInputId()
    {
        Assert.Equal(
            "subagent-terminal:edge-terminal-report:job-terminal-report",
            SubagentTerminalReportIdempotencyKeys.For("edge-terminal-report", "job-terminal-report"));
        var child = await OpenChildAsync("terminal-report");
        var claim = await child.ClaimSubagentTerminalReportAsync(new("edge-terminal-report", "job-terminal-report"));
        Assert.Equal(SubagentTerminalReportClaimDisposition.ClaimedPending, claim.Disposition);

        var delivered = await child.RecordSubagentTerminalReportDeliveredAsync(
            new("edge-terminal-report", "job-terminal-report", "parent-input-1"));
        Assert.Equal(SubagentTerminalReportDeliveryDisposition.Delivered, delivered.Disposition);

        var replay = await child.ClaimSubagentTerminalReportAsync(new("edge-terminal-report", "job-terminal-report"));
        Assert.Equal(SubagentTerminalReportClaimDisposition.Delivered, replay.Disposition);
        Assert.Equal("parent-input-1", replay.DeliveredInputId);

        var deliveredReplay = await child.RecordSubagentTerminalReportDeliveredAsync(
            new("edge-terminal-report", "job-terminal-report", "parent-input-1"));
        Assert.Equal(SubagentTerminalReportDeliveryDisposition.AlreadyDelivered, deliveredReplay.Disposition);
        var conflict = await child.RecordSubagentTerminalReportDeliveredAsync(
            new("edge-terminal-report", "job-terminal-report", "parent-input-2"));
        Assert.Equal(SubagentTerminalReportDeliveryDisposition.InputIdConflict, conflict.Disposition);
        Assert.Equal("parent-input-1", conflict.DeliveredInputId);
    }

    [Fact]
    public async Task DetachAndTerminalClaimLinearizeInEitherOrder()
    {
        var detachedFirst = await OpenChildAsync("detach-first");
        var detached = await detachedFirst.ApplyParentLinkDetachAsync(
            DetachCommand(
                "detach-command-1",
                "edge-detach-first",
                "session-child-detach-first",
                "parent",
                "job-detach-first",
                11));
        Assert.Equal(SessionTreeDetachMutationState.Detached, detached.State);

        var suppressed = await detachedFirst.ClaimSubagentTerminalReportAsync(
            new("edge-detach-first", "job-detach-first"));
        Assert.Equal(SubagentTerminalReportClaimDisposition.Suppressed, suppressed.Disposition);
        var detachedReplay = await detachedFirst.ApplyParentLinkDetachAsync(
            DetachCommand(
                "detach-command-1",
                "edge-detach-first",
                "session-child-detach-first",
                "parent",
                "job-detach-first",
                11));
        Assert.Equal(detached, detachedReplay);

        var claimFirst = await OpenChildAsync("claim-first");
        var pending = await claimFirst.ClaimSubagentTerminalReportAsync(
            new("edge-claim-first", "job-claim-first"));
        Assert.Equal(SubagentTerminalReportClaimDisposition.ClaimedPending, pending.Disposition);
        var pendingDetach = await claimFirst.ApplyParentLinkDetachAsync(
            DetachCommand(
                "detach-command-2",
                "edge-claim-first",
                "session-child-claim-first",
                "parent",
                "job-claim-first",
                12));
        Assert.Equal(SessionTreeDetachMutationState.Detached, pendingDetach.State);
        Assert.Equal(TerminalReportState.Pending, pendingDetach.Link!.TerminalReport);

        var delivered = await claimFirst.RecordSubagentTerminalReportDeliveredAsync(
            new("edge-claim-first", "job-claim-first", "parent-input-claim-first"));
        Assert.Equal(SubagentTerminalReportDeliveryDisposition.Delivered, delivered.Disposition);
        var deliveredDetach = await claimFirst.ApplyParentLinkDetachAsync(
            DetachCommand(
                "detach-command-2",
                "edge-claim-first",
                "session-child-claim-first",
                "parent",
                "job-claim-first",
                12));
        Assert.Equal(TerminalReportState.Delivered, deliveredDetach.Link!.TerminalReport);

        var reparent = await claimFirst.ApplyParentLinkDetachAsync(
            DetachCommand(
                "detach-command-3",
                "edge-claim-first",
                "session-child-claim-first",
                "other-parent",
                "job-claim-first",
                13));
        Assert.Equal(SessionTreeDetachMutationState.Rejected, reparent.State);
        Assert.Equal("parent_link_identity_mismatch", reparent.RejectionReason);
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
        var reservedDuringDetach = await fence.ReserveAsync(new ReserveSessionTreeLinkCommand(
            projectId,
            "edge-during-detach",
            "other-parent",
            "child-during-detach",
            "/workspace",
            "runner-1",
            "opencode",
            null,
            "command-during-detach",
            "job-during-detach"));
        Assert.Equal(LinkReservationState.Reserved, reservedDuringDetach.State);
        Assert.Equal(
            "session_tree_mutation_busy",
            (await fence.BeginFinalizeAsync("command-during-detach", "edge-during-detach")).RejectionReason);
        Assert.Equal(
            SessionTreeStopSnapshotDisposition.Blocked,
            (await fence.BeginStopSnapshotAsync(new BeginSessionTreeStopSnapshotCommand(
                projectId,
                parentId,
                "operation-during-detach",
                "stop-input:during-detach",
                "fingerprint:during-detach"))).Disposition);

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
        Assert.Equal(1, (await fence.GetAsync()).GraphRevision);
        Assert.Equal(
            SessionTreeDetachMutationState.Rejected,
            (await fence.CommitDetachAsync(begin.CommandId, begin.EdgeId, firstBegin.Revision)).State);

        var applied = await child.ApplyParentLinkDetachAsync(
            new ApplyParentLinkDetachCommand(
                begin.EdgeId,
                begin.ParentSessionId,
                begin.ChildLaunchJobId,
                firstBegin.Revision,
                begin.CommandId,
                begin.ChildSessionId,
                begin.ExpectedAttachedRevision));
        Assert.Equal(SessionTreeDetachMutationState.Detached, applied.State);
        Assert.NotNull(applied.Receipt);
        var ack = await fence.AcknowledgeDetachAsync(applied.Receipt!);
        Assert.Equal(SessionTreeDetachMutationState.Acknowledged, ack.State);
        Assert.Equal(ack, await fence.AcknowledgeDetachAsync(applied.Receipt!));

        var committed = await fence.CommitDetachAsync(begin.CommandId, begin.EdgeId, firstBegin.Revision);
        var replay = await fence.CommitDetachAsync(begin.CommandId, begin.EdgeId, firstBegin.Revision);
        Assert.Equal(SessionTreeDetachMutationState.Detached, committed.State);
        Assert.Equal(committed, replay);
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

        var rejected = await fence.ReserveAsync(new ReserveSessionTreeLinkCommand(
            projectId,
            "edge-materializing",
            "other-parent",
            "child-materializing",
            "/workspace",
            "runner-1",
            "opencode",
            null,
            "command-materializing",
            "job-materializing"));
        Assert.Equal(LinkReservationState.Rejected, rejected.State);
        Assert.Equal("stop_snapshot_materializing", rejected.RejectionReason);

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
            null));
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
        return session;
    }

    private static async Task AttachAsync(
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
            null,
            commandId,
            jobId));
        Assert.Equal(LinkReservationState.Reserved, reserved.State);
        var begun = await fence.BeginFinalizeAsync(commandId, edgeId);
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
            null));
        Assert.Equal(SessionTreeAttachMutationState.Attached, attached.State);
        await fence.AcknowledgeFinalizeAsync(attached.Receipt!);
        var committed = await fence.CommitFinalizeAsync(commandId, edgeId, begun.Revision);
        Assert.Equal(LinkReservationState.Attached, committed.State);
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
