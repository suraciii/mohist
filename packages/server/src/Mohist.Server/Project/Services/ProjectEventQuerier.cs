using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Project.Services;

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
        if (string.IsNullOrWhiteSpace(projectId)) return [];
        if (limit <= 0) limit = 200;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var scope = await LoadScopeAsync(db, projectId, ct);
        if (scope.IsEmpty) return [];

        var buckets = new List<IReadOnlyList<ProjectEventEnvelope>>();
        if (scope.IssueSources.Count > 0)
            buckets.Add(await LoadIssueEventsAsync(db, scope.IssueSources, limit, ct));
        if (scope.WorkflowSources.Count > 0)
            buckets.Add(await LoadWorkflowEventsAsync(db, scope.WorkflowSources, scope.WorkflowIssueNumbers, limit, ct));
        if (scope.AgentSessions.Count > 0)
        {
            var sessionById = scope.AgentSessions.ToDictionary(session => session.SessionId, StringComparer.Ordinal);
            buckets.Add(await LoadAgentSessionEventsAsync(db, scope.AgentSessionSources, sessionById, limit, ct));
            buckets.Add(BuildSessionOpenedEvents(scope.AgentSessions));
            buckets.Add(await LoadSessionClosedEventsAsync(db, sessionById, ct));
        }

        return buckets
            .SelectMany(bucket => bucket)
            .OrderByDescending(entry => entry.Time)
            .ThenByDescending(entry => entry.Id)
            .ThenBy(entry => entry.Origin)
            .ThenBy(entry => entry.Source, StringComparer.Ordinal)
            .ThenBy(entry => entry.Type, StringComparer.Ordinal)
            .ThenBy(entry => entry.EnvelopeId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    private static async Task<ProjectScope> LoadScopeAsync(MohistDbContext db, string projectId, CancellationToken ct)
    {
        var issues = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .Select(row => new { row.IssueId, row.Number, row.WorkflowRunId })
            .ToListAsync(ct);
        var workflowRuns = await db.WorkflowRuns.AsNoTracking()
            .Where(row => row.MetadataProjectId == projectId)
            .Select(row => new { row.WorkflowRunId, row.State })
            .ToListAsync(ct);
        var sessionRows = await db.AgentSessions.AsNoTracking()
            .Where(row => row.LabelProjectId == projectId)
            .ToListAsync(ct);

        var workflowIssueNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var workflowRun in workflowRuns)
        {
            var issueNumber = ReadWorkflowIssueNumber(workflowRun.State);
            if (issueNumber is not null) workflowIssueNumbers[workflowRun.WorkflowRunId] = issueNumber.Value;
        }
        foreach (var issue in issues)
        {
            if (!string.IsNullOrWhiteSpace(issue.WorkflowRunId) && issue.Number is not null)
                workflowIssueNumbers[issue.WorkflowRunId] = issue.Number.Value;
        }

        return new ProjectScope(
            issues.Select(issue => IssueEventPersistence.IssueSource(issue.IssueId)).ToList(),
            workflowRuns.Select(workflowRun => WorkflowRunEventPersistence.WorkflowRunSource(workflowRun.WorkflowRunId)).ToList(),
            workflowIssueNumbers,
            sessionRows.Select(ProjectEventSessionContext.From).ToList());
    }

    private static async Task<IReadOnlyList<ProjectEventEnvelope>> LoadIssueEventsAsync(
        MohistDbContext db,
        IReadOnlyList<string> sources,
        int limit,
        CancellationToken ct)
    {
        var rows = await db.IssueEvents.AsNoTracking()
            .Where(row => sources.Contains(row.Source))
            .ToListAsync(ct);

        return rows
            .OrderByDescending(row => row.Time)
            .ThenByDescending(row => row.Id)
            .Take(limit)
            .Select(ProjectEventEnvelope.FromIssue)
            .ToList();
    }

    private static async Task<IReadOnlyList<ProjectEventEnvelope>> LoadWorkflowEventsAsync(
        MohistDbContext db,
        IReadOnlyList<string> sources,
        IReadOnlyDictionary<string, int> workflowIssueNumbers,
        int limit,
        CancellationToken ct)
    {
        var rows = await db.WorkflowRunEvents.AsNoTracking()
            .Where(row => sources.Contains(row.Source))
            .ToListAsync(ct);

        return rows
            .OrderByDescending(row => row.Time)
            .ThenByDescending(row => row.Id)
            .Take(limit)
            .Select(row => ProjectEventEnvelope.FromWorkflowRun(
                row,
                workflowIssueNumbers.GetValueOrDefault(ProjectEventEnvelope.ExtractWorkflowRunId(row.Source))))
            .ToList();
    }

    private static async Task<IReadOnlyList<ProjectEventEnvelope>> LoadAgentSessionEventsAsync(
        MohistDbContext db,
        IReadOnlyList<string> sources,
        IReadOnlyDictionary<string, ProjectEventSessionContext> sessionById,
        int limit,
        CancellationToken ct)
    {
        var rows = await db.AgentSessionEvents.AsNoTracking()
            .Where(row => sources.Contains(row.Source))
            .ToListAsync(ct);

        return rows
            .OrderByDescending(row => row.Time)
            .ThenByDescending(row => row.Id)
            .Take(limit)
            .Select(row => ProjectEventEnvelope.FromAgentSession(
                row,
                sessionById.GetValueOrDefault(ProjectEventEnvelope.ExtractAgentSessionId(row.Source))))
            .ToList();
    }

    private static IReadOnlyList<ProjectEventEnvelope> BuildSessionOpenedEvents(
        IReadOnlyList<ProjectEventSessionContext> sessions) =>
        sessions.Select((session, index) => ProjectEventEnvelope.SessionOpened(session, -1L - index)).ToList();

    private static async Task<IReadOnlyList<ProjectEventEnvelope>> LoadSessionClosedEventsAsync(
        MohistDbContext db,
        IReadOnlyDictionary<string, ProjectEventSessionContext> sessionById,
        CancellationToken ct)
    {
        var loaded = await TranscriptPartLoader.LoadAsync(db, sessionById.Keys, ct, TranscriptPartTypes.SessionClosed);
        return loaded.Parts
            .Where(part => loaded.SessionByTurnId.TryGetValue(part.TurnId, out var sessionId) && sessionById.ContainsKey(sessionId))
            .Select(part => ProjectEventEnvelope.SessionClosed(sessionById[loaded.SessionByTurnId[part.TurnId]], part))
            .ToList();
    }

    private static int? ReadWorkflowIssueNumber(string state)
    {
        try
        {
            using var document = JsonDocument.Parse(state);
            if (!document.RootElement.TryGetProperty("metadata", out var metadata)
                || !metadata.TryGetProperty("annotations", out var annotations)
                || !annotations.TryGetProperty("issueNumber", out var issueNumber))
                return null;

            return issueNumber.ValueKind switch
            {
                JsonValueKind.Number when issueNumber.TryGetInt32(out var number) => number,
                JsonValueKind.String when int.TryParse(issueNumber.GetString(), out var number) => number,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ProjectScope(
        IReadOnlyList<string> IssueSources,
        IReadOnlyList<string> WorkflowSources,
        IReadOnlyDictionary<string, int> WorkflowIssueNumbers,
        IReadOnlyList<ProjectEventSessionContext> AgentSessions)
    {
        public IReadOnlyList<string> AgentSessionSources => AgentSessions
            .Select(session => AgentSessionEventPersistence.AgentSessionSource(session.SessionId))
            .ToList();

        public bool IsEmpty =>
            IssueSources.Count == 0
            && WorkflowSources.Count == 0
            && AgentSessions.Count == 0;
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
    string? SessionSourceKind,
    string? WorkflowRunId,
    string? AgentId,
    string? AgentName)
{
    internal static ProjectEventEnvelope FromIssue(IssueEventRow row) =>
        Build(row.Id, row.Source, ProjectEventOrigin.Issue, "issue", row.Type, row.Time, row.EventId, row.SpecVersion, row.Subject, row.DataContentType, row.Data, row.ExtensionsJson);

    internal static ProjectEventEnvelope FromWorkflowRun(WorkflowRunEventRow row, int? issueNumber) =>
        Build(row.Id, row.Source, ProjectEventOrigin.WorkflowRun, "workflow-run", row.Type, row.Time, row.EventId, row.SpecVersion, row.Subject, row.DataContentType, row.Data, row.ExtensionsJson, issueNumber: issueNumber);

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
            issueNumber: session?.IssueNumber,
            sessionSourceKind: session?.SourceKind,
            workflowRunId: session?.WorkflowRunId,
            agentId: session?.AgentId,
            agentName: session?.AgentName);

    internal static ProjectEventEnvelope SessionOpened(ProjectEventSessionContext session, long id) =>
        SessionLifecycle(session, id, "coder_session_started", ToData(new { status = "opened" }), ToOffset(session.CreatedAt), $"{session.SessionId}:opened");

    internal static ProjectEventEnvelope SessionClosed(ProjectEventSessionContext session, AgentSessionTranscriptPartRow part) =>
        SessionLifecycle(session, part.Id, "session.closed", ToData(part.PayloadJson), ToOffset(part.LastSeenAt), $"{session.SessionId}:closed:{part.Id}");

    internal static string ExtractWorkflowRunId(string source) => ExtractAggregateId(source, "workflow-run");

    internal static string ExtractAgentSessionId(string source) => ExtractAggregateId(source, "agent-session");

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
        int? issueNumber = null,
        string? sessionSourceKind = null,
        string? workflowRunId = null,
        string? agentId = null,
        string? agentName = null) =>
        new(
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
            DeserializeExtensions(extensionsJson),
            runnerId ?? ResolveRunnerId(extensionsJson),
            issueNumber,
            sessionSourceKind,
            workflowRunId,
            agentId,
            agentName);

    private static string ExtractAggregateId(string source, string aggregateKind)
    {
        var prefix = aggregateKind switch
        {
            "issue" => IssueEventPersistence.SourcePrefix,
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

    private static string? ResolveRunnerId(string extensionsJson)
    {
        if (string.IsNullOrWhiteSpace(extensionsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(extensionsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "runnerid", "runnerId", "runner_id", "runner" })
            {
                if (document.RootElement.TryGetProperty(name, out var property)
                    && property.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.GetString()))
                    return property.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

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

internal sealed record ProjectEventSessionContext(
    string SessionId,
    DateTime CreatedAt,
    string? SourceKind,
    string? WorkflowRunId,
    int? IssueNumber,
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
            row.LabelAgentId,
            row.LabelAgentName,
            row.RunnerId);
    }

    private static int? ReadNumber(string? value) => int.TryParse(value, out var number) ? number : null;
}
