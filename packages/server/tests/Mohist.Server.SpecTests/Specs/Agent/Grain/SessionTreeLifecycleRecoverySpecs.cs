using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
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
            "runtime-session",
            "command-crash-window",
            "job-crash-window",
            "root-agent",
            1,
            SessionTreeExpectedLinkState.Absent);

        await fence.ReserveAsync(command);
        var binding = await _fixture.Grains.GetGrain<IAgentSessionGrain>(parentId)
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
        Assert.NotNull(binding.Receipt);
        var bindingReceipt = binding.Receipt!;
        var attachBegin = await fence.BeginFinalizeAsync(command.CommandId, command.EdgeId, bindingReceipt);
        Assert.Equal(1, attachBegin.Revision);
        Assert.Equal(attachBegin, await fence.BeginFinalizeAsync(command.CommandId, command.EdgeId, bindingReceipt));
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
            command.ExpectedRuntimeSessionId,
            projectId,
            bindingReceipt.BindingEpoch,
            bindingReceipt.ReceiptId,
            SessionTreeExpectedLinkState.Absent));
        Assert.Equal(SessionTreeAttachMutationState.Attached, attachApplied.State);
        var attachAck = await fence.AcknowledgeFinalizeAsync(attachApplied.Receipt!);
        Assert.True(
            !attachAck.ReconciliationRequired && attachAck.State == LinkReservationState.Reserved,
            $"state={attachAck.State}; reason={attachAck.RejectionReason}; reconciliation={attachAck.ReconciliationRequired}");
        Assert.Equal(attachAck, await fence.AcknowledgeFinalizeAsync(attachApplied.Receipt!));
        Assert.Equal(0, (await fence.GetAsync()).GraphRevision);
        var attachCommitted = await fence.CommitFinalizeAsync(
            command.CommandId,
            command.EdgeId,
            attachBegin.Revision);
        Assert.True(
            attachCommitted.State == LinkReservationState.Attached,
            $"state={attachCommitted.State}; reason={attachCommitted.RejectionReason}; reconciliation={attachCommitted.ReconciliationRequired}");
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

    [Fact]
    public async Task ChildWrittenAttachTupleMismatchRequiresReconciliation()
    {
        var projectId = $"tree-attach-mismatch-{Guid.NewGuid():N}";
        var parentId = $"tree-attach-parent-{Guid.NewGuid():N}";
        var childId = $"tree-attach-child-{Guid.NewGuid():N}";
        await OpenSessionAsync(projectId, parentId, "root-agent");
        var child = await OpenSessionAsync(projectId, childId, "child-agent");
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var command = new ReserveSessionTreeLinkCommand(
            projectId,
            "edge-attach-mismatch",
            parentId,
            childId,
            "/workspace",
            "runner-1",
            "opencode",
            "runtime-session",
            "command-attach-mismatch",
            "job-attach-mismatch",
            "root-agent",
            1,
            SessionTreeExpectedLinkState.Absent);

        await fence.ReserveAsync(command);
        var binding = (await _fixture.Grains.GetGrain<IAgentSessionGrain>(parentId)
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
                command.ParentAgentId!))).Receipt!;
        var begun = await fence.BeginFinalizeAsync(command.CommandId, command.EdgeId, binding);
        var apply = new ApplyParentLinkAttachCommand(
            command.CommandId,
            command.EdgeId,
            parentId,
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
            SessionTreeExpectedLinkState.Absent);
        var attached = await child.ApplyParentLinkAttachAsync(apply);
        Assert.Equal(SessionTreeAttachMutationState.Attached, attached.State);
        var childMismatch = await child.ApplyParentLinkAttachAsync(apply with { ProjectId = "wrong-project" });
        Assert.Equal(SessionTreeAttachMutationState.ReconciliationRequired, childMismatch.State);
        Assert.Equal(attached, await child.ApplyParentLinkAttachAsync(apply));

        var fenceMismatch = await fence.AcknowledgeFinalizeAsync(
            attached.Receipt! with { ProjectId = "wrong-project" });
        Assert.True(fenceMismatch.ReconciliationRequired);
        Assert.True((await fence.GetAsync()).ReconciliationRequired);
    }

    [Fact]
    public async Task FixedReminderRecoveryAfterBeginConvergesExactlyOnce()
    {
        var setup = await CreateDetachCaseAsync("after-begin");
        var begun = await setup.Fence.BeginDetachAsync(setup.Command);

        await setup.Fence.RunRecoveryTickAsync();
        Assert.Equal(2, (await setup.Fence.GetAsync()).GraphRevision);
        Assert.Equal(SessionTreeDetachMutationState.Detached, (await setup.Fence.BeginDetachAsync(setup.Command)).State);

        await setup.Fence.RunRecoveryTickAsync();
        Assert.Equal(2, (await setup.Fence.GetAsync()).GraphRevision);
        Assert.Equal(begun.Revision, (await setup.Fence.BeginDetachAsync(setup.Command)).Revision);
    }

    [Fact]
    public async Task FixedReminderRecoveryAfterChildApplyConvergesExactlyOnce()
    {
        var setup = await CreateDetachCaseAsync("after-child-apply");
        var begun = await setup.Fence.BeginDetachAsync(setup.Command);
        var applied = await setup.Child.ApplyParentLinkDetachAsync(
            DetachCommand(
                setup.Command.CommandId,
                setup.Command.EdgeId,
                setup.Command.ChildSessionId,
                setup.Command.ParentSessionId,
                setup.Command.ChildLaunchJobId,
                begun.Revision));

        await setup.Fence.RunRecoveryTickAsync();
        Assert.Equal(SessionTreeDetachMutationState.Detached, (await setup.Fence.BeginDetachAsync(setup.Command)).State);
        Assert.Equal(2, (await setup.Fence.GetAsync()).GraphRevision);
        Assert.Equal(applied, await setup.Child.ApplyParentLinkDetachAsync(
            DetachCommand(
                setup.Command.CommandId,
                setup.Command.EdgeId,
                setup.Command.ChildSessionId,
                setup.Command.ParentSessionId,
                setup.Command.ChildLaunchJobId,
                begun.Revision)));
        await setup.Fence.RunRecoveryTickAsync();
        Assert.Equal(2, (await setup.Fence.GetAsync()).GraphRevision);
    }

    [Fact]
    public async Task FixedReminderRecoveryAfterAckConvergesExactlyOnce()
    {
        var setup = await CreateDetachCaseAsync("after-ack");
        var begun = await setup.Fence.BeginDetachAsync(setup.Command);
        var applied = await setup.Child.ApplyParentLinkDetachAsync(
            DetachCommand(
                setup.Command.CommandId,
                setup.Command.EdgeId,
                setup.Command.ChildSessionId,
                setup.Command.ParentSessionId,
                setup.Command.ChildLaunchJobId,
                begun.Revision));
        Assert.Equal(SessionTreeDetachMutationState.Acknowledged,
            (await setup.Fence.AcknowledgeDetachAsync(applied.Receipt!)).State);

        await setup.Fence.RunRecoveryTickAsync();
        Assert.Equal(SessionTreeDetachMutationState.Detached, (await setup.Fence.BeginDetachAsync(setup.Command)).State);
        Assert.Equal(2, (await setup.Fence.GetAsync()).GraphRevision);
        await setup.Fence.RunRecoveryTickAsync();
        Assert.Equal(2, (await setup.Fence.GetAsync()).GraphRevision);
    }

    [Fact]
    public async Task FixedReminderRecoveryAfterCommitIsHistoricalReplayOnly()
    {
        var setup = await CreateDetachCaseAsync("after-commit");
        var begun = await setup.Fence.BeginDetachAsync(setup.Command);
        var applied = await setup.Child.ApplyParentLinkDetachAsync(
            DetachCommand(
                setup.Command.CommandId,
                setup.Command.EdgeId,
                setup.Command.ChildSessionId,
                setup.Command.ParentSessionId,
                setup.Command.ChildLaunchJobId,
                begun.Revision));
        await setup.Fence.AcknowledgeDetachAsync(applied.Receipt!);
        Assert.Equal(SessionTreeDetachMutationState.Detached,
            (await setup.Fence.CommitDetachAsync(setup.Command.CommandId, setup.Command.EdgeId, begun.Revision)).State);

        await setup.Fence.RunRecoveryTickAsync();
        Assert.Equal(SessionTreeDetachMutationState.Detached, (await setup.Fence.BeginDetachAsync(setup.Command)).State);
        Assert.Equal(2, (await setup.Fence.GetAsync()).GraphRevision);
        Assert.Equal(
            SessionTreeDetachMutationState.Detached,
            (await setup.Fence.CommitDetachAsync(setup.Command.CommandId, setup.Command.EdgeId, begun.Revision)).State);
        await setup.Fence.RunRecoveryTickAsync();
        Assert.Equal(2, (await setup.Fence.GetAsync()).GraphRevision);
    }

    [Fact]
    public async Task CommitAfterReleaseExceptionLeavesExactReceiptForReminderRecovery()
    {
        var projectId = $"tree-release-recovery-{Guid.NewGuid():N}";
        var parentId = $"tree-release-parent-{Guid.NewGuid():N}";
        var childId = $"tree-release-child-{Guid.NewGuid():N}";
        var parent = await OpenSessionAsync(projectId, parentId, "root-agent");
        var child = await OpenSessionAsync(projectId, childId, "child-agent");
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var command = new ReserveSessionTreeLinkCommand(
            projectId,
            "edge-release-recovery",
            parentId,
            childId,
            "/workspace",
            "runner-1",
            "opencode",
            "runtime-session",
            "command-release-recovery",
            "job-release-recovery",
            "root-agent",
            1,
            SessionTreeExpectedLinkState.Absent);

        await fence.ReserveAsync(command);
        var binding = (await parent.AcquireChildAttachBindingAsync(
            new AcquireChildAttachBindingCommand(
                projectId,
                command.CommandId,
                command.EdgeId,
                parentId,
                command.ExpectedWorkDir,
                command.ExpectedRunnerId,
                command.ExpectedRuntime,
                command.ExpectedRuntimeSessionId,
                command.ExpectedBindingEpoch!.Value,
                command.ParentAgentId!))).Receipt!;
        var begun = await fence.BeginFinalizeAsync(command.CommandId, command.EdgeId, binding);
        var attached = await child.ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
            command.CommandId,
            command.EdgeId,
            parentId,
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
        await fence.AcknowledgeFinalizeAsync(attached.Receipt!);

        var store = _fixture.Cluster
            .GetSiloServiceProvider(null)
            .GetRequiredService<IAgentSessionStore>();
        var parentSnapshot = await store.LoadAsync(parentId);
        Assert.NotNull(parentSnapshot);
        await parent.DeactivateAndWait(_fixture.Grains);
        await store.DeleteAsync(parentId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fence.CommitFinalizeAsync(
            command.CommandId,
            command.EdgeId,
            begun.Revision));

        var afterException = await fence.GetAsync();
        Assert.Equal(begun.Revision, afterException.GraphRevision);
        Assert.Equal(LinkReservationState.Attached, Assert.Single(afterException.Reservations!).State);
        Assert.NotNull(afterException.ReleaseObligation);
        Assert.Equal(attached.Receipt, afterException.ReleaseObligation!.Receipt);
        Assert.Equal("attached", afterException.ReleaseObligation.Outcome);
        var blockedDetach = await fence.BeginDetachAsync(new BeginSessionTreeDetachCommand(
            projectId,
            command.EdgeId,
            parentId,
            childId,
            "command-release-recovery-detach",
            command.ChildLaunchJobId!,
            begun.Revision));
        Assert.Equal(SessionTreeDetachMutationState.ReconciliationRequired, blockedDetach.State);
        Assert.Equal(begun.Revision, blockedDetach.Revision);

        await store.SaveAsync(parentId, parentSnapshot!);
        await fence.RunRecoveryTickAsync();

        var releasedByRecovery = await parent.ReleaseChildAttachBindingAsync(
            new ReleaseChildAttachBindingCommand(binding, "attached"));
        Assert.Equal(SessionTreeBindingReleaseState.AlreadyReleased, releasedByRecovery.State);
        var afterRecovery = await fence.GetAsync();
        Assert.Equal(afterException.GraphRevision, afterRecovery.GraphRevision);
        Assert.Equal(afterException.Reservations, afterRecovery.Reservations);
        Assert.Null(afterRecovery.ReleaseObligation);
    }

    private async Task<(
        ISessionTreeMutationFenceGrain Fence,
        IAgentSessionGrain Child,
        BeginSessionTreeDetachCommand Command)> CreateDetachCaseAsync(string suffix)
    {
        var projectId = $"tree-reminder-{suffix}-{Guid.NewGuid():N}";
        var parentId = $"tree-reminder-parent-{suffix}-{Guid.NewGuid():N}";
        var childId = $"tree-reminder-child-{suffix}-{Guid.NewGuid():N}";
        await OpenSessionAsync(projectId, parentId, "root-agent");
        var child = await OpenSessionAsync(projectId, childId, "child-agent");
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var attach = new ReserveSessionTreeLinkCommand(
            projectId,
            $"edge-reminder-{suffix}",
            parentId,
            childId,
            "/workspace",
            "runner-1",
            "opencode",
            "runtime-session",
            $"command-reminder-attach-{suffix}",
            $"job-reminder-{suffix}",
            "root-agent",
            1,
            SessionTreeExpectedLinkState.Absent);
        await fence.ReserveAsync(attach);
        var binding = (await _fixture.Grains.GetGrain<IAgentSessionGrain>(parentId)
            .AcquireChildAttachBindingAsync(new AcquireChildAttachBindingCommand(
                projectId,
                attach.CommandId,
                attach.EdgeId,
                parentId,
                attach.ExpectedWorkDir,
                attach.ExpectedRunnerId,
                attach.ExpectedRuntime,
                attach.ExpectedRuntimeSessionId,
                attach.ExpectedBindingEpoch!.Value,
                attach.ParentAgentId!))).Receipt!;
        var attachBegin = await fence.BeginFinalizeAsync(attach.CommandId, attach.EdgeId, binding);
        var attached = await child.ApplyParentLinkAttachAsync(new ApplyParentLinkAttachCommand(
            attach.CommandId,
            attach.EdgeId,
            parentId,
            attach.ParentAgentId!,
            attach.ChildLaunchJobId!,
            attachBegin.Revision,
            attach.ExpectedWorkDir,
            attach.ExpectedRunnerId,
            attach.ExpectedRuntime,
            attach.ExpectedRuntimeSessionId,
            projectId,
            binding.BindingEpoch,
            binding.ReceiptId,
            SessionTreeExpectedLinkState.Absent));
        await fence.AcknowledgeFinalizeAsync(attached.Receipt!);
        Assert.Equal(LinkReservationState.Attached,
            (await fence.CommitFinalizeAsync(attach.CommandId, attach.EdgeId, attachBegin.Revision)).State);

        return (fence, child, new BeginSessionTreeDetachCommand(
            projectId,
            attach.EdgeId,
            parentId,
            childId,
            $"command-reminder-detach-{suffix}",
            attach.ChildLaunchJobId!,
            attachBegin.Revision));
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

    private static ApplyParentLinkDetachCommand DetachCommand(
        string commandId,
        string edgeId,
        string childSessionId,
        string parentSessionId,
        string jobId,
        long revision) =>
        new(edgeId, parentSessionId, jobId, revision, commandId, childSessionId, revision - 1);
}
