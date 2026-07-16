using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.AgentOps.Services;

public class ProjectEventFeedAssembler : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public ProjectEventFeedAssembler(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<ProjectEventEnvelope>> ListAsync(
        string projectId,
        int limit = 200,
        ProjectEventFilter? filter = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return [];
        if (limit <= 0) limit = 200;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var buckets = new List<IReadOnlyList<ProjectEventEnvelope>>();
        buckets.Add(await LoadIssueEventsAsync(db, projectId, limit, filter, ct));
        buckets.Add(await LoadWorkflowEventsAsync(db, projectId, limit, filter, ct));
        buckets.Add(await LoadAgentSessionEventsAsync(db, projectId, limit, filter, ct));
        buckets.Add(await LoadSessionOpenedEventsAsync(db, projectId, limit, filter, ct));
        buckets.Add(await LoadSessionClosedEventsAsync(db, projectId, limit, filter, ct));

        var events = buckets
            .SelectMany(bucket => bucket)
            .Where(entry => filter?.Matches(entry) ?? true)
            .OrderByDescending(entry => entry.Time)
            .ThenByDescending(entry => entry.Id)
            .ThenBy(entry => entry.Origin)
            .ThenBy(entry => entry.Source, StringComparer.Ordinal)
            .ThenBy(entry => entry.Type, StringComparer.Ordinal)
            .ThenBy(entry => entry.EnvelopeId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        return events;
    }

    private static async Task<IReadOnlyList<ProjectEventEnvelope>> LoadIssueEventsAsync(
        MohistDbContext db,
        string projectId,
        int limit,
        ProjectEventFilter? filter,
        CancellationToken ct)
    {
        var allowedTypes = filter?.CandidateTypes(ProjectEventOrigin.Issue);
        if (allowedTypes is { Length: 0 }) return [];

        var sourcePrefix = IssueEventPersistence.ProjectSourcePrefix(projectId);
        var query = db.IssueEvents.AsNoTracking()
            .Where(row => row.Source.StartsWith(sourcePrefix));
        if (allowedTypes is not null) query = query.Where(row => allowedTypes.Contains(row.Type));

        var rows = await query
            .OrderByDescending(row => row.TimeSortKey)
            .ThenByDescending(row => row.Id)
            .ThenBy(row => row.Source)
            .ThenBy(row => row.Type)
            .ThenBy(row => row.EventId)
            .Take(limit)
            .ToListAsync(ct);

        return rows.Select(ProjectEventEnvelope.FromIssue).ToList();
    }

    private static async Task<IReadOnlyList<ProjectEventEnvelope>> LoadWorkflowEventsAsync(
        MohistDbContext db,
        string projectId,
        int limit,
        ProjectEventFilter? filter,
        CancellationToken ct)
    {
        var allowedTypes = filter?.CandidateTypes(ProjectEventOrigin.WorkflowRun);
        if (allowedTypes is { Length: 0 }) return [];

        var query = from row in db.WorkflowRunEvents.AsNoTracking()
                    join workflowRun in db.WorkflowRuns.AsNoTracking().Where(run => run.MetadataProjectId == projectId)
                        on row.Source equals WorkflowRunEventPersistence.SourcePrefix + workflowRun.WorkflowRunId
                    select new { Event = row };
        if (allowedTypes is not null) query = query.Where(row => allowedTypes.Contains(row.Event.Type));

        var rows = await query
            .OrderByDescending(row => row.Event.TimeSortKey)
            .ThenByDescending(row => row.Event.Id)
            .ThenBy(row => row.Event.Source)
            .ThenBy(row => row.Event.Type)
            .ThenBy(row => row.Event.EventId)
            .Take(limit)
            .ToListAsync(ct);

        return rows
            .Select(row => ProjectEventEnvelope.FromWorkflowRun(row.Event))
            .ToList();
    }

    private static async Task<IReadOnlyList<ProjectEventEnvelope>> LoadAgentSessionEventsAsync(
        MohistDbContext db,
        string projectId,
        int limit,
        ProjectEventFilter? filter,
        CancellationToken ct)
    {
        var allowedTypes = filter?.CandidateTypes(ProjectEventOrigin.AgentSession);
        if (allowedTypes is { Length: 0 }) return [];

        var query = from row in db.AgentSessionEvents.AsNoTracking()
                    join session in db.AgentSessions.AsNoTracking().Where(session => session.LabelProjectId == projectId)
                        on row.Source equals AgentSessionEventPersistence.SourcePrefix + session.Id
                    select new { Event = row, Session = session };
        if (allowedTypes is not null) query = query.Where(row => allowedTypes.Contains(row.Event.Type));
        if (filter?.RequiresAgentSessionStatusFailure == true)
        {
            query = query.Where(row => row.Event.Type != "coder_session_status_changed"
                || row.Event.DataStatus == "failed"
                || row.Event.DataStatus == "timeout"
                || row.Event.DataStatus == "cancelled");
        }

        var rows = await query
            .OrderByDescending(row => row.Event.TimeSortKey)
            .ThenByDescending(row => row.Event.Id)
            .ThenBy(row => row.Event.Source)
            .ThenBy(row => row.Event.Type)
            .ThenBy(row => row.Event.EventId)
            .Take(limit)
            .ToListAsync(ct);

        return rows
            .Select(row => ProjectEventEnvelope.FromAgentSession(
                row.Event,
                ProjectEventSessionContext.From(row.Session)))
            .ToList();
    }

    private static async Task<IReadOnlyList<ProjectEventEnvelope>> LoadSessionOpenedEventsAsync(
        MohistDbContext db,
        string projectId,
        int limit,
        ProjectEventFilter? filter,
        CancellationToken ct)
    {
        if (filter is not null && !filter.Matches(ProjectEventOrigin.AgentSession, "coder_session_started", default)) return [];

        var sessions = await db.AgentSessions.AsNoTracking()
            .Where(session => session.LabelProjectId == projectId)
            .OrderByDescending(session => session.CreatedAt)
            .ThenBy(session => session.Id)
            .Take(limit)
            .ToListAsync(ct);

        return sessions.Select(session => ProjectEventEnvelope.SessionOpened(ProjectEventSessionContext.From(session))).ToList();
    }

    private static async Task<IReadOnlyList<ProjectEventEnvelope>> LoadSessionClosedEventsAsync(
        MohistDbContext db,
        string projectId,
        int limit,
        ProjectEventFilter? filter,
        CancellationToken ct)
    {
        if (filter is not null && !filter.MayMatchSessionClosed()) return [];

        var rows = await (
            from part in db.AgentSessionTranscriptParts.AsNoTracking()
            join turn in db.AgentSessionTranscriptTurns.AsNoTracking() on part.TurnId equals turn.Id
            join session in db.AgentSessions.AsNoTracking().Where(session => session.LabelProjectId == projectId)
                on turn.SessionId equals session.Id
            where part.Type == TranscriptPartTypes.SessionClosed
                && (filter == null || !filter.RequiresAgentSessionStatusFailure
                    || part.PayloadStatus == "failed"
                    || part.PayloadStatus == "timeout"
                    || part.PayloadStatus == "cancelled")
            orderby part.LastSeenAt descending, part.Id descending, session.Id, part.Type, part.CorrelationKey
            select new { Part = part, Session = session })
            .Take(limit)
            .ToListAsync(ct);

        return rows
            .Select(row => ProjectEventEnvelope.SessionClosed(ProjectEventSessionContext.From(row.Session), row.Part))
            .ToList();
    }

}

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
    JsonElement Data,
    IReadOnlyDictionary<string, string> Extensions,
    string? RunnerId,
    int? IssueNumber,
    int? EpicNumber,
    string? SessionSourceKind,
    string? WorkflowRunId,
    string? AgentId,
    string? AgentName)
{
    internal static ProjectEventEnvelope FromIssue(IssueEventRow row) =>
        Build(row.Id, row.Source, ProjectEventOrigin.Issue, "issue", row.Type, row.Time, row.EventId, row.SpecVersion, row.Subject, row.DataContentType, row.Data, row.ExtensionsJson);

    internal static ProjectEventEnvelope FromWorkflowRun(WorkflowRunEventRow row) =>
        Build(row.Id, row.Source, ProjectEventOrigin.WorkflowRun, "workflow-run", row.Type, row.Time, row.EventId, row.SpecVersion, row.Subject, row.DataContentType, row.Data, row.ExtensionsJson);

    internal static ProjectEventEnvelope FromAgentSession(AgentSessionEventRow row, ProjectEventSessionContext? session) =>
        Build(
            row.Id,
            row.Source,
            ProjectEventOrigin.AgentSession,
            "agent-session",
            row.Type,
            row.Time,
            row.EventId,
            row.SpecVersion,
            row.Subject,
            row.DataContentType,
            row.Data,
            row.ExtensionsJson,
            runnerId: session?.RunnerId,
            sessionSourceKind: session?.SourceKind,
            workflowRunId: session?.WorkflowRunId,
            agentId: session?.AgentId,
            agentName: session?.AgentName);

    internal static ProjectEventEnvelope SessionOpened(ProjectEventSessionContext session) =>
        SessionLifecycle(session, 0, "coder_session_started", ToData(new { status = "opened" }), ToOffset(session.CreatedAt), $"{session.SessionId}:opened");

    internal static ProjectEventEnvelope SessionClosed(ProjectEventSessionContext session, AgentSessionTranscriptPartRow part) =>
        SessionLifecycle(session, part.Id, "session.closed", ToData(part.PayloadJson), ToOffset(part.LastSeenAt), $"{session.SessionId}:closed:{part.Id}");

    private static ProjectEventEnvelope SessionLifecycle(
        ProjectEventSessionContext session,
        long id,
        string type,
        JsonElement data,
        DateTimeOffset time,
        string envelopeId) =>
        new(
            id,
            ProjectEventOrigin.AgentSession,
            "agent-session",
            session.SessionId,
            AgentSessionEventPersistence.AgentSessionSource(session.SessionId),
            type,
            time,
            envelopeId,
            "1.0",
            session.SessionId,
            "application/json",
            data,
            new Dictionary<string, string>(),
            session.RunnerId,
            session.IssueNumber,
            session.EpicNumber,
            session.SourceKind,
            session.WorkflowRunId,
            session.AgentId,
            session.AgentName);

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
        JsonElement data,
        string extensionsJson,
        string? runnerId = null,
        string? sessionSourceKind = null,
        string? workflowRunId = null,
        string? agentId = null,
        string? agentName = null)
    {
        var extensions = DeserializeExtensions(extensionsJson);
        return new(
            id,
            origin,
            aggregateKind,
            ExtractAggregateId(source, aggregateKind),
            source,
            type,
            time,
            envelopeId,
            specVersion,
            subject,
            dataContentType,
            data,
            extensions,
            runnerId ?? CloudEventLineage.ReadValue(extensions, EventCatalog.Lineage.RunnerId),
            ReadPositiveNumber(extensions, EventCatalog.Lineage.Issue),
            ReadPositiveNumber(extensions, EventCatalog.Lineage.Epic),
            sessionSourceKind,
            workflowRunId,
            agentId,
            agentName);
    }

    private static string ExtractAggregateId(string source, string aggregateKind)
    {
        if (aggregateKind == "issue")
        {
            var issueMarker = "/issues/";
            var index = source.LastIndexOf(issueMarker, StringComparison.Ordinal);
            return index >= 0 ? source[(index + issueMarker.Length)..] : source;
        }

        var prefix = aggregateKind switch
        {
            "workflow-run" => WorkflowRunEventPersistence.SourcePrefix,
            "agent-session" => AgentSessionEventPersistence.SourcePrefix,
            _ => null,
        };
        if (prefix is null) return source;
        return source.StartsWith(prefix, StringComparison.Ordinal) ? source[prefix.Length..] : source;
    }

    private static IReadOnlyDictionary<string, string> DeserializeExtensions(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, CloudEvent.JsonOptions)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static int? ReadPositiveNumber(
        IReadOnlyDictionary<string, string> extensions,
        string key) =>
        extensions.TryGetValue(key, out var value)
            && int.TryParse(value, out var number)
            && number > 0
                ? number
                : null;

    private static JsonElement ToData(object value) => JsonSerializer.SerializeToElement(value, CloudEvent.JsonOptions);

    private static JsonElement ToData(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ToData(new { });
        }
    }

    private static DateTimeOffset ToOffset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public enum ProjectEventOrigin
{
    Issue,
    WorkflowRun,
    AgentSession,
}

public sealed class ProjectEventFilter
{
    private static readonly string[] IssueTypes =
    [
        "com.mohist.issue.created", "com.mohist.issue.work-started", "com.mohist.issue.completed",
        "com.mohist.issue.cancelled", "com.mohist.issue.reopened", "com.mohist.issue.archived",
        "com.mohist.issue.unarchived", "com.mohist.issue.labels-changed", "com.mohist.issue.priority-changed",
        "com.mohist.issue.draft-changed", "com.mohist.issue.prerequisite-added",
        "com.mohist.issue.prerequisite-removed", "com.mohist.issue.workflow-profile-changed",
        "com.mohist.issue.repository-changed",
    ];

    private static readonly string[] WorkflowTypes =
    [
        "com.mohist.workflow.run.started", "com.mohist.workflow.run.completed", "com.mohist.workflow.run.resumed",
        "com.mohist.workflow.run.retrying", "com.mohist.workflow.run.rerunning", "com.mohist.workflow.run.failed",
        "com.mohist.workflow.run.stopped", "com.mohist.workflow.run.paused", "com.mohist.workflow.stage.started",
        "com.mohist.workflow.stage.completed", "com.mohist.workflow.stage.approval-resolved",
        "com.mohist.workflow.stage.failed", "com.mohist.workflow.stage.approval-requested",
        "com.mohist.workflow.feedback.requested", "com.mohist.workflow.task.started",
        "com.mohist.workflow.task.completed", "com.mohist.workflow.task.failed",
        "com.mohist.workflow.check.passed", "com.mohist.workflow.check.failed", "com.mohist.workflow.check.pending",
        "com.mohist.workflow.repair-scheduled", "com.mohist.workflow.artifact.recorded",
    ];

    private static readonly string[] AgentSessionTypes =
    [
        "coder_session_started", "coder_session_completed", "coder_session_cancelled", "coder_session_failed",
        "coder_session_status_changed", "com.mohist.agent-session.runtime-bound",
        "com.mohist.agent-session.usage-recorded", "com.mohist.agent-session.model-changed",
        "com.mohist.agent-session.context-compacted", "com.mohist.agent-session.context-health-updated",
        "com.mohist.agent-session.context-exhausted", "session.closed", "session.liveness",
    ];

    private readonly HashSet<string> _types;

    private ProjectEventFilter(HashSet<string> types, bool attentionOnly)
    {
        _types = types;
        AttentionOnly = attentionOnly;
    }

    public bool AttentionOnly { get; }

    public bool RequiresAgentSessionStatusFailure =>
        (_types.Count == 0 || _types.Contains("failure"))
        && (AttentionOnly || _types.Contains("failure"));

    public static bool TryCreate(string? types, bool attentionOnly, out ProjectEventFilter? filter)
    {
        if (types is null)
        {
            filter = attentionOnly ? new ProjectEventFilter(new HashSet<string>(StringComparer.Ordinal), true) : null;
            return true;
        }

        var values = types.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (values.Count == 0 || values.Any(type => type is not ("issue-state" or "workflow-stage" or "agent-session" or "failure")))
        {
            filter = null;
            return false;
        }

        filter = values.Count == 0 && !attentionOnly ? null : new ProjectEventFilter(values, attentionOnly);
        return true;
    }

    public string[]? CandidateTypes(ProjectEventOrigin origin)
    {
        if (_types.Count == 0 && !AttentionOnly) return null;

        var candidates = SourceTypes(origin)
            .Where(type => Matches(origin, type, default)
                || (origin == ProjectEventOrigin.AgentSession
                    && RequiresAgentSessionStatusFailure
                    && type == "coder_session_status_changed"))
            .ToArray();
        return candidates;
    }

    public bool MayMatchSessionClosed() =>
        RequiresAgentSessionStatusFailure
        || (!AttentionOnly && (_types.Count == 0 || _types.Contains("agent-session")));

    public bool Matches(ProjectEventEnvelope entry) => Matches(entry.Origin, entry.Type, entry.Data);

    public bool Matches(ProjectEventOrigin origin, string type, JsonElement data)
    {
        var classification = Classify(origin, type, data);
        if (classification is null) return false;
        if (_types.Count > 0 && !_types.Contains(classification.Value.Type)) return false;
        return !AttentionOnly || classification.Value.Attention is not "routine";
    }

    private static IEnumerable<string> SourceTypes(ProjectEventOrigin origin) => origin switch
    {
        ProjectEventOrigin.Issue => IssueTypes,
        ProjectEventOrigin.WorkflowRun => WorkflowTypes,
        ProjectEventOrigin.AgentSession => AgentSessionTypes,
        _ => [],
    };

    private static ProjectEventClassification? Classify(ProjectEventOrigin origin, string type, JsonElement data)
    {
        if (type == "com.mohist.runner.connected" || type == "com.mohist.runner.heartbeat")
            return new("runner", "routine");
        if (type == "com.mohist.runner.disconnected") return new("runner", "blocked");

        if (origin == ProjectEventOrigin.Issue && IssueTypes.Contains(type, StringComparer.Ordinal))
            return new("issue-state", "routine");

        if (origin == ProjectEventOrigin.WorkflowRun && WorkflowTypes.Contains(type, StringComparer.Ordinal))
        {
            if (type is "com.mohist.workflow.run.failed" or "com.mohist.workflow.run.stopped"
                or "com.mohist.workflow.stage.failed" or "com.mohist.workflow.task.failed"
                or "com.mohist.workflow.check.failed") return new("failure", "failure");
            if (type is "com.mohist.workflow.stage.approval-requested" or "com.mohist.workflow.feedback.requested")
                return new("workflow-stage", "approval");
            if (type is "com.mohist.workflow.run.paused" or "com.mohist.workflow.check.pending")
                return new("workflow-stage", "blocked");
            return new("workflow-stage", "routine");
        }

        if (origin == ProjectEventOrigin.AgentSession && AgentSessionTypes.Contains(type, StringComparer.Ordinal))
        {
            var status = ReadStatus(data);
            if (type is "coder_session_failed" or "com.mohist.agent-session.context-exhausted"
                || status is "failed" or "timeout" or "cancelled") return new("failure", "failure");
            return new("agent-session", "routine");
        }

        return null;
    }

    private static string? ReadStatus(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("status", out var status)) return null;
        return status.ValueKind == JsonValueKind.String ? status.GetString()?.ToLowerInvariant() : null;
    }

    private readonly record struct ProjectEventClassification(string Type, string Attention);
}

internal sealed record ProjectEventSessionContext(
    string SessionId,
    DateTime CreatedAt,
    string? SourceKind,
    string? WorkflowRunId,
    int? IssueNumber,
    int? EpicNumber,
    string? AgentId,
    string? AgentName,
    string? RunnerId)
{
    public static ProjectEventSessionContext From(AgentSessionRow row)
    {
        var sourceKind = row.LabelSourceKind;
        return new(
            row.Id,
            row.CreatedAt,
            sourceKind,
            string.Equals(sourceKind, "workflow", StringComparison.Ordinal) ? row.LabelSourceId : null,
            ReadNumber(row.LabelIssueNumber) ?? ReadNumber(row.LabelAgentLaunchIssueNumber),
            ReadNumber(ReadStateLabel(row.State, AgentSessionQueryMetadataKeys.EpicNumber))
                ?? ReadNumber(row.LabelAgentLaunchEpicNumber),
            row.LabelAgentId,
            row.LabelAgentName,
            row.RunnerId);
    }

    private static int? ReadNumber(string? value) =>
        int.TryParse(value, out var number) && number > 0 ? number : null;

    private static string? ReadStateLabel(string state, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(state);
            if (!document.RootElement.TryGetProperty("metadata", out var metadata)
                || !metadata.TryGetProperty("labels", out var labels)
                || labels.ValueKind != JsonValueKind.Object
                || !labels.TryGetProperty(key, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
