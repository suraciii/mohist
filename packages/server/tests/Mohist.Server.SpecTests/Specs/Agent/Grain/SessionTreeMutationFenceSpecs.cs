using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
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

        var first = await fence.ReserveAsync(Command(projectId, "edge-1", "command-1"));
        var second = await fence.ReserveAsync(Command(projectId, "edge-2", "command-2"));

        Assert.Equal(0, first.Revision);
        Assert.Equal(0, second.Revision);
        var reserved = await fence.GetAsync();
        Assert.Equal(2, reserved.Reservations!.Count);
        Assert.Equal(0, reserved.GraphRevision);

        var assignedFirst = await fence.BeginFinalizeAsync("command-1", "edge-1");
        var busySecond = await fence.BeginFinalizeAsync("command-2", "edge-2");
        Assert.Equal(1, assignedFirst.Revision);
        Assert.Equal("finalize_busy", busySecond.RejectionReason);
        Assert.Equal(LinkReservationState.Reserved, busySecond.State);

        var replayedFirst = await fence.BeginFinalizeAsync("command-1", "edge-1");
        Assert.Equal(assignedFirst.Revision, replayedFirst.Revision);

        var attachedFirst = await fence.CommitFinalizeAsync("command-1", "edge-1");
        Assert.Equal(1, attachedFirst.Revision);
        var assignedSecond = await fence.BeginFinalizeAsync("command-2", "edge-2");
        Assert.Equal(2, assignedSecond.Revision);
        var attachedSecond = await fence.CommitFinalizeAsync("command-2", "edge-2");
        Assert.Equal(2, attachedSecond.Revision);

        var final = await fence.GetAsync();
        Assert.Equal(2, final.GraphRevision);
        Assert.Equal(
            new[] { 1L, 2L },
            final.Reservations!.OrderBy(item => item.EdgeId).Select(item => item.AttachedRevision!.Value));
    }

    [Fact]
    public async Task ReservationDoesNotMoveRevision_AndFutureLinkIsHiddenUntilCommit()
    {
        var projectId = $"tree-cursor-{Guid.NewGuid():N}";
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);

        await fence.ReserveAsync(Command(projectId, "edge-1", "command-1"));
        var beforeQuery = await fence.GetAsync();
        Assert.Equal(0, beforeQuery.GraphRevision);
        Assert.Null(beforeQuery.Reservations!.Single().AttachedRevision);

        var assigned = await fence.BeginFinalizeAsync("command-1", "edge-1");
        Assert.Equal(1, assigned.Revision);
        var duringQuery = await fence.GetAsync();
        Assert.Equal(0, duringQuery.GraphRevision);
        Assert.Null(duringQuery.Reservations!.Single().AttachedRevision);
        Assert.Equal(1, duringQuery.PendingMutations!.Single().AssignedRevision);

        var committed = await fence.CommitFinalizeAsync("command-1", "edge-1");
        Assert.Equal(1, committed.Revision);
        var afterQuery = await fence.GetAsync();
        Assert.Equal(1, afterQuery.GraphRevision);
        Assert.Equal(1, afterQuery.Reservations!.Single().AttachedRevision);
    }

    [Fact]
    public async Task FinalizeRecoveryRequiresTheReservedCommandAndRejectsAbortedOrUnknownEdges()
    {
        var projectId = $"tree-fence-validation-{Guid.NewGuid():N}";
        var fence = _fixture.Grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);

        await fence.ReserveAsync(Command(projectId, "edge-attached", "command-attached"));
        var assigned = await fence.BeginFinalizeAsync("command-attached", "edge-attached");
        await fence.CommitFinalizeAsync("command-attached", "edge-attached");

        var mismatchedBegin = await fence.BeginFinalizeAsync("command-other", "edge-attached");
        Assert.Equal(LinkReservationState.Rejected, mismatchedBegin.State);
        Assert.Equal("parent_tree_link_command_mismatch", mismatchedBegin.RejectionReason);
        var mismatchedCommit = await fence.CommitFinalizeAsync("command-other", "edge-attached");
        Assert.Equal(LinkReservationState.Rejected, mismatchedCommit.State);
        Assert.Equal("parent_tree_link_command_mismatch", mismatchedCommit.RejectionReason);

        await fence.ReserveAsync(Command(projectId, "edge-aborted", "command-aborted"));
        await fence.RejectAsync("command-aborted", "edge-aborted", "parent_binding_changed");
        var aborted = await fence.BeginFinalizeAsync("command-aborted", "edge-aborted");
        Assert.Equal(LinkReservationState.Rejected, aborted.State);
        Assert.Equal("parent_binding_changed", aborted.RejectionReason);

        var unknown = await fence.BeginFinalizeAsync("command-unknown", "edge-unknown");
        Assert.Equal(LinkReservationState.Rejected, unknown.State);
        Assert.Equal("parent_tree_link_not_reserved", unknown.RejectionReason);
        Assert.Equal(assigned.Revision, (await fence.GetAsync()).Reservations!.Single(item => item.EdgeId == "edge-attached").AttachedRevision);
    }

    private static ReserveSessionTreeLinkCommand Command(string projectId, string edgeId, string commandId) =>
        new(
            ProjectId: projectId,
            EdgeId: edgeId,
            ParentSessionId: "parent",
            ChildSessionId: $"child-{edgeId}",
            ExpectedWorkDir: "/workspace",
            ExpectedRunnerId: "runner",
            ExpectedRuntime: "opencode",
            ExpectedRuntimeSessionId: "runtime-session",
            CommandId: commandId);
}
