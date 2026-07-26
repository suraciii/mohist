using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

public enum AgentSessionQueryOrder
{
    CreatedAscending,
    CreatedDescending,
}

public class AgentSessionQuery : IScopedService
{
    private const string DirectSourceKind = "agent-launch";
    private const string ActiveActivityValue = "active";

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public AgentSessionQuery(IDbContextFactory<MohistDbContext> dbFactory, TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
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
        query = ApplyStatusFilter(query, status, _timeProvider.GetUtcNow().UtcDateTime);
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
    /// Purpose-specific candidate query for the project-scoped
    /// <c>/api/agent/status</c> read. Materializes only Agent Sessions that
    /// can contribute to the active-agent readout for
    /// <paramref name="projectId"/>:
    /// <list type="bullet">
    /// <item>direct Agent Sessions whose own project label matches the
    ///   request and whose projected activity is <c>"active"</c>;</item>
    /// <item>non-direct Sessions whose own project label matches the
    ///   request, have a work id, and join a Workflow Run currently
    ///   persisted as <c>"running"</c> for that same project.</item>
    /// </list>
    /// Rows are returned in the existing global creation-descending order
    /// so callers preserve the established active-agent ordering. The
    /// candidate predicate uses the indexed <c>Activity</c> projection and
    /// the matching <c>IX_AgentSessions_StatusProject_SourceKind_Activity_CreatedAt</c>
    /// composite index for the direct branch, and the indexed
    /// <see cref="WorkflowRunRow.Status"/> / <see cref="WorkflowRunRow.MetadataProjectId"/>
    /// projections for the joined branch, so historical Sessions with
    /// <c>activity = idle</c>, missing source ids, or non-running Workflow
    /// Runs are not selected and their <c>State</c> JSON is not
    /// deserialized (issue-467).
    /// </summary>
    public virtual async Task<IReadOnlyList<AgentSessionRecord>> ListStatusCandidatesAsync(
        string projectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var directCandidates = db.AgentSessions.AsNoTracking()
            .Where(session =>
                session.LabelProjectId == projectId
                && session.LabelSourceKind == DirectSourceKind
                && session.Activity == ActiveActivityValue);
        var workflowCandidates =
            from session in db.AgentSessions.AsNoTracking()
            join workflow in db.WorkflowRuns.AsNoTracking()
                on session.LabelSourceId equals workflow.WorkflowRunId
            where session.LabelProjectId == projectId
                && (session.LabelSourceKind == null || session.LabelSourceKind != DirectSourceKind)
                && session.LabelWorkId != null
                && session.LabelWorkId.Trim() != string.Empty
                && workflow.MetadataProjectId == projectId
                && workflow.Status == "running"
            select session;

        var rows = await directCandidates
            .Concat(workflowCandidates)
            .OrderByDescending(session => session.CreatedAt)
            .ToListAsync(ct);
        OnRowsMaterialized(rows.Count);
        return ToRecords(rows);
    }

    /// <summary>
    /// Narrow internal hook fired once after every candidate query
    /// materializes its rows. Default is a no-op; production code keeps the
    /// existing request-work interceptors as the sole source for database
    /// and downstream counts. The seam exists so deterministic tests can
    /// observe how many rows the new status candidate query materialized
    /// without expanding the public API (issue-467 acceptance criteria).
    /// </summary>
    internal Action<int>? OnRowsMaterializedCallback { get; set; }

    private void OnRowsMaterialized(int count) =>
        OnRowsMaterializedCallback?.Invoke(count);

    /// <summary>
    /// Translates the "active"/"inactive" status filter into a DB-level
    /// predicate on <see cref="AgentSessionRow.AgentSessionId"/> and
    /// <see cref="AgentSessionRow.LastDataAt"/>, so it composes with label
    /// filters, ordering and limit in a single SQL statement.
    /// </summary>
    private static IQueryable<AgentSessionRow> ApplyStatusFilter(
        IQueryable<AgentSessionRow> query, string? status, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(status))
            return query;
        return status.Trim().ToLowerInvariant() switch
        {
            "active" => query.Where(s => s.AgentSessionId != null),
            "inactive" => query.Where(s => s.AgentSessionId == null),
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

                // Direct Agent (agent-launch) lookup keys — .
                // Each maps a GenericAgentSessionMetadata constant to the
                // matching stored computed column; drift between SQL and
                // metadata is caught at compile time.
                GenericAgentSessionMetadata.AgentId => query.Where(s => s.LabelAgentId == value),
                GenericAgentSessionMetadata.AgentName => query.Where(s => s.LabelAgentName == value),
                GenericAgentSessionMetadata.IssueNumber => query.Where(s => s.LabelAgentLaunchIssueNumber == value),
                GenericAgentSessionMetadata.EpicNumber => query.Where(s => s.LabelAgentLaunchEpicNumber == value),
                GenericAgentSessionMetadata.Repository => query.Where(s => s.LabelAgentLaunchRepository == value),
                GenericAgentSessionMetadata.WorkspacePath => query.Where(s => s.LabelAgentLaunchWorkspacePath == value),

                // subscription trigger correlation labels.
                GenericAgentSessionMetadata.TriggerEventId => query.Where(s => s.LabelTriggerEventId == value),
                GenericAgentSessionMetadata.TriggerRuleId => query.Where(s => s.LabelTriggerRuleId == value),

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
    /// <summary>
    /// Resolves a label value with the record-first-then-metadata fallback:
    /// the record's own <see cref="Labels"/> is consulted first, and when the
    /// key is absent the session's <see cref="AgentSessionMetadata.Labels"/>
    /// is consulted as a defensive fallback.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentSessionQuery.ToRecords"/> constructs every production
    /// record with <c>session.Metadata.Labels</c> (so the two dictionaries
    /// coincide in production); the fallback exists for synthetic records
    /// built directly via the constructor with a hand-crafted label
    /// dictionary (tests / fakes). Returns <c>null</c> when the key is
    /// absent from both sources.
    /// </remarks>
    public string? Label(string key) => Labels.TryGetValue(key, out var value)
        ? value
        : Session.Metadata.Label(key);

    /// <summary>
    /// Reads the issue-number label (with record-first-then-metadata
    /// fallback via <see cref="Label(string)"/>) and parses it as an
    /// <see cref="int"/>. Returns <c>0</c> when the label is absent,
    /// empty, whitespace, or non-numeric — matching the prior
    /// <c>AgentSessionQuerier.IssueNumber</c> semantics (now an
    /// instance method, no longer a querier static).
    /// </summary>
    public int IssueNumber() =>
        int.TryParse(Label(AgentSessionQueryMetadataKeys.IssueNumber), out var issueNumber)
            ? issueNumber
            : 0;
}
