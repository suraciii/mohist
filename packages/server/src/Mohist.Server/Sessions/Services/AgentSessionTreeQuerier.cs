using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

public sealed class AgentSessionTreeQuerier(
    IDbContextFactory<MohistDbContext> dbFactory) : IScopedService
{
    public async Task<AgentSessionTreePage?> GetAsync(
        string projectId,
        string rootSessionId,
        int limit,
        string? continuation,
        CancellationToken ct = default)
    {
        var published = await SessionTreeGraphRevisionWatermark.ReadPublishedRevisionAsync(dbFactory, projectId, ct);
        var cursor = ReadCursor(projectId, rootSessionId, published, continuation);
        var revision = cursor?.GraphRevision ?? published;
        var rows = await LoadTreeRowsAsync(projectId, rootSessionId, revision, ct);
        if (rows.Count == 0)
            return null;

        var pageSize = Math.Clamp(limit, 1, 200);
        var pageRows = rows.Skip(cursor?.Offset ?? 0).Take(pageSize).ToArray();
        var page = pageRows.Select(item => ToNode(item.Record, item.Depth)).ToArray();
        var edges = pageRows
            .Where(item => item.Record.Row.ParentSessionId is not null)
            .Select(item => new AgentSessionTreeEdge(
                item.Record.Row.ParentLinkEdgeId!,
                item.Record.Row.ParentSessionId!,
                item.Record.Session.Id,
                item.Record.Row.ChildLaunchJobId!,
                SessionParentLinkState.Attached.ToString().ToLowerInvariant()))
            .ToArray();
        var nextOffset = (cursor?.Offset ?? 0) + page.Length;
        var next = nextOffset < rows.Count
            ? EncodeCursor(new TreeCursor(projectId, rootSessionId, revision, nextOffset))
            : null;
        return new AgentSessionTreePage(
            new AgentSessionTreeRoot(rootSessionId),
            revision,
            page,
            edges,
            next);
    }

    private async Task<IReadOnlyList<(AgentSessionRecord Record, int Depth)>> LoadTreeRowsAsync(
        string projectId,
        string rootSessionId,
        long revision,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var root = await db.AgentSessions.AsNoTracking()
            .Where(row => row.Id == rootSessionId && row.LabelProjectId == projectId
                && (((row.LaunchVisibility == null || row.LaunchVisibility == "visible")
                        && (row.ParentSessionId == null
                            || (row.ParentLinkDetachedRevision != null
                                && row.ParentLinkDetachedRevision <= revision)))
                    || (row.ParentLinkAttachedRevision <= revision
                        && (row.ParentLinkDetachedRevision == null
                            || row.ParentLinkDetachedRevision > revision))))
            .FirstOrDefaultAsync(ct);
        if (root is null)
            return [];

        var rootRecord = ToRecord(root);
        if (rootRecord is null)
            return [];

        var result = new List<(AgentSessionRecord Record, int Depth)> { (rootRecord, 0) };
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootSessionId };
        var seenEdges = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new List<string> { rootSessionId };
        var depth = 0;

        while (frontier.Count > 0)
        {
            depth++;
            var parentIds = frontier.ToArray();
            var candidates = await db.AgentSessions.AsNoTracking()
                .Where(row => row.LabelProjectId == projectId
                    && row.ParentSessionId != null
                    && parentIds.Contains(row.ParentSessionId)
                    && row.ParentLinkAttachedRevision <= revision
                    && (row.ParentLinkDetachedRevision == null || row.ParentLinkDetachedRevision > revision))
                .OrderBy(row => row.ParentSessionId)
                .ThenBy(row => row.ParentLinkAttachedRevision)
                .ThenBy(row => row.ParentLinkEdgeId)
                .ToListAsync(ct);

            var byParent = candidates
                .GroupBy(row => row.ParentSessionId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var next = new List<string>();
            foreach (var parent in frontier)
            {
                if (!byParent.TryGetValue(parent, out var siblings))
                    continue;
                foreach (var candidate in siblings)
                {
                    ValidateCandidate(candidate, projectId, revision, seenEdges, visited);
                    var record = ToRecord(candidate);
                    if (record is null)
                        throw new SessionTreeProjectionInconsistentException();
                    result.Add((record, depth));
                    next.Add(candidate.Id);
                    visited.Add(candidate.Id);
                }
            }
            frontier = next;
        }

        return result;
    }

    private static void ValidateCandidate(
        AgentSessionRow candidate,
        string projectId,
        long revision,
        HashSet<string> seenEdges,
        HashSet<string> visited)
    {
        if (!string.Equals(candidate.LabelProjectId, projectId, StringComparison.Ordinal)
            || string.IsNullOrEmpty(candidate.ParentLinkEdgeId)
            || string.IsNullOrEmpty(candidate.ParentSessionId)
            || string.IsNullOrEmpty(candidate.ChildLaunchJobId))
            throw new SessionTreeProjectionInconsistentException();

        var attached = candidate.ParentLinkAttachedRevision;
        if (!attached.HasValue || attached.Value <= 0 || attached.Value > revision)
            throw new SessionTreeProjectionInconsistentException();

        var detached = candidate.ParentLinkDetachedRevision;
        if (detached.HasValue && (detached.Value <= revision || detached.Value <= attached.Value))
            throw new SessionTreeProjectionInconsistentException();

        if (string.Equals(candidate.ParentSessionId, candidate.Id, StringComparison.Ordinal)
            || !seenEdges.Add(candidate.ParentLinkEdgeId!)
            || !visited.Add(candidate.Id))
            throw new SessionTreeProjectionInconsistentException();
    }

    private static AgentSessionTreeNode ToNode(AgentSessionRecord record, int depth) => new(
        SessionId: record.Session.Id,
        ParentSessionId: record.Row.ParentSessionId,
        EdgeId: record.Row.ParentLinkEdgeId,
        JobId: record.Row.ChildLaunchJobId,
        AgentId: record.Label(GenericAgentSessionMetadata.AgentId),
        AgentName: record.Label(GenericAgentSessionMetadata.AgentName),
        WorkDir: record.Session.Runtime.WorkDir,
        RunnerId: record.Session.Runtime.RunnerId,
        Activity: record.Session.Status.Activity.ToString().ToLowerInvariant(),
        Depth: depth,
        AttachedRevision: record.Row.ParentLinkAttachedRevision);

    private static AgentSessionRecord? ToRecord(AgentSessionRow row)
    {
        var session = AgentSessionJson.Deserialize(row);
        return session is null
            ? null
            : new AgentSessionRecord(
                row,
                session,
                session.Metadata.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static TreeCursor? ReadCursor(
        string projectId,
        string rootSessionId,
        long currentRevision,
        string? continuation)
    {
        if (string.IsNullOrWhiteSpace(continuation))
            return null;

        TreeCursor? cursor;
        try
        {
            cursor = JsonSerializer.Deserialize<TreeCursor>(
                Encoding.UTF8.GetString(Convert.FromBase64String(continuation)),
                JSON.Options);
        }
        catch (Exception ex) when (ex is FormatException
            or ArgumentException
            or JsonException
            or DecoderFallbackException)
        {
            throw new AgentSessionTreeContinuationException();
        }

        if (cursor is null
            || !string.Equals(cursor.ProjectId, projectId, StringComparison.Ordinal)
            || !string.Equals(cursor.RootSessionId, rootSessionId, StringComparison.Ordinal)
            || cursor.GraphRevision < 0
            || cursor.GraphRevision > currentRevision
            || cursor.Offset < 0)
        {
            throw new AgentSessionTreeContinuationException();
        }
        return cursor;
    }

    private static string EncodeCursor(TreeCursor cursor) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor, JSON.Options)));

    private sealed record TreeCursor(string ProjectId, string RootSessionId, long GraphRevision, int Offset);
}

public sealed class AgentSessionTreeContinuationException : Exception
{
}

public sealed class SessionTreeProjectionInconsistentException : Exception
{
}

public sealed record AgentSessionTreePage(
    AgentSessionTreeRoot Root,
    long Revision,
    IReadOnlyList<AgentSessionTreeNode> Nodes,
    IReadOnlyList<AgentSessionTreeEdge> Edges,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Continuation);

public sealed record AgentSessionTreeRoot(string SessionId);

public sealed record AgentSessionTreeEdge(
    string EdgeId,
    string ParentSessionId,
    string ChildSessionId,
    string ChildLaunchJobId,
    string State);

public sealed record AgentSessionTreeNode(
    string SessionId,
    string? ParentSessionId,
    string? EdgeId,
    string? JobId,
    string? AgentId,
    string? AgentName,
    string? WorkDir,
    string? RunnerId,
    string Activity,
    int Depth,
    long? AttachedRevision);
