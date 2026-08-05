using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Sessions.Services;

public interface ISessionTreeMutationFenceReadPort
{
    Task<SessionTreeMutationFence> GetAsync(string projectId);
    Task<SessionTreeStopSnapshotFacts> ReadAtAsync(
        string projectId,
        string rootSessionId,
        long graphRevision,
        CancellationToken cancellationToken = default);
    Task<SessionTreeSessionBindingFact?> ReadBindingAsync(
        string projectId,
        string sessionId,
        CancellationToken cancellationToken = default);
}

public sealed class SessionTreeMutationFenceReadPort : ISessionTreeMutationFenceReadPort, IScopedService
{
    private readonly IGrainFactory _grains;
    private readonly IDbContextFactory<MohistDbContext>? _dbFactory;

    public SessionTreeMutationFenceReadPort(IGrainFactory grains)
    {
        _grains = grains;
    }

    public SessionTreeMutationFenceReadPort(
        IGrainFactory grains,
        IDbContextFactory<MohistDbContext> dbFactory)
    {
        _grains = grains;
        _dbFactory = dbFactory;
    }

    public Task<SessionTreeMutationFence> GetAsync(string projectId) =>
        _grains.GetGrain<ISessionTreeMutationFenceGrain>(projectId).GetAsync();

    public async Task<SessionTreeStopSnapshotFacts> ReadAtAsync(
        string projectId,
        string rootSessionId,
        long graphRevision,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(rootSessionId))
            throw new ArgumentException("RootSessionId is required.", nameof(rootSessionId));
        if (graphRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(graphRevision));

        if (_dbFactory is null)
            throw new InvalidOperationException("ReadAtAsync requires the persistence-backed fence read port.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var root = await db.AgentSessions.AsNoTracking()
            .Where(row => row.Id == rootSessionId)
            .FirstOrDefaultAsync(cancellationToken);
        if (root is null)
            throw new InvalidOperationException("The requested session tree root is not visible at the fence revision.");

        var records = new List<(AgentSessionRow Row, AgentSession Session, int Depth)>();
        var rootRecord = SessionTreeTopology.ReadCandidate(projectId, root);
        var rootSession = rootRecord.Session;
        if (!SessionTreeTopology.IsVisibleAt(root, graphRevision, asRoot: true))
            throw new InvalidOperationException("The requested session tree root is not visible at the fence revision.");
        records.Add((root, rootSession, 0));

        var visitedSessions = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        var visitedEdges = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new[] { (SessionId: rootSessionId, Depth: 0) };
        while (frontier.Length > 0)
        {
            var parentIds = frontier.Select(item => item.SessionId).ToArray();
            var children = await db.AgentSessions.AsNoTracking()
                .Where(row => row.ParentSessionId != null && parentIds.Contains(row.ParentSessionId))
                .OrderBy(row => row.ParentSessionId)
                .ThenBy(row => row.ParentLinkAttachedRevision)
                .ThenBy(row => row.ParentLinkEdgeId)
                .ToListAsync(cancellationToken);

            var byParent = children
                .GroupBy(row => row.ParentSessionId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var next = new List<(string SessionId, int Depth)>();
            foreach (var parent in frontier)
            {
                if (!byParent.TryGetValue(parent.SessionId, out var siblings))
                    continue;
                foreach (var child in siblings)
                {
                    var record = SessionTreeTopology.ReadCandidate(projectId, child);
                    if (!visitedSessions.Add(child.Id)
                        || !visitedEdges.Add(child.ParentLinkEdgeId!))
                    {
                        throw new SessionTreeProjectionInconsistentException(
                            "session_tree_projection_inconsistent: cycle or duplicate durable edge");
                    }
                    if (!SessionTreeTopology.IsVisibleAt(child, graphRevision, asRoot: false))
                        continue;
                    records.Add((child, record.Session, parent.Depth + 1));
                    next.Add((child.Id, parent.Depth + 1));
                }
            }
            frontier = next.ToArray();
        }

        var membership = records
            .Select(item => new SessionTreeStopMembership(
                item.Row.Id,
                item.Row.ParentSessionId,
                item.Row.ParentLinkEdgeId,
                item.Row.ChildLaunchJobId,
                item.Row.ParentSessionId is null
                    ? 0
                    : item.Row.ParentLinkAttachedRevision!.Value))
            .ToArray();
        var targets = records
            .Select(item =>
            {
                var turn = item.Session.Status.Turns?.LastOrDefault();
                return new SessionTreeStopTargetFact(
                    item.Row.Id,
                    turn?.Id,
                    turn?.JobId,
                    turn?.Status,
                    item.Session.Runtime.RunnerId,
                    item.Session.Runtime.Runtime,
                    item.Session.Status.AgentRuntimeSessionId,
                    item.Session.Runtime.WorkDir);
            })
            .ToArray();
        return new SessionTreeStopSnapshotFacts(
            projectId,
            rootSessionId,
            graphRevision,
            membership,
            targets);
    }

    public async Task<SessionTreeSessionBindingFact?> ReadBindingAsync(
        string projectId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId is required.", nameof(sessionId));
        if (_dbFactory is null)
            throw new InvalidOperationException("ReadBindingAsync requires the persistence-backed fence read port.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.AgentSessions.AsNoTracking()
            .Where(item => item.Id == sessionId)
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        if (row.LabelProjectId is not null
            && !string.Equals(row.LabelProjectId, projectId, StringComparison.Ordinal))
            return null;
        var session = SessionTreeTopology.ReadCandidate(projectId, row).Session;
        return new SessionTreeSessionBindingFact(
            row.LabelProjectId ?? string.Empty,
            row.Id,
            session.Runtime.WorkDir,
            session.Runtime.RunnerId,
            session.Runtime.Runtime,
            session.Status.AgentRuntimeSessionId,
            session.BindingEpoch);
    }
}
