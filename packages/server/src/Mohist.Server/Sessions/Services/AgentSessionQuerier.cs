using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Services;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Sessions.Services;

public class AgentSessionQuerier
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly WorkflowQuerier _workflowQuerier;

    public AgentSessionQuerier(IDbContextFactory<MohistDbContext> dbFactory, WorkflowQuerier workflowQuerier)
    {
        _dbFactory = dbFactory;
        _workflowQuerier = workflowQuerier;
    }

    public async Task<IReadOnlyList<AgentSessionDto>> ListByWorkflowAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentSessions.AsNoTracking()
            .Where(s => s.WorkflowRunId == workflowRunId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return ToDomain(rows).Select(x => ToAgentSessionDto(x.Session, x.Row)).ToList();
    }

    public async Task<WorkflowSessionDetailDto?> GetByWorkflowAsync(string workflowRunId, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await db.AgentSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkflowRunId == workflowRunId && s.SessionName == sessionName, ct);
        if (session is null) return null;

        var events = await db.AgentSessionRuntimeEvents.AsNoTracking()
            .Where(e => e.SessionId == session.Id)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
        var domainSession = AgentSessionJson.Deserialize(session);
        return domainSession is null ? null : new WorkflowSessionDetailDto(ToWorkflowDto(domainSession), events.Select(ToEventDto).ToList());
    }

    public async Task<IReadOnlyList<WorkflowSessionDto>> ListByIssueAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentSessions.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.IssueNumber == issueNumber)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return ToDomain(rows).Select(x => ToWorkflowDto(x.Session)).ToList();
    }

    public async Task<IReadOnlyList<AgentSessionInfoDto>> ListCurrentAsync(string projectId, string? status = null, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<AgentSessionRow> query = db.AgentSessions.AsNoTracking().Where(s => s.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(status) && AgentSessionStatusNames.TryParse(status, out var s))
            query = query.Where(x => x.Status == AgentSessionStatusNames.ToName(s));
        var rows = await query.OrderByDescending(s => s.CreatedAt).Take(limit).ToListAsync(ct);
        rows = await ReconcileActiveSessionsAsync(db, rows, ct);
        var issueTitles = await LoadIssueTitlesAsync(db, projectId, rows.Select(r => r.IssueNumber), ct);
        var eventSummaries = await LoadEventSummariesAsync(db, rows.Select(r => r.Id), ct);
        return ToDomain(rows).Select(x =>
        {
            var s = x.Session;
            var row = x.Row;
            var events = eventSummaries.GetValueOrDefault(s.Id);
            var usage = Usage(s);
            return new AgentSessionInfoDto(
            IssueNumber(s),
            IssueTitle(issueTitles, IssueNumber(s)),
            row.Stage ?? string.Empty,
            s.Id,
            StatusName(s),
            s.Settings.Model,
            null,
            s.Status.CreatedAt.ToString("o"),
            s.Status.CompletedAt?.ToString("o"),
            LastActivityAt(s).ToString("o"),
            events?.ResolvedModel,
            usage.InputTokens,
            usage.OutputTokens,
            usage.TotalTokens,
            usage.CachedReadTokens,
            usage.ThoughtTokens,
            usage.CostAmount,
            usage.CostCurrency,
            usage.ContextWindowUsed,
            usage.ContextWindowSize,
            events?.FailureCategory,
            events?.ToolCallCount,
            events?.ToolErrorCount);
        }).ToList();
    }

    public async Task<IReadOnlyList<AgentSessionSummaryDto>> ListSummariesByIssueAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentSessions.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.IssueNumber == issueNumber)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return ToDomain(rows).Select(x => ToSummaryDto(x.Session, x.Row)).ToList();
    }

    public async Task<AgentSessionMetadataDto?> GetSessionMetadataAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (session is null) return null;
        var domainSession = AgentSessionJson.Deserialize(session);
        if (domainSession is null) return null;

        var eventQuery = db.AgentSessionRuntimeEvents.AsNoTracking()
            .Where(e => e.SessionId == session.Id);
        var eventCount = await eventQuery.CountAsync(ct);
        var toolCount = await eventQuery.CountAsync(e => e.Type == "tool_call" || e.Type == "tool_call_update", ct);
        var eventSummary = BuildEventSummary(await eventQuery.OrderBy(e => e.Sequence).ToListAsync(ct));
        var usage = Usage(domainSession);

        return new AgentSessionMetadataDto(
            domainSession.Id,
            domainSession.SessionName,
            domainSession.Status.AgentRuntimeSessionId ?? domainSession.Id,
            StatusName(domainSession),
            domainSession.Settings.Model,
            session.Stage,
            domainSession.Title,
            domainSession.Status.CreatedAt.ToString("o"),
            domainSession.Status.CompletedAt?.ToString("o"),
            eventSummary.ResolvedModel,
            usage.InputTokens,
            usage.OutputTokens,
            usage.TotalTokens,
            usage.CachedReadTokens,
            usage.ThoughtTokens,
            usage.CostAmount,
            usage.CostCurrency,
            usage.ContextWindowUsed,
            usage.ContextWindowSize,
            eventSummary.FailureCategory,
            eventSummary.ToolCallCount,
            eventSummary.ToolErrorCount,
            new AgentSessionMetadataCounts(eventCount, toolCount));
    }

    public async Task<AgentSessionRuntimeEventsResponse?> GetSessionEventsAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (session is null) return null;

        var events = await db.AgentSessionRuntimeEvents.AsNoTracking()
            .Where(e => e.SessionId == session.Id)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);

        return new AgentSessionRuntimeEventsResponse(
            events.Select(e => new AgentSessionRuntimeEventDto(
                e.Id,
                e.Sequence,
                e.Type,
                ParsePayload(e.PayloadJson),
                e.CreatedAt.ToString("o"))).ToList());
    }

    private static async Task<AgentSessionRow?> FindCurrentSessionAsync(
        MohistDbContext db,
        string projectId,
        int issueNumber,
        string sessionName,
        CancellationToken ct)
    {
        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Number == issueNumber)
            .ToListAsync(ct);
        var issue = IssueRowMapper.Deserialize(rows)
            .FirstOrDefault(issue => IssueRowMapper.IsIssue(issue, projectId, issueNumber));
        var workflowRunId = issue?.WorkflowRunId;
        if (workflowRunId is null) return null;

        return await db.AgentSessions.AsNoTracking()
            .FirstOrDefaultAsync(s =>
                s.ProjectId == projectId
                && s.IssueNumber == issueNumber
                && s.WorkflowRunId == workflowRunId
                && s.SessionName == sessionName,
                ct);
    }

    public async Task<ActivityDto> GetActivityAsync(string projectId, int? limit = null, IReadOnlyList<ActivityWaitingCardDto>? waiting = null, IReadOnlyList<string>? runnerIds = null, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessions = await db.AgentSessions.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
        sessions = await ReconcileActiveSessionsAsync(db, sessions, ct);

        var sessionIds = sessions.Select(s => s.Id).ToArray();
        var latestEvents = await LoadLatestEventsAsync(db, sessionIds, ct);
        var eventSummaries = await LoadEventSummariesAsync(db, sessionIds, ct);
        var issueTitles = await LoadIssueTitlesAsync(db, projectId, sessions.Select(s => s.IssueNumber), ct);
        var taskProgressMap = await BuildTaskProgressMapAsync(sessions, ct);

        var cards = ToDomain(sessions)
            .Select(x => ToActivityCard(x.Session, x.Row, latestEvents.GetValueOrDefault(x.Session.Id), eventSummaries.GetValueOrDefault(x.Session.Id), IssueTitle(issueTitles, IssueNumber(x.Session)), taskProgressMap.GetValueOrDefault(x.Session.Id)))
            .ToList();

        waiting ??= [];
        var summary = new ActivitySummaryDto(
            cards.Count(c => c.Status is "created" or "running" or "probing"),
            waiting.Count,
            cards.Count(c => c.Status == "completed"),
            cards.Count(c => c.Status is "failed" or "cancelled"),
            new ActivitySlotUsageDto(cards.Count(c => c.Status is "created" or "running" or "probing"), (runnerIds?.Count ?? 0) + 1));

        return new ActivityDto(summary, cards, waiting.ToList());
    }

    private async Task<Dictionary<string, ActivityTaskProgressDto>> BuildTaskProgressMapAsync(List<AgentSessionRow> sessions, CancellationToken ct)
    {
        var result = new Dictionary<string, ActivityTaskProgressDto>(StringComparer.Ordinal);
        var workflowRunIds = sessions.Select(s => s.WorkflowRunId).Distinct(StringComparer.Ordinal).ToArray();
        if (workflowRunIds.Length == 0) return result;

        var statusTasks = workflowRunIds.Select(async wrid =>
        {
            var status = await _workflowQuerier.GetStatusAsync(wrid);
            return (WorkflowRunId: wrid, Status: status);
        });
        var statuses = await Task.WhenAll(statusTasks);
        var statusByWorkflow = statuses.ToDictionary(x => x.WorkflowRunId, x => x.Status, StringComparer.Ordinal);

        foreach (var session in sessions)
        {
            if (!statusByWorkflow.TryGetValue(session.WorkflowRunId, out var status) || status is null)
                continue;

            var currentStageId = status.CurrentStage;
            if (string.IsNullOrWhiteSpace(currentStageId))
                continue;

            var stage = status.Stages.FirstOrDefault(s => string.Equals(s.Stage, currentStageId, StringComparison.OrdinalIgnoreCase));
            if (stage is null)
                continue;

            var completed = stage.Tasks.Count(t => string.Equals(t.Status, "completed", StringComparison.OrdinalIgnoreCase));
            var total = stage.Tasks.Count;
            if (total > 0)
                result[session.Id] = new ActivityTaskProgressDto(completed, total);
        }

        return result;
    }

    private static async Task<Dictionary<string, AgentSessionRuntimeEventRow>> LoadLatestEventsAsync(
        MohistDbContext db, string[] sessionIds, CancellationToken ct)
    {
        if (sessionIds.Length == 0) return [];

        var latestSeqs = await db.AgentSessionRuntimeEvents.AsNoTracking()
            .Where(e => sessionIds.Contains(e.SessionId))
            .GroupBy(e => e.SessionId)
            .Select(g => new { SessionId = g.Key, Sequence = g.Max(e => e.Sequence) })
            .ToListAsync(ct);
        if (latestSeqs.Count == 0) return [];

        var latestBySession = latestSeqs.ToDictionary(e => e.SessionId, e => e.Sequence);
        var events = await db.AgentSessionRuntimeEvents.AsNoTracking()
            .Where(e => sessionIds.Contains(e.SessionId))
            .ToListAsync(ct);

        return events
            .Where(e => latestBySession.TryGetValue(e.SessionId, out var seq) && e.Sequence == seq)
            .ToDictionary(e => e.SessionId);
    }

    private static async Task<Dictionary<string, AgentSessionRuntimeEventSummary>> LoadEventSummariesAsync(
        MohistDbContext db, IEnumerable<string> sessionIds, CancellationToken ct)
    {
        var ids = sessionIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return [];

        var events = await db.AgentSessionRuntimeEvents.AsNoTracking()
            .Where(e => ids.Contains(e.SessionId))
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);

        return events
            .GroupBy(e => e.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => BuildEventSummary(g), StringComparer.Ordinal);
    }

    private static AgentSessionRuntimeEventSummary BuildEventSummary(IEnumerable<AgentSessionRuntimeEventRow> events)
    {
        string? resolvedModel = null;
        string? failureCategory = null;
        var toolCalls = 0;
        var toolErrors = 0;

        foreach (var e in events)
        {
            if (e.Type == "agent_session_model_resolved")
            {
                var payload = ParsePayload(e.PayloadJson);
                resolvedModel = GetStringProp(payload, "resolvedModel") ?? resolvedModel;
            }
            else if (e.Type == "agent_session_terminal")
            {
                var payload = ParsePayload(e.PayloadJson);
                failureCategory = GetStringProp(payload, "failureCategory") ?? failureCategory;
            }
            else if (e.Type == "tool_call")
            {
                toolCalls++;
            }
            else if (e.Type == "tool_call_update")
            {
                var payload = ParsePayload(e.PayloadJson);
                var status = GetStringProp(payload, "status") ?? GetStringProp(payload, "state");
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    toolErrors++;
            }
        }

        return new AgentSessionRuntimeEventSummary(
            resolvedModel,
            failureCategory,
            toolCalls == 0 ? null : toolCalls,
            toolErrors == 0 ? null : Math.Min(toolErrors, Math.Max(toolCalls, toolErrors)));
    }

    private static ActivityCardDto ToActivityCard(AgentSession s, AgentSessionRow row, AgentSessionRuntimeEventRow? latestEvent, AgentSessionRuntimeEventSummary? eventSummary, string issueTitle, ActivityTaskProgressDto? taskProgress)
    {
        var lastActivityAt = LastActivityAt(s).ToString("o");
        var usage = Usage(s);
        return new ActivityCardDto(
            $"issue_{s.ProjectId}_{IssueNumber(s)}",
            IssueNumber(s),
            issueTitle,
            row.Stage ?? string.Empty,
            null,
            s.Id,
            StatusName(s),
            s.Settings.Model,
            null,
            s.Status.CreatedAt.ToString("o"),
            s.Status.CompletedAt?.ToString("o"),
            lastActivityAt,
            new ActivityWorkItemDto(row.WorkType ?? "task", row.WorkId ?? s.SessionName, row.WorkId ?? s.SessionName, row.Stage, null),
            taskProgress,
            latestEvent is null ? null : ToPreview(latestEvent),
            s.Status.FailureReason,
            eventSummary?.ResolvedModel,
            usage.InputTokens,
            usage.OutputTokens,
            usage.TotalTokens,
            usage.CachedReadTokens,
            usage.ThoughtTokens,
            usage.CostAmount,
            usage.CostCurrency,
            usage.ContextWindowUsed,
            usage.ContextWindowSize,
            eventSummary?.FailureCategory,
            eventSummary?.ToolCallCount,
            eventSummary?.ToolErrorCount);
    }

    private static async Task<Dictionary<int, string>> LoadIssueTitlesAsync(
        MohistDbContext db,
        string projectId,
        IEnumerable<int> issueNumbers,
        CancellationToken ct)
    {
        var numbers = issueNumbers.Distinct().ToArray();
        if (numbers.Length == 0) return [];

        var rows = await db.Issues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.Number != null && numbers.Contains(row.Number.Value))
            .ToListAsync(ct);

        return IssueRowMapper.ByNumber(rows, projectId, numbers)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Title);
    }

    private static string IssueTitle(IReadOnlyDictionary<int, string> titles, int issueNumber) =>
        titles.TryGetValue(issueNumber, out var title) && !string.IsNullOrWhiteSpace(title)
            ? title
            : $"Issue #{issueNumber}";

    private static ActivityPreviewDto ToPreview(AgentSessionRuntimeEventRow e)
    {
        var text = ExtractPreviewText(e.PayloadJson);
        var kind = e.Type.Contains("tool", StringComparison.OrdinalIgnoreCase) ? "tool" : "text";
        return new ActivityPreviewDto(kind, string.IsNullOrWhiteSpace(text) ? e.Type : Truncate(text, 120), e.CreatedAt.ToString("o"));
    }

    private static string ExtractPreviewText(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(json);
            if (payload.ValueKind == JsonValueKind.String) return payload.GetString() ?? string.Empty;
            if (payload.ValueKind != JsonValueKind.Object) return string.Empty;
            foreach (var key in new[] { "title", "toolName", "text", "message", "command" })
            {
                if (payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? string.Empty;
            }
            if (payload.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("text", out var contentText)
                && contentText.ValueKind == JsonValueKind.String)
                return contentText.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "\u2026";

    private static JsonElement? ParsePayload(string json)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringProp(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object) return null;
        return element.Value.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static AgentSessionDto ToAgentSessionDto(AgentSession s, AgentSessionRow row)
    {
        var usage = Usage(s);
        return new AgentSessionDto(
            s.Id, s.ProjectId, IssueNumber(s), s.RunId, s.SessionName,
            row.WorkId, row.WorkType, row.Stage, s.Title, s.Runtime.RunnerId, s.Status.AgentRuntimeSessionId,
            StatusName(s), s.Settings.Model, s.Runtime.WorkDir, s.ChangeDir, null,
            s.Status.CreatedAt.ToString("o"), s.Status.StartedAt?.ToString("o"), s.Status.CompletedAt?.ToString("o"),
            s.Status.LastDataAt?.ToString("o"), s.Status.FailureReason, s.Status.ExitCode,
            null, usage.InputTokens, usage.OutputTokens,
            usage.TotalTokens, usage.CachedReadTokens, usage.ThoughtTokens,
            usage.CostAmount, usage.CostCurrency,
            usage.ContextWindowUsed, usage.ContextWindowSize, null,
            null, null);
    }

    private static WorkflowSessionDto ToWorkflowDto(AgentSession s) => new(
        s.Id, s.RunId, s.SessionName, s.Status.AgentRuntimeSessionId,
        s.ProjectId, IssueNumber(s) == 0 ? null : IssueNumber(s), s.Runtime.RunnerId,
        StatusName(s), s.Settings.Model, s.Runtime.WorkDir, null,
        s.Status.CreatedAt.ToString("o"), s.Status.StartedAt?.ToString("o"), s.Status.LastDataAt?.ToString("o"),
        s.Status.CompletedAt?.ToString("o"), s.Status.FailureReason, s.Status.ExitCode);

    private static AgentSessionSummaryDto ToSummaryDto(AgentSession s, AgentSessionRow row)
    {
        var usage = Usage(s);
        return new AgentSessionSummaryDto(
            s.Id, s.SessionName, s.Status.AgentRuntimeSessionId ?? s.Id, row.WorkId, s.Title,
            StatusName(s), s.Status.CreatedAt.ToString("o"), s.Status.CompletedAt?.ToString("o"),
            s.Settings.Model, null, row.Stage, s.Title,
            s.Status.LastDataAt?.ToString("o"), null, null, s.Status.FailureReason,
            null, usage.InputTokens, usage.OutputTokens,
            usage.TotalTokens, usage.CachedReadTokens, usage.ThoughtTokens,
            usage.CostAmount, usage.CostCurrency,
            usage.ContextWindowUsed, usage.ContextWindowSize, null,
            null, null);
    }

    private static int IssueNumber(AgentSession session) => session.IssueNumber;
    private static string StatusName(AgentSession session) => AgentSessionStatusNames.ToName(session.Status.Phase);
    private static DateTime LastActivityAt(AgentSession session) =>
        session.Status.LastDataAt ?? session.Status.StartedAt ?? session.Status.CreatedAt;
    private static AgentUsageSummary Usage(AgentSession session) => session.Status.UsageSummary ?? new AgentUsageSummary();

    private static AgentSessionRuntimeEventLogDto ToEventDto(AgentSessionRuntimeEventRow e) => new(
        e.Id.ToString(), e.SessionId, e.ProjectId, e.IssueNumber, e.WorkflowRunId,
        e.SessionName, e.AgentSessionId, e.WorkId, e.WorkType, e.Stage,
        e.Sequence, e.Type, ParsePayload(e.PayloadJson), e.CreatedAt.ToString("o"));

    private static List<AgentSessionProjection> ToDomain(IEnumerable<AgentSessionRow> rows)
    {
        var result = new List<AgentSessionProjection>();
        foreach (var row in rows)
        {
            var session = AgentSessionJson.Deserialize(row);
            if (session is not null)
                result.Add(new AgentSessionProjection(row, session));
        }
        return result;
    }

    private static async Task<List<AgentSessionRow>> ReconcileActiveSessionsAsync(
        MohistDbContext db,
        List<AgentSessionRow> sessions,
        CancellationToken ct)
    {
        if (sessions.Count == 0) return sessions;

        var activeRows = sessions
            .Where(IsActiveSession)
            .ToList();
        if (activeRows.Count == 0) return sessions;

        var workflowIds = activeRows
            .Select(s => s.WorkflowRunId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var leases = await LoadLeasesAsync(db, workflowIds, ct);
        if (leases.Count == 0) return sessions;

        var allowedSessionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var activeSession in activeRows)
        {
            if (!leases.TryGetValue(activeSession.WorkflowRunId, out var lease) || lease is null)
            {
                allowedSessionIds.Add(activeSession.Id);
                continue;
            }

            if (MatchesLease(activeSession, lease))
                allowedSessionIds.Add(activeSession.Id);
        }

        return sessions
            .Where(s => !IsActiveSession(s) || allowedSessionIds.Contains(s.Id))
            .ToList();
    }

    private static async Task<Dictionary<string, WorkLease?>> LoadLeasesAsync(MohistDbContext db, string[] workflowIds, CancellationToken ct)
    {
        var rows = await db.WorkflowLeases.AsNoTracking()
            .Where(row => workflowIds.Contains(row.WorkflowRunId))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.WorkflowRunId, row => WorkflowLeaseJson.Deserialize(row.State), StringComparer.Ordinal);
    }

    private static bool MatchesLease(AgentSessionRow session, WorkLease lease) =>
        string.Equals(session.RunnerId, lease.RunnerId, StringComparison.Ordinal)
        && string.Equals(session.WorkId, lease.WorkId, StringComparison.Ordinal);

    private static bool IsActiveSession(AgentSessionRow session) =>
        session.CompletedAt is null
        && session.Status is "created" or "running" or "probing";
}

internal sealed record AgentSessionRuntimeEventSummary(
    string? ResolvedModel,
    string? FailureCategory,
    int? ToolCallCount,
    int? ToolErrorCount);

internal sealed record AgentSessionProjection(AgentSessionRow Row, AgentSession Session);
