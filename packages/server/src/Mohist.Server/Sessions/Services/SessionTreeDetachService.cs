using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Sessions.Services;

public enum SessionTreeDetachResultState
{
    Detached,
    ReconciliationRequired,
}

public sealed record SessionTreeDetachResult(
    SessionTreeDetachResultState State,
    string ChildSessionId,
    string ParentSessionId,
    string EdgeId,
    string ChildLaunchJobId,
    long AttachedRevision,
    long DetachedRevision,
    bool Historic,
    string? Reason = null);

public sealed class SessionTreeDetachService(
    IGrainFactory grains,
    ISessionTreeMutationFenceReadPort readPort) : IScopedService
{
    public async Task<SessionTreeDetachResult?> DetachAsync(
        string projectId,
        string childSessionId,
        CancellationToken cancellationToken = default)
    {
        var fact = await readPort.ReadLinkAsync(projectId, childSessionId, cancellationToken);
        if (fact is null)
            return null;

        var link = fact.Link;
        var fence = grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId);
        var commandId = SessionTreeStopOperationIds.ForDetach(
            projectId,
            childSessionId,
            link.AttachedRevision);
        if (link.State == SessionParentLinkState.Detached)
            return HistoricResult(childSessionId, link, await fence.GetAsync());

        var begun = await fence.BeginDetachAsync(new BeginSessionTreeDetachCommand(
            projectId,
            link.EdgeId,
            link.ParentSessionId,
            childSessionId,
            commandId,
            link.ChildLaunchJobId,
            link.AttachedRevision));
        if (begun.State == SessionTreeDetachMutationState.ReconciliationRequired)
            return ReconciliationResult(childSessionId, link, begun.RejectionReason);
        if (begun.State == SessionTreeDetachMutationState.Rejected)
            return ReconciliationResult(childSessionId, link, begun.RejectionReason);
        if (begun.State == SessionTreeDetachMutationState.Detached)
            return HistoricResult(childSessionId, link, await fence.GetAsync());

        var applied = await grains.GetGrain<IAgentSessionGrain>(childSessionId).ApplyParentLinkDetachAsync(
            new ApplyParentLinkDetachCommand(
                link.EdgeId,
                link.ParentSessionId,
                link.ChildLaunchJobId,
                begun.Revision,
                commandId,
                childSessionId,
                link.AttachedRevision));
        if (applied.State is not SessionTreeDetachMutationState.Detached || applied.Receipt is null)
            return ReconciliationResult(childSessionId, link, applied.RejectionReason ?? "child_detach_not_applied");

        var acknowledged = await fence.AcknowledgeDetachAsync(applied.Receipt);
        if (acknowledged.State is not (SessionTreeDetachMutationState.Acknowledged or SessionTreeDetachMutationState.Detached))
            return ReconciliationResult(childSessionId, link, acknowledged.RejectionReason ?? "detach_acknowledgement_rejected");

        var committed = await fence.CommitDetachAsync(commandId, link.EdgeId, begun.Revision);
        if (committed.State != SessionTreeDetachMutationState.Detached)
            return ReconciliationResult(childSessionId, link, committed.RejectionReason ?? "detach_commit_rejected");

        return new SessionTreeDetachResult(
            SessionTreeDetachResultState.Detached,
            childSessionId,
            link.ParentSessionId,
            link.EdgeId,
            link.ChildLaunchJobId,
            link.AttachedRevision,
            begun.Revision,
            Historic: false);
    }

    private static SessionTreeDetachResult HistoricResult(
        string childSessionId,
        SessionParentLink link,
        SessionTreeMutationFence fence)
    {
        var receipt = fence.DetachReceipts?
            .FirstOrDefault(item => item.EdgeId == link.EdgeId);
        if (receipt is null
            || receipt.ChildSessionId != childSessionId
            || receipt.ParentSessionId != link.ParentSessionId
            || receipt.ChildLaunchJobId != link.ChildLaunchJobId
            || receipt.ExpectedAttachedRevision != link.AttachedRevision)
        {
            return ReconciliationResult(childSessionId, link, "historic_detach_tuple_missing");
        }

        return new SessionTreeDetachResult(
            SessionTreeDetachResultState.Detached,
            childSessionId,
            receipt.ParentSessionId,
            receipt.EdgeId,
            receipt.ChildLaunchJobId,
            receipt.ExpectedAttachedRevision,
            receipt.Revision,
            Historic: true);
    }

    private static SessionTreeDetachResult ReconciliationResult(
        string childSessionId,
        SessionParentLink link,
        string? reason) => new(
        SessionTreeDetachResultState.ReconciliationRequired,
        childSessionId,
        link.ParentSessionId,
        link.EdgeId,
        link.ChildLaunchJobId,
        link.AttachedRevision,
        link.DetachedRevision ?? 0,
        Historic: link.State == SessionParentLinkState.Detached,
        reason);
}
