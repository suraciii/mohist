using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Workflow.Services.Sessions;

namespace Mohist.Server.Sessions.Services;

public enum AgentSessionQueryOrder
{
    CreatedAscending,
    CreatedDescending,
}

public sealed class AgentSessionQuery : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public AgentSessionQuery(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<AgentSessionRecord?> FirstByLabelsAsync(
        IReadOnlyDictionary<string, string> labels,
        AgentSessionQueryOrder order = AgentSessionQueryOrder.CreatedAscending,
        CancellationToken ct = default)
    {
        var records = await ListByLabelsAsync(labels, order, limit: 1, ct: ct);
        return records.FirstOrDefault();
    }

    public async Task<IReadOnlyList<AgentSessionRecord>> ListByLabelsAsync(
        IReadOnlyDictionary<string, string> labels,
        AgentSessionQueryOrder order = AgentSessionQueryOrder.CreatedAscending,
        int? limit = null,
        DateTime? from = null,
        DateTime? to = null,
        string? status = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = QueryRowsByLabels(db.AgentSessions.AsNoTracking(), labels);
        if (from is not null)
            query = query.Where(session => session.CreatedAt >= from.Value);
        if (to is not null)
            query = query.Where(session => session.CreatedAt < to.Value);
        query = ApplyStatusFilter(query, status);
        query = order == AgentSessionQueryOrder.CreatedDescending
            ? query.OrderByDescending(session => session.CreatedAt)
            : query.OrderBy(session => session.CreatedAt);
        if (limit is > 0)
            query = query.Take(limit.Value);

        var rows = await query.ToListAsync(ct);
        return ToRecords(rows);
    }

    public async Task<IReadOnlyList<AgentSessionRecord>> ListByIdsAsync(
        IReadOnlyList<string> sessionIds,
        CancellationToken ct = default)
    {
        if (sessionIds.Count == 0) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentSessions.AsNoTracking()
            .Where(session => sessionIds.Contains(session.Id))
            .OrderBy(session => session.CreatedAt)
            .ToListAsync(ct);
        return ToRecords(rows);
    }

    /// <summary>
    /// Translates the "active"/"inactive" status filter into a DB-level
    /// predicate on <see cref="AgentSessionRow.AgentSessionId"/> and
    /// <see cref="AgentSessionRow.LastDataAt"/>, so it composes with label
    /// filters, ordering and limit in a single SQL statement.
    /// </summary>
    private static IQueryable<AgentSessionRow> ApplyStatusFilter(
        IQueryable<AgentSessionRow> query, string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return query;
        var cutoff = DateTime.UtcNow - AgentSessionJsonHelper.ActiveRuntimeEventWindow;
        return status.Trim().ToLowerInvariant() switch
        {
            "active" => query.Where(s => s.AgentSessionId != null && s.LastDataAt >= cutoff),
            "inactive" => query.Where(s => s.AgentSessionId == null || s.LastDataAt < cutoff),
            _ => query,
        };
    }

    private static IQueryable<AgentSessionRow> QueryRowsByLabels(
        IQueryable<AgentSessionRow> query,
        IReadOnlyDictionary<string, string> labels)
    {
        var filters = labels
            .Where(label => !string.IsNullOrWhiteSpace(label.Key) && !string.IsNullOrWhiteSpace(label.Value))
            .ToArray();
        if (filters.Length == 0)
            return query.Where(_ => false);

        foreach (var (key, value) in filters)
        {
            query = key switch
            {
                AgentSessionQueryMetadataKeys.ProjectId => query.Where(s => s.LabelProjectId == value),
                AgentSessionQueryMetadataKeys.WorkflowRunId => query.Where(s => s.LabelSourceId == value),
                AgentSessionQueryMetadataKeys.SessionName => query.Where(s => s.LabelSessionName == value),
                AgentSessionQueryMetadataKeys.IssueNumber => query.Where(s => s.LabelIssueNumber == value),
                AgentSessionQueryMetadataKeys.WorkId => query.Where(s => s.LabelWorkId == value),
                AgentSessionQueryMetadataKeys.WorkType => query.Where(s => s.LabelWorkType == value),
                AgentSessionQueryMetadataKeys.Stage => query.Where(s => s.LabelStage == value),
                AgentSessionQueryMetadataKeys.SourceKind => query.Where(s => s.LabelSourceKind == value),

                // Direct Agent (agent-launch) lookup keys — issued-130 T-001.
                // Each maps a GenericAgentSessionMetadata constant to the
                // matching stored computed column; drift between SQL and
                // metadata is caught at compile time.
                GenericAgentSessionMetadata.AgentId => query.Where(s => s.LabelAgentId == value),
                GenericAgentSessionMetadata.AgentName => query.Where(s => s.LabelAgentName == value),
                GenericAgentSessionMetadata.IssueNumber => query.Where(s => s.LabelAgentLaunchIssueNumber == value),
                GenericAgentSessionMetadata.EpicNumber => query.Where(s => s.LabelAgentLaunchEpicNumber == value),
                GenericAgentSessionMetadata.Repository => query.Where(s => s.LabelAgentLaunchRepository == value),
                GenericAgentSessionMetadata.WorkspacePath => query.Where(s => s.LabelAgentLaunchWorkspacePath == value),

                _ => query.Where(_ => false),
            };
        }

        return query;
    }

    /// <summary>
    /// Builds <see cref="AgentSessionRecord"/> objects from raw rows by
    /// deserializing the <see cref="AgentSessionRow.State"/> JSON.
    /// Labels come from <see cref="AgentSessionMetadata.Labels"/> inside the
    /// JSON directly — the label data lives in one place (the State column)
    /// and is indexed via stored computed columns on AgentSessions.
    /// </summary>
    private static IReadOnlyList<AgentSessionRecord> ToRecords(IReadOnlyList<AgentSessionRow> rows)
    {
        if (rows.Count == 0) return [];

        var result = new List<AgentSessionRecord>(rows.Count);
        foreach (var row in rows)
        {
            var session = AgentSessionJson.Deserialize(row);
            if (session is null) continue;
            result.Add(new AgentSessionRecord(
                row,
                session,
                session.Metadata.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal)));
        }

        return result;
    }
}

public sealed record AgentSessionRecord(
    AgentSessionRow Row,
    AgentSession Session,
    IReadOnlyDictionary<string, string> Labels)
{
    public string? Label(string key) => Labels.TryGetValue(key, out var value) ? value : null;
}
