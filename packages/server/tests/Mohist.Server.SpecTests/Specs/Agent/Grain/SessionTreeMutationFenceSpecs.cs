using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Agent.Grain;

[Collection("AgentJobGrain")]
public sealed class SessionTreeMutationFenceSpecs
{
    private readonly AgentJobGrainFixture _fixture;

    public SessionTreeMutationFenceSpecs(AgentJobGrainFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReservationsAreIndependent_AndFinalizeRevisionsAreStrictlyOrdered()
    {
        var projectId = $"tree-fence-{Guid.NewGuid():N}";
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var parentId = $"parent-{projectId}";
        await OpenParentAsync(projectId, parentId);
        var firstCommand = Command(projectId, "edge-1", "command-1", parentId);
        var secondCommand = Command(projectId, "edge-2", "command-2", parentId);
        var firstBinding = await AcquireAsync(parentId, projectId, firstCommand);
        var secondBinding = await AcquireAsync(parentId, projectId, secondCommand);

        var first = await fence.ReserveAsync(firstCommand);
        var second = await fence.ReserveAsync(secondCommand);

        Assert.Equal(0, first.Revision);
        Assert.Equal(0, second.Revision);
        var reserved = await fence.GetAsync();
        Assert.Equal(2, reserved.Reservations!.Count);
        Assert.Empty(reserved.PendingMutations ?? []);
        Assert.Equal(0, reserved.GraphRevision);

        var assignedFirst = await fence.BeginFinalizeAsync("command-1", "edge-1", firstBinding);
        var reservedDuringFinalize = await fence.ReserveAsync(Command(projectId, "edge-3", "command-3", parentId));
        var busySecond = await fence.BeginFinalizeAsync("command-2", "edge-2", secondBinding);
        Assert.Equal(1, assignedFirst.Revision);
        Assert.Equal(LinkReservationState.Reserved, reservedDuringFinalize.State);
        Assert.Equal("session_tree_mutation_busy", busySecond.RejectionReason);
        Assert.Equal(LinkReservationState.Reserved, busySecond.State);

        var unacknowledged = await fence.CommitFinalizeAsync("command-1", "edge-1", 1);
        Assert.Equal("parent_tree_link_not_acknowledged", unacknowledged.RejectionReason);
        Assert.Equal(0, (await fence.GetAsync()).GraphRevision);

        await AcknowledgeAttachAsync(fence, firstCommand, firstBinding, 1);
        var attachedFirst = await fence.CommitFinalizeAsync("command-1", "edge-1", 1);
        Assert.Equal(1, attachedFirst.Revision);

        var assignedSecond = await fence.BeginFinalizeAsync("command-2", "edge-2", secondBinding);
        Assert.Equal(2, assignedSecond.Revision);
        await AcknowledgeAttachAsync(fence, secondCommand, secondBinding, 2);
        var attachedSecond = await fence.CommitFinalizeAsync("command-2", "edge-2", 2);
        Assert.Equal(2, attachedSecond.Revision);

        var final = await fence.GetAsync();
        Assert.Equal(2, final.GraphRevision);
        Assert.Equal(
            new[] { 1L, 2L },
            final.Reservations!
                .Where(item => item.State == LinkReservationState.Attached)
                .OrderBy(item => item.EdgeId)
                .Select(item => item.AttachedRevision!.Value));
        Assert.Equal(
            LinkReservationState.Reserved,
            final.Reservations!.Single(item => item.EdgeId == "edge-3").State);
    }

    [Fact]
    public async Task AttachReceiptMismatchFailsClosed_AndExactAckPublishesOnce()
    {
        var projectId = $"tree-fence-receipt-{Guid.NewGuid():N}";
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var parentId = $"parent-{projectId}";
        await OpenParentAsync(projectId, parentId);
        var command = Command(projectId, "edge-receipt", "command-receipt", parentId);
        await fence.ReserveAsync(command);
        var binding = await AcquireAsync(parentId, projectId, command);
        var begun = await fence.BeginFinalizeAsync(command.CommandId, command.EdgeId, binding);

        var exactReceipt = new SessionTreeAttachReceipt(
            command.CommandId,
            command.EdgeId,
            command.ParentSessionId,
            command.ChildSessionId,
            command.ChildLaunchJobId!,
            begun.Revision,
            projectId,
            SessionTreeMutationKind.Attach,
            command.ExpectedWorkDir,
            command.ExpectedRunnerId,
            command.ExpectedRuntime,
            command.ExpectedRuntimeSessionId,
            binding.BindingEpoch,
            binding.ReceiptId,
            SessionTreeExpectedLinkState.Absent,
            command.ParentAgentId!);
        foreach (var wrongReceipt in new[]
        {
            exactReceipt with { CommandId = "wrong-command" },
            exactReceipt with { EdgeId = "wrong-edge" },
            exactReceipt with { ParentSessionId = "wrong-parent" },
            exactReceipt with { ChildSessionId = "wrong-child" },
            exactReceipt with { ChildLaunchJobId = "wrong-job" },
            exactReceipt with { Revision = begun.Revision + 1 },
            exactReceipt with { ProjectId = "wrong-project" },
            exactReceipt with { MutationKind = SessionTreeMutationKind.Detach },
            exactReceipt with { ParentWorkDir = "/wrong-workspace" },
            exactReceipt with { RunnerId = "wrong-runner" },
            exactReceipt with { Runtime = "wrong-runtime" },
            exactReceipt with { RuntimeSessionId = "wrong-runtime-session" },
            exactReceipt with { BindingEpoch = binding.BindingEpoch + 1 },
            exactReceipt with { BindingUseReceiptId = "wrong-receipt" },
            exactReceipt with { ExpectedLinkState = SessionTreeExpectedLinkState.Attached },
            exactReceipt with { ParentAgentId = "wrong-agent" },
        })
        {
            var wrong = await fence.AcknowledgeFinalizeAsync(wrongReceipt);
            Assert.True(wrong.ReconciliationRequired);
        }
        Assert.Equal(0, (await fence.GetAsync()).GraphRevision);
        var blocked = await fence.CommitFinalizeAsync(command.CommandId, command.EdgeId, begun.Revision);
        Assert.True(blocked.ReconciliationRequired);

        var exact = await fence.AcknowledgeFinalizeAsync(exactReceipt);
        Assert.True(exact.ReconciliationRequired);
        var committed = await fence.CommitFinalizeAsync(command.CommandId, command.EdgeId, begun.Revision);
        var replay = await fence.CommitFinalizeAsync(command.CommandId, command.EdgeId, begun.Revision);
        Assert.True(committed.ReconciliationRequired);
        Assert.Equal(committed, replay);
        Assert.Equal(0, (await fence.GetAsync()).GraphRevision);

        foreach (var wrongReceipt in new[]
        {
            exactReceipt with { CommandId = "wrong-attached-command" },
            exactReceipt with { EdgeId = "wrong-attached-edge" },
            exactReceipt with { ParentSessionId = "wrong-attached-parent" },
            exactReceipt with { ChildSessionId = "wrong-attached-child" },
            exactReceipt with { ChildLaunchJobId = "wrong-attached-job" },
            exactReceipt with { Revision = begun.Revision + 1 },
        })
        {
            var wrongReplay = await fence.AcknowledgeFinalizeAsync(wrongReceipt);
            Assert.True(wrongReplay.ReconciliationRequired);
        }
        var attachedReplay = await fence.AcknowledgeFinalizeAsync(exactReceipt);
        Assert.True(attachedReplay.ReconciliationRequired);
    }

    [Fact]
    public async Task BeginFinalizeRechecksDurableParentBindingBeforeAssigningRevision()
    {
        var projectId = $"tree-fence-binding-{Guid.NewGuid():N}";
        var parentId = $"parent-{projectId}";
        var parent = await OpenParentAsync(projectId, parentId);
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var command = Command(projectId, "edge-binding", "command-binding", parentId);
        await fence.ReserveAsync(command);

        await parent.ResetAsync(new ResetAgentSessionCommand(
            "runtime-session",
            "runtime-session-replaced"));

        var rejected = await fence.BeginFinalizeAsync(
            command.CommandId,
            command.EdgeId,
            new SessionTreeBindingUseReceipt(
                "unacquired",
                projectId,
                command.CommandId,
                command.EdgeId,
                parentId,
                command.ExpectedWorkDir,
                command.ExpectedRunnerId,
                command.ExpectedRuntime,
                command.ExpectedRuntimeSessionId,
                command.ExpectedBindingEpoch!.Value,
                ParentAgentId: command.ParentAgentId!));
        Assert.Equal(LinkReservationState.Rejected, rejected.State);
        Assert.Equal("parent_binding_changed", rejected.RejectionReason);
        var state = await fence.GetAsync();
        Assert.Equal(0, state.GraphRevision);
        Assert.Equal(LinkReservationState.Rejected, Assert.Single(state.Reservations!).State);
        Assert.Equal(
            rejected,
            await fence.BeginFinalizeAsync(
                command.CommandId,
                command.EdgeId,
                new SessionTreeBindingUseReceipt(
                    "unacquired",
                    projectId,
                    command.CommandId,
                    command.EdgeId,
                    parentId,
                    command.ExpectedWorkDir,
                    command.ExpectedRunnerId,
                    command.ExpectedRuntime,
                    command.ExpectedRuntimeSessionId,
                    command.ExpectedBindingEpoch!.Value,
                    ParentAgentId: command.ParentAgentId!)));
    }

    [Fact]
    public async Task AcquiredBindingBlocksResetUntilRelease_AndResetUsesEpochCas()
    {
        var projectId = $"tree-fence-reset-race-{Guid.NewGuid():N}";
        var parentId = $"parent-{projectId}";
        var parent = await OpenParentAsync(projectId, parentId);
        var command = Command(projectId, "edge-reset-race", "command-reset-race", parentId);
        var binding = await AcquireAsync(parentId, projectId, command);

        var heldReset = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            parent.ResetAsync(new ResetAgentSessionCommand(
                "runtime-session",
                "runtime-session-replacement",
                "opencode",
                binding.BindingEpoch)));
        Assert.Equal("binding_attach_in_progress", heldReset.Message);

        var released = await parent.ReleaseChildAttachBindingAsync(new ReleaseChildAttachBindingCommand(
            binding,
            "rejected"));
        Assert.Equal(SessionTreeBindingReleaseState.Released, released.State);
        var reset = await parent.ResetAsync(new ResetAgentSessionCommand(
            "runtime-session",
            "runtime-session-replacement",
            "opencode",
            binding.BindingEpoch));
        Assert.Equal("reset", reset.Operation);
        Assert.Equal(binding.BindingEpoch + 1, (await parent.GetAsync())!.BindingEpoch);

        var staleAcquire = await parent.AcquireChildAttachBindingAsync(new AcquireChildAttachBindingCommand(
            projectId,
            "command-reset-race-stale",
            "edge-reset-race-stale",
            parentId,
            command.ExpectedWorkDir,
            command.ExpectedRunnerId,
            command.ExpectedRuntime,
            "runtime-session-replacement",
            binding.BindingEpoch,
            command.ParentAgentId!));
        Assert.Equal(SessionTreeBindingAcquireState.BindingChanged, staleAcquire.State);
        Assert.Equal("parent_binding_changed", staleAcquire.RejectionReason);
    }

    [Fact]
    public async Task CommitFinalizeReleaseMismatchDoesNotReturnAttachedOrUnlockFence()
    {
        var projectId = $"tree-fence-release-gate-{Guid.NewGuid():N}";
        var parentId = $"parent-{projectId}";
        var parent = await OpenParentAsync(projectId, parentId);
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var command = Command(projectId, "edge-release-gate", "command-release-gate", parentId);
        await fence.ReserveAsync(command);
        var binding = await AcquireAsync(parentId, projectId, command);
        var begun = await fence.BeginFinalizeAsync(command.CommandId, command.EdgeId, binding);
        await AcknowledgeAttachAsync(fence, command, binding, begun.Revision);

        var releasedWithWrongOutcome = await parent.ReleaseChildAttachBindingAsync(
            new ReleaseChildAttachBindingCommand(binding, "wrong-outcome"));
        Assert.Equal(SessionTreeBindingReleaseState.Released, releasedWithWrongOutcome.State);

        var committed = await fence.CommitFinalizeAsync(
            command.CommandId,
            command.EdgeId,
            begun.Revision);

        Assert.True(committed.ReconciliationRequired);
        Assert.NotEqual(LinkReservationState.Attached, committed.State);
        var state = await fence.GetAsync();
        Assert.True(state.ReconciliationRequired);
        Assert.Equal(begun.Revision, state.GraphRevision);
        Assert.Equal(LinkReservationState.Attached, Assert.Single(state.Reservations!).State);
        Assert.NotNull(state.ReleaseObligation);
        Assert.Equal("attached", state.ReleaseObligation!.Outcome);
    }

    [Fact]
    public async Task ReservationReplayWithDifferentBodyIsConflict_AndRejectedReplayIsStable()
    {
        var projectId = $"tree-fence-replay-{Guid.NewGuid():N}";
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var parentId = $"parent-{projectId}";
        await OpenParentAsync(projectId, parentId);
        var command = Command(projectId, "edge-replay", "command-replay", parentId);
        await fence.ReserveAsync(command);

        var conflict = await fence.ReserveAsync(command with { ParentSessionId = "other-parent" });
        Assert.True(conflict.ReconciliationRequired);
        Assert.Equal("reconciliation_required", conflict.RejectionReason);

        var rejected = await fence.RejectAsync(command.CommandId, command.EdgeId, "parent_binding_changed");
        Assert.True(rejected.ReconciliationRequired);
        var replay = await fence.ReserveAsync(command);
        Assert.True(replay.ReconciliationRequired);
        Assert.Equal(0, (await fence.GetAsync()).GraphRevision);
    }

    [Fact]
    public async Task ProjectionInconsistentStopMaterialization_PersistsReconciliationAndFailsClosed()
    {
        var projectId = $"tree-fence-inconsistent-{Guid.NewGuid():N}";
        var rootId = $"tree-fence-inconsistent-root-{Guid.NewGuid():N}";
        var childId = $"tree-fence-inconsistent-child-{Guid.NewGuid():N}";
        await OpenParentAsync(projectId, rootId);
        await OpenParentAsync(projectId, childId);
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var child = _fixture.Grains.GetGrain<IAgentSessionGrain>(childId);
        await AttachChildAsync(fence, child, projectId, rootId, childId, "edge-inconsistent");
        var reserved = await fence.ReserveAsync(Command(projectId, "edge-reserve", "command-reserve", rootId));
        Assert.Equal(LinkReservationState.Reserved, reserved.State);

        await CorruptChildLinkAsync(childId);

        var command = new BeginSessionTreeStopSnapshotCommand(
            projectId,
            rootId,
            "operation-inconsistent",
            "stop-input:inconsistent",
            "fingerprint:inconsistent");
        var blocked = await fence.BeginStopSnapshotAsync(command);
        Assert.Equal(SessionTreeStopSnapshotDisposition.Blocked, blocked.Disposition);
        Assert.Equal("session_tree_reconciliation_required", blocked.RejectionReason);

        var state = await fence.GetAsync();
        Assert.True(state.ReconciliationRequired);
        Assert.Equal("session_tree_reconciliation_required", state.ReconciliationReason);
        Assert.False(state.ActiveTreeStop);
        var materializing = Assert.Single(state.StopSnapshots!);
        Assert.Equal(SessionTreeStopSnapshotPhase.Materializing, materializing.Phase);
        Assert.Equal(command.OperationId, materializing.OperationId);
        Assert.Equal(command.RootSessionId, materializing.RootSessionId);
        Assert.Equal(1, materializing.GraphRevision);
        Assert.Equal(materializing.GraphRevision, state.GraphRevision);
        Assert.Empty(materializing.Membership);
        Assert.Empty(materializing.Targets);

        var replay = await fence.BeginStopSnapshotAsync(command);
        Assert.Equal(SessionTreeStopSnapshotDisposition.Blocked, replay.Disposition);
        Assert.Equal("session_tree_reconciliation_required", replay.RejectionReason);
        var otherStop = await fence.BeginStopSnapshotAsync(new BeginSessionTreeStopSnapshotCommand(
            projectId,
            rootId,
            "operation-inconsistent-other",
            "stop-input:inconsistent-other",
            "fingerprint:inconsistent-other"));
        Assert.Equal(SessionTreeStopSnapshotDisposition.Blocked, otherStop.Disposition);
        Assert.Equal("session_tree_reconciliation_required", otherStop.RejectionReason);

        var admission = await fence.SetStopAdmissionAsync(
            command.OperationId,
            SessionTreeStopAdmissionOutcome.Running);
        Assert.False(admission.Active);
        Assert.Equal("session_tree_reconciliation_required", admission.RejectionReason);

        var reserve = await fence.ReserveAsync(Command(projectId, "edge-after", "command-after", "other-parent"));
        Assert.True(reserve.ReconciliationRequired);
        Assert.Equal("reconciliation_required", reserve.RejectionReason);
        var finalize = await fence.BeginFinalizeAsync(
            "command-reserve",
            "edge-reserve",
            new SessionTreeBindingUseReceipt(
                "receipt-reserve",
                projectId,
                "command-reserve",
                "edge-reserve",
                rootId,
                "/workspace",
                "runner",
                "opencode",
                "runtime-session",
                1,
                ParentAgentId: "parent-agent"));
        Assert.True(finalize.ReconciliationRequired);
        Assert.Equal("reconciliation_required", finalize.RejectionReason);
        var detach = await fence.BeginDetachAsync(new BeginSessionTreeDetachCommand(
            projectId,
            "edge-reserve",
            rootId,
            "child-reserve",
            "command-detach-reserve",
            "job-detach-reserve",
            1));
        Assert.Equal(SessionTreeDetachMutationState.ReconciliationRequired, detach.State);
        Assert.Equal("reconciliation_required", detach.RejectionReason);

        var final = await fence.GetAsync();
        Assert.True(final.ReconciliationRequired);
        Assert.Equal(1, final.GraphRevision);
        Assert.Equal(
            SessionTreeStopSnapshotPhase.Materializing,
            Assert.Single(final.StopSnapshots!).Phase);
    }

    private async Task AttachChildAsync(
        ISessionTreeMutationFenceGrain fence,
        IAgentSessionGrain child,
        string projectId,
        string rootId,
        string childId,
        string edgeId)
    {
        var command = Command(projectId, edgeId, $"command-{edgeId}", rootId) with { ChildSessionId = childId };
        Assert.Equal(LinkReservationState.Reserved, (await fence.ReserveAsync(command)).State);
        var binding = await AcquireAsync(rootId, projectId, command);
        var begun = await fence.BeginFinalizeAsync(command.CommandId, command.EdgeId, binding);
        var attached = await child.ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
            command.CommandId,
            command.EdgeId,
            command.ParentSessionId,
            command.ParentAgentId!,
            command.ChildLaunchJobId!,
            begun.Revision,
            command.ExpectedWorkDir,
            command.ExpectedRunnerId,
            command.ExpectedRuntime,
            command.ExpectedRuntimeSessionId,
            projectId,
            binding.BindingEpoch,
            binding.ReceiptId,
            SessionTreeExpectedLinkState.Absent));
        Assert.Equal(SessionTreeAttachMutationState.Attached, attached.State);
        var acknowledged = await fence.AcknowledgeFinalizeAsync(attached.Receipt!);
        Assert.False(acknowledged.ReconciliationRequired);
        var committed = await fence.CommitFinalizeAsync(command.CommandId, command.EdgeId, begun.Revision);
        Assert.Equal(LinkReservationState.Attached, committed.State);
    }

    private async Task CorruptChildLinkAsync(string childId)
    {
        var factory = _fixture.Cluster.GetSiloServiceProvider(null)
            .GetRequiredService<IDbContextFactory<MohistDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.AgentSessions.SingleAsync(item => item.Id == childId);
        row.ParentLinkDetachedRevision = row.ParentLinkAttachedRevision;
        await db.SaveChangesAsync();
    }

    private async Task AcknowledgeAttachAsync(
        ISessionTreeMutationFenceGrain fence,
        ReserveSessionTreeLinkCommand command,
        SessionTreeBindingUseReceipt binding,
        long revision)
    {
        var result = await fence.AcknowledgeFinalizeAsync(new SessionTreeAttachReceipt(
            command.CommandId,
            command.EdgeId,
            command.ParentSessionId,
            command.ChildSessionId,
            command.ChildLaunchJobId!,
            revision,
            binding.ProjectId,
            SessionTreeMutationKind.Attach,
            binding.ParentWorkDir,
            binding.RunnerId,
            binding.Runtime,
            binding.RuntimeSessionId,
            binding.BindingEpoch,
            binding.ReceiptId,
            SessionTreeExpectedLinkState.Absent,
            command.ParentAgentId!));
        Assert.False(result.ReconciliationRequired);
    }

    private async Task<SessionTreeBindingUseReceipt> AcquireAsync(
        string parentId,
        string projectId,
        ReserveSessionTreeLinkCommand command)
    {
        var result = await _fixture.Grains.GetGrain<IAgentSessionGrain>(parentId)
            .AcquireChildAttachBindingAsync(new AcquireChildAttachBindingCommand(
                projectId,
                command.CommandId,
                command.EdgeId,
                parentId,
                command.ExpectedWorkDir,
                command.ExpectedRunnerId,
                command.ExpectedRuntime,
                command.ExpectedRuntimeSessionId,
                command.ExpectedBindingEpoch!.Value,
                command.ParentAgentId!));
        Assert.NotNull(result.Receipt);
        return result.Receipt!;
    }

    private async Task<IAgentSessionGrain> OpenParentAsync(string projectId, string parentId)
    {
        var parent = _fixture.Grains.GetGrain<IAgentSessionGrain>(parentId);
        await parent.OpenAsync(new OpenAgentSessionCommand(
            "runner",
            "opencode",
            "/workspace",
            Metadata: new AgentSessionMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentSessionQueryMetadataKeys.ProjectId] = projectId,
                [AgentSessionQueryMetadataKeys.SourceKind] = "agent-launch",
                [GenericAgentSessionMetadata.AgentId] = "parent-agent",
                [GenericAgentSessionMetadata.AgentName] = "parent-agent",
            })));
        await parent.AttachPhysicalSessionAsync(new AttachPhysicalSessionCommand(
            "runtime-session",
            ExpectedRunnerId: "runner",
            ExpectedRuntime: "opencode"));
        return parent;
    }

    private static ReserveSessionTreeLinkCommand Command(
        string projectId,
        string edgeId,
        string commandId,
        string parentSessionId) =>
        new(
            projectId,
            edgeId,
            parentSessionId,
            $"child-{edgeId}",
            "/workspace",
            "runner",
            "opencode",
            "runtime-session",
            commandId,
            $"job-{edgeId}",
            "parent-agent",
            1,
            SessionTreeExpectedLinkState.Absent);
}
