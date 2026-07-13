using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Project.Services;

/// <summary>
/// Reads recorded CloudEvents across all per-aggregate event tables for a
/// single project (issue-402 T-000). Sources are the four event tables
/// (<c>IssueEvents</c>, <c>WorkflowRunEvents</c>, <c>AgentSessionEvents</c>,
/// <c>EpicEvents</c>) that already persist every domain transition; the
/// querier projects them onto a unified, time-sorted stream without changing
/// how events are recorded, emitted, or subscribed.
/// </summary>
/// <remarks>
/// <para>
/// Project scoping is computed once per call by collecting the
/// aggregate ids owned by the project (<c>Issues</c> by <c>ProjectId</c>,
/// <c>WorkflowRuns</c> by the <c>MetadataProjectId</c> computed column,
/// <c>AgentSessions</c> by the <c>LabelProjectId</c> computed column, and
/// <c>Epics</c> by <c>ProjectId</c>) and then translating each id to its
/// canonical CloudEvent <c>source</c> prefix. The four queries ride the
/// existing <c>Source</c> indexes on the event tables; an empty project
/// short-circuits before any event-table read.
/// </para>
/// <para>
/// Per-table order is most-recent-first by <c>Time</c>, with
/// <c>Id</c> (per-table monotonic) as a stable tiebreaker so two events
/// at the same <c>Time</c> keep a deterministic order. The final merge
/// sorts the per-table buckets in memory (SQLite's EF provider cannot
/// translate ORDER BY over <c>DateTimeOffset</c>) and then caps the
/// merged stream at <paramref name="limit"/> so a project with
/// concentrated issue activity and sparse session activity cannot be
/// starved of session evidence just because issues dominate the
/// underlying sources.
/// </para>
/// <para>
/// This read path is purely additive: <c>AppendAsync</c> /
/// <c>ListAsync</c> / <c>ListUndeliveredAsync</c> semantics are unchanged,
/// and no subscription or dispatch behavior is added.
/// </para>
/// </remarks>
public class ProjectEventQuerier : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public ProjectEventQuerier(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<ProjectEventEnvelope>> ListAsync(
        string projectId,
        int limit = 200,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            return [];
        if (limit <= 0) limit = 200;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var scope = await LoadScopeAsync(db, projectId, ct);
        if (scope.IsEmpty) return [];

        var perTableCap = Math.Max(limit, 200);

        var tasks = new List<Task<List<ProjectEventEnvelope>>>();

        if (scope.IssueSources.Count > 0)
        {
            tasks.Add(LoadIssueEventsAsync(db, scope.IssueSources, perTableCap, ct));
        }
        if (scope.WorkflowSources.Count > 0)
        {
            tasks.Add(LoadWorkflowEventsAsync(db, scope.WorkflowSources, perTableCap, ct));
        }
        if (scope.AgentSessionSources.Count > 0)
        {
            tasks.Add(LoadAgentSessionEventsAsync(db, scope.AgentSessionSources, perTableCap, ct));
        }
        if (scope.EpicSources.Count > 0)
        {
            tasks.Add(LoadEpicEventsAsync(db, scope.EpicSources, perTableCap, ct));
        }

        var buckets = await Task.WhenAll(tasks);

        return buckets
            .SelectMany(b => b)
            .OrderByDescending(e => e.Time)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToList();
    }

    private static async Task<ProjectScope> LoadScopeAsync(MohistDbContext db, string projectId, CancellationToken ct)
    {
        var issueIds = await db.Issues.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .Select(r => r.IssueId)
            .ToListAsync(ct);

        var workflowRunIds = await db.WorkflowRuns.AsNoTracking()
            .Where(r => r.MetadataProjectId == projectId)
            .Select(r => r.WorkflowRunId)
            .ToListAsync(ct);

        var agentSessionIds = await db.AgentSessions.AsNoTracking()
            .Where(s => s.LabelProjectId == projectId)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var epicIds = await db.Epics.AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .Select(e => e.Id)
            .ToListAsync(ct);

        return new ProjectScope(
            issueIds.Select(IssueEventPersistence.IssueSource).ToList(),
            workflowRunIds.Select(WorkflowRunEventPersistence.WorkflowRunSource).ToList(),
            agentSessionIds.Select(AgentSessionEventPersistence.AgentSessionSource).ToList(),
            epicIds.Select(EpicEventPersistence.EpicSource).ToList());
    }

    private static async Task<List<ProjectEventEnvelope>> LoadIssueEventsAsync(
        MohistDbContext db,
        IReadOnlyList<string> sources,
        int limit,
        CancellationToken ct)
    {
        var rows = await db.IssueEvents.AsNoTracking()
            .Where(e => sources.Contains(e.Source))
            .Take(limit)
            .ToListAsync(ct);

        return rows
            .OrderByDescending(e => e.Time)
            .ThenByDescending(e => e.Id)
            .Select(ProjectEventEnvelope.FromIssue)
            .ToList();
    }

    private static async Task<List<ProjectEventEnvelope>> LoadWorkflowEventsAsync(
        MohistDbContext db,
        IReadOnlyList<string> sources,
        int limit,
        CancellationToken ct)
    {
        var rows = await db.WorkflowRunEvents.AsNoTracking()
            .Where(e => sources.Contains(e.Source))
            .Take(limit)
            .ToListAsync(ct);

        return rows
            .OrderByDescending(e => e.Time)
            .ThenByDescending(e => e.Id)
            .Select(ProjectEventEnvelope.FromWorkflowRun)
            .ToList();
    }

    private static async Task<List<ProjectEventEnvelope>> LoadAgentSessionEventsAsync(
        MohistDbContext db,
        IReadOnlyList<string> sources,
        int limit,
        CancellationToken ct)
    {
        var rows = await db.AgentSessionEvents.AsNoTracking()
            .Where(e => sources.Contains(e.Source))
            .Take(limit)
            .ToListAsync(ct);

        return rows
            .OrderByDescending(e => e.Time)
            .ThenByDescending(e => e.Id)
            .Select(ProjectEventEnvelope.FromAgentSession)
            .ToList();
    }

    private static async Task<List<ProjectEventEnvelope>> LoadEpicEventsAsync(
        MohistDbContext db,
        IReadOnlyList<string> sources,
        int limit,
        CancellationToken ct)
    {
        var rows = await db.EpicEvents.AsNoTracking()
            .Where(e => sources.Contains(e.Source))
            .Take(limit)
            .ToListAsync(ct);

        return rows
            .OrderByDescending(e => e.Time)
            .ThenByDescending(e => e.Id)
            .Select(ProjectEventEnvelope.FromEpic)
            .ToList();
    }

    private sealed record ProjectScope(
        IReadOnlyList<string> IssueSources,
        IReadOnlyList<string> WorkflowSources,
        IReadOnlyList<string> AgentSessionSources,
        IReadOnlyList<string> EpicSources)
    {
        public bool IsEmpty =>
            IssueSources.Count == 0
            && WorkflowSources.Count == 0
            && AgentSessionSources.Count == 0
            && EpicSources.Count == 0;
    }
}

/// <summary>
/// Wire-level projection of a single recorded CloudEvent surfaced by the
/// project-scoped read endpoint (<c>GET /api/projects/&#123;projectRef&#125;/events</c>).
/// Carries the envelope identity (<see cref="Origin"/>, <see cref="Type"/>,
/// <see cref="Time"/>, <see cref="SourceAggregateId"/>, <see cref="RunnerId"/>)
/// the Web projection needs to classify entries as <c>issue-state</c>,
/// <c>workflow-stage</c>, <c>agent-session</c>, <c>runner</c>, or
/// <c>failure</c> evidence without re-reading the raw envelope.
/// </summary>
/// <remarks>
/// <para>
/// Origin names match the four per-aggregate tables so a single
/// <see cref="Origin"/> value tells the Web layer which event class the
/// row belongs to. The original CloudEvent envelope (<see cref="EnvelopeId"/>,
/// <see cref="SpecVersion"/>, <see cref="Subject"/>, <see cref="Data"/>,
/// <see cref="DataContentType"/>, <see cref="Extensions"/>) is preserved
/// unchanged so the projection layer can opt into richer payload decoding
/// later without a schema migration.
/// </para>
/// <para>
/// Aggregate ids are derived from the <c>Source</c> URI-reference
/// (<c>/mohist/&#123;kind&#125;/&#123;id&#125;</c>) and exposed as plain
/// strings so JSON consumers can route the entry to the matching detail
/// page. <see cref="RunnerId"/> is sourced from the row's <c>Subject</c>
/// when the publisher stamped it; events without a runner binding surface
/// <c>null</c>.
/// </para>
/// </remarks>
public sealed record ProjectEventEnvelope(
    long Id,
    ProjectEventOrigin Origin,
    string SourceAggregateKind,
    string SourceAggregateId,
    string Source,
    string Type,
    DateTimeOffset Time,
    string EnvelopeId,
    string SpecVersion,
    string? Subject,
    string? DataContentType,
    System.Text.Json.JsonElement Data,
    IReadOnlyDictionary<string, string> Extensions,
    string? RunnerId)
{
    internal static ProjectEventEnvelope FromIssue(IssueEventRow row) =>
        Build(row.Id, row.Source, ProjectEventOrigin.Issue, "issue", row.Type, row.Time, row.EventId, row.SpecVersion, row.Subject, row.DataContentType, row.Data, row.ExtensionsJson);

    internal static ProjectEventEnvelope FromWorkflowRun(WorkflowRunEventRow row) =>
        Build(row.Id, row.Source, ProjectEventOrigin.WorkflowRun, "workflow-run", row.Type, row.Time, row.EventId, row.SpecVersion, row.Subject, row.DataContentType, row.Data, row.ExtensionsJson);

    internal static ProjectEventEnvelope FromAgentSession(AgentSessionEventRow row) =>
        Build(row.Id, row.Source, ProjectEventOrigin.AgentSession, "agent-session", row.Type, row.Time, row.EventId, row.SpecVersion, row.Subject, row.DataContentType, row.Data, row.ExtensionsJson);

    internal static ProjectEventEnvelope FromEpic(EpicEventRow row) =>
        Build(row.Id, row.Source, ProjectEventOrigin.Epic, "epic", row.Type, row.Time, row.EventId, row.SpecVersion, row.Subject, row.DataContentType, row.Data, row.ExtensionsJson);

    private static ProjectEventEnvelope Build(
        long id,
        string source,
        ProjectEventOrigin origin,
        string aggregateKind,
        string type,
        DateTimeOffset time,
        string envelopeId,
        string specVersion,
        string? subject,
        string? dataContentType,
        System.Text.Json.JsonElement data,
        string extensionsJson)
    {
        return new ProjectEventEnvelope(
            Id: id,
            Origin: origin,
            SourceAggregateKind: aggregateKind,
            SourceAggregateId: ExtractAggregateId(source, aggregateKind),
            Source: source,
            Type: type,
            Time: time,
            EnvelopeId: envelopeId,
            SpecVersion: specVersion,
            Subject: subject,
            DataContentType: dataContentType,
            Data: data,
            Extensions: DeserializeExtensions(extensionsJson),
            RunnerId: ResolveRunnerId(extensionsJson));
    }

    private static string ExtractAggregateId(string source, string aggregateKind)
    {
        var prefix = aggregateKind switch
        {
            "issue" => IssueEventPersistence.SourcePrefix,
            "workflow-run" => WorkflowRunEventPersistence.SourcePrefix,
            "agent-session" => AgentSessionEventPersistence.SourcePrefix,
            "epic" => EpicEventPersistence.SourcePrefix,
            _ => null,
        };
        if (prefix is null) return source;
        return source.StartsWith(prefix, StringComparison.Ordinal)
            ? source.Substring(prefix.Length)
            : source;
    }

    private static IReadOnlyDictionary<string, string> DeserializeExtensions(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json, CloudEvent.JsonOptions)
                   ?? new Dictionary<string, string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static string? ResolveRunnerId(string extensionsJson)
    {
        if (string.IsNullOrWhiteSpace(extensionsJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(extensionsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            foreach (var name in new[] { "runnerid", "runnerId", "runner_id", "runner" })
            {
                if (doc.RootElement.TryGetProperty(name, out var prop)
                    && prop.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var value = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return null;
    }
}

public enum ProjectEventOrigin
{
    Issue,
    WorkflowRun,
    AgentSession,
    Epic,
}
