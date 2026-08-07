using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

internal static class SessionTreeTopology
{
    public static AgentSessionRecord ReadCandidate(string projectId, AgentSessionRow row)
    {
        var session = AgentSessionJson.Deserialize(row)
            ?? throw Inconsistent("invalid durable session state");
        if (string.IsNullOrWhiteSpace(row.Id)
            || !string.Equals(row.LabelProjectId, projectId, StringComparison.Ordinal))
            throw Inconsistent("cross-project or empty session identity");

        ValidateParentTuple(row, session);
        return new AgentSessionRecord(
            row,
            session,
            session.Metadata.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public static bool IsVisibleAt(AgentSessionRow row, long revision, bool asRoot)
    {
        if (row.ParentSessionId is null)
            return row.LaunchVisibility is null or "visible";
        if (asRoot && row.ParentLinkDetachedRevision <= revision)
            return row.LaunchVisibility is null or "visible" or "provisional";
        return row.ParentLinkAttachedRevision <= revision
            && (row.ParentLinkDetachedRevision is null
                || row.ParentLinkDetachedRevision > revision);
    }

    private static void ValidateParentTuple(AgentSessionRow row, AgentSession session)
    {
        if (row.ParentSessionId is null)
        {
            if (row.ParentLinkEdgeId is not null
                || row.ChildLaunchJobId is not null
                || row.ParentLinkState is not null
                || row.ParentLinkAttachedRevision is not null
                || row.ParentLinkDetachedRevision is not null
                || session.ParentLink is not null)
                throw Inconsistent("root session has a partial parent tuple");
            return;
        }

        if (row.ParentSessionId == row.Id
            || string.IsNullOrWhiteSpace(row.ParentLinkEdgeId)
            || string.IsNullOrWhiteSpace(row.ChildLaunchJobId)
            || row.ParentLinkAttachedRevision is null
            || row.ParentLinkAttachedRevision <= 0
            || row.ParentLinkState is not ("attached" or "detached")
            || session.ParentLink is null)
            throw Inconsistent("child session has an invalid parent tuple");

        if (row.ParentLinkDetachedRevision is { } detachedRevision
            && (detachedRevision <= 0 || detachedRevision <= row.ParentLinkAttachedRevision.Value))
            throw Inconsistent("child session has an invalid detach revision");

        var link = session.ParentLink;
        if (link.EdgeId != row.ParentLinkEdgeId
            || link.ParentSessionId != row.ParentSessionId
            || link.ParentAgentId != row.ParentAgentId
            || link.ChildLaunchJobId != row.ChildLaunchJobId
            || link.AttachedRevision != row.ParentLinkAttachedRevision
            || link.DetachedRevision != row.ParentLinkDetachedRevision
            || link.State.ToString().ToLowerInvariant() != row.ParentLinkState)
            throw Inconsistent("child session parent tuple disagrees with durable link");
    }

    private static SessionTreeProjectionInconsistentException Inconsistent(string detail) =>
        new($"session_tree_projection_inconsistent: {detail}");
}

public sealed class SessionTreeProjectionInconsistentException : InvalidOperationException
{
    public SessionTreeProjectionInconsistentException()
        : base("session_tree_projection_inconsistent")
    {
    }

    public SessionTreeProjectionInconsistentException(string message)
        : base(message)
    {
    }
}
