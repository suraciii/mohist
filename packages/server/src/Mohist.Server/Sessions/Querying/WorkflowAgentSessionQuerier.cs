using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Storage;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Infrastructure.Persistence.Workflow;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Queries;
using Mohist.Server.Workflow.Storage;
using Mohist.Server.Workflow.Views;

namespace Mohist.Server.Sessions.Querying;

public class WorkflowAgentSessionQuerier
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly WorkflowQueryService _workflowReader;

    public WorkflowAgentSessionQuerier(IDbContextFactory<MohistDbContext> dbFactory, WorkflowQueryService workflowReader)
    {
        _dbFactory = dbFactory;
        _workflowReader = workflowReader;
    }

    public async Task<IReadOnlyList<WorkflowAgentSessionDto>> ListByWorkflowAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowAgentSessions.AsNoTracking()
            .Where(s => s.WorkflowRunId == workflowRunId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToWorkflowAgentSessionDto).ToList();
    }

    public async Task<WorkflowSessionDetailDto?> GetByWorkflowAsync(string workflowRunId, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await db.WorkflowAgentSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkflowRunId == workflowRunId && s.SessionName == sessionName, ct);
        if (session is null) return null;

        var events = await db.WorkflowAgentSessionEvents.AsNoTracking()
            .Where(e => e.SessionId == session.Id)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
        return new WorkflowSessionDetailDto(ToWorkflowDto(session), events.Select(ToEventDto).ToList());
    }

    public async Task<IReadOnlyList<WorkflowSessionDto>> ListByIssueAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowAgentSessions.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.IssueNumber == issueNumber)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToWorkflowDto).ToList();
    }

    public async Task<IReadOnlyList<WorkflowAgentSessionInfoDto>> ListCurrentAsync(string projectId, string? status = null, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<WorkflowAgentSessionRow> query = db.WorkflowAgentSessions.AsNoTracking().Where(s => s.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(status) && AgentSessionStatusNames.TryParse(status, out var s))
            query = query.Where(x => x.Status == AgentSessionStatusNames.ToName(s));
        var rows = await query.OrderByDescending(s => s.CreatedAt).Take(limit).ToListAsync(ct);
        rows = await ReconcileActiveSessionsAsync(db, rows, ct);
        var issueTitles = await LoadIssueTitlesAsync(db, projectId, rows.Select(r => r.IssueNumber), ct);
        return rows.Select(s => new WorkflowAgentSessionInfoDto(
            s.IssueNumber,
            IssueTitle(issueTitles, s.IssueNumber),
            s.Stage ?? string.Empty,
            s.Id,
            s.Status,
            s.Model,
            s.Title,
            s.CreatedAt.ToString("o"),
            s.CompletedAt?.ToString("o"),
            (s.LastDataAt ?? s.StartedAt ?? s.CreatedAt).ToString("o"),
            s.ResolvedModel,
            s.InputTokens,
            s.OutputTokens,
            s.TotalTokens,
            s.CachedReadTokens,
            s.ThoughtTokens,
            s.CostAmount,
            s.CostCurrency,
            s.ContextWindowUsed,
            s.ContextWindowSize,
            s.FailureCategory,
            s.ToolCallCount,
            s.ToolErrorCount)).ToList();
    }

    public async Task<IReadOnlyList<WorkflowAgentSessionSummaryDto>> ListSummariesByIssueAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowAgentSessions.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.IssueNumber == issueNumber)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToSummaryDto).ToList();
    }

    public async Task<AgentSessionMetadataDto?> GetSessionMetadataAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (session is null) return null;

        var eventQuery = db.WorkflowAgentSessionEvents.AsNoTracking()
            .Where(e => e.SessionId == session.Id);
        var eventCount = await eventQuery.CountAsync(ct);
        var toolCount = await eventQuery.CountAsync(e => e.Type == "tool_call" || e.Type == "tool_call_update", ct);

        return new AgentSessionMetadataDto(
            session.Id,
            session.SessionName,
            session.AgentSessionId ?? session.Id,
            session.Status,
            session.Model,
            session.Stage,
            session.Title,
            session.CreatedAt.ToString("o"),
            session.CompletedAt?.ToString("o"),
            session.ResolvedModel,
            session.InputTokens,
            session.OutputTokens,
            session.TotalTokens,
            session.CachedReadTokens,
            session.ThoughtTokens,
            session.CostAmount,
            session.CostCurrency,
            session.ContextWindowUsed,
            session.ContextWindowSize,
            session.FailureCategory,
            session.ToolCallCount,
            session.ToolErrorCount,
            new AgentSessionMetadataCounts(eventCount, toolCount));
    }

    public async Task<AgentSessionEventsResponse?> GetSessionEventsAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (session is null) return null;

        var events = await db.WorkflowAgentSessionEvents.AsNoTracking()
            .Where(e => e.SessionId == session.Id)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);

        return new AgentSessionEventsResponse(
            events.Select(e => new AgentSessionEventDto(
                e.Id,
                e.Sequence,
                e.Type,
                ParsePayload(e.PayloadJson),
                e.CreatedAt.ToString("o"))).ToList());
    }

    private static async Task<WorkflowAgentSessionRow?> FindCurrentSessionAsync(
        MohistDbContext db,
        string projectId,
        int issueNumber,
        string sessionName,
        CancellationToken ct)
    {
        var issue = await db.IssueStates.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == $"{projectId}:{issueNumber}", ct);
        var workflowRunId = issue is null
            ? null
            : IssueSnapshot.DeserializeIssue(issue.StateJson)?.WorkflowRunId;
        if (workflowRunId is null) return null;

        return await db.WorkflowAgentSessions.AsNoTracking()
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
        var sessions = await db.WorkflowAgentSessions.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
        sessions = await ReconcileActiveSessionsAsync(db, sessions, ct);

        var sessionIds = sessions.Select(s => s.Id).ToArray();
        var latestEvents = await LoadLatestEventsAsync(db, sessionIds, ct);
        var issueTitles = await LoadIssueTitlesAsync(db, projectId, sessions.Select(s => s.IssueNumber), ct);
        var taskProgressMap = await BuildTaskProgressMapAsync(sessions, ct);

        var cards = sessions
            .Select(s => ToActivityCard(s, latestEvents.GetValueOrDefault(s.Id), IssueTitle(issueTitles, s.IssueNumber), taskProgressMap.GetValueOrDefault(s.Id)))
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

    private async Task<Dictionary<string, ActivityTaskProgressDto>> BuildTaskProgressMapAsync(List<WorkflowAgentSessionRow> sessions, CancellationToken ct)
    {
        var result = new Dictionary<string, ActivityTaskProgressDto>(StringComparer.Ordinal);
        var workflowRunIds = sessions.Select(s => s.WorkflowRunId).Distinct(StringComparer.Ordinal).ToArray();
        if (workflowRunIds.Length == 0) return result;

        var statusTasks = workflowRunIds.Select(async wrid =>
        {
            var status = await _workflowReader.GetStatusAsync(wrid);
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

    private static async Task<Dictionary<string, WorkflowAgentSessionEventRow>> LoadLatestEventsAsync(
        MohistDbContext db, string[] sessionIds, CancellationToken ct)
    {
        if (sessionIds.Length == 0) return [];

        var latestSeqs = await db.WorkflowAgentSessionEvents.AsNoTracking()
            .Where(e => sessionIds.Contains(e.SessionId))
            .GroupBy(e => e.SessionId)
            .Select(g => new { SessionId = g.Key, Sequence = g.Max(e => e.Sequence) })
            .ToListAsync(ct);
        if (latestSeqs.Count == 0) return [];

        var latestBySession = latestSeqs.ToDictionary(e => e.SessionId, e => e.Sequence);
        var events = await db.WorkflowAgentSessionEvents.AsNoTracking()
            .Where(e => sessionIds.Contains(e.SessionId))
            .ToListAsync(ct);

        return events
            .Where(e => latestBySession.TryGetValue(e.SessionId, out var seq) && e.Sequence == seq)
            .ToDictionary(e => e.SessionId);
    }

    private static ActivityCardDto ToActivityCard(WorkflowAgentSessionRow s, WorkflowAgentSessionEventRow? latestEvent, string issueTitle, ActivityTaskProgressDto? taskProgress)
    {
        var lastActivityAt = (s.LastDataAt ?? s.StartedAt ?? s.CreatedAt).ToString("o");
        return new ActivityCardDto(
            $"issue_{s.ProjectId}_{s.IssueNumber}",
            s.IssueNumber,
            issueTitle,
            s.Stage ?? string.Empty,
            null,
            s.Id,
            s.Status,
            s.Model,
            s.Title,
            s.CreatedAt.ToString("o"),
            s.CompletedAt?.ToString("o"),
            lastActivityAt,
            new ActivityWorkItemDto(s.WorkType ?? "task", s.WorkId ?? s.SessionName, s.Title ?? s.SessionName, s.Stage, null),
            taskProgress,
            latestEvent is null ? null : ToPreview(latestEvent),
            s.FailureReason,
            s.ResolvedModel,
            s.InputTokens,
            s.OutputTokens,
            s.TotalTokens,
            s.CachedReadTokens,
            s.ThoughtTokens,
            s.CostAmount,
            s.CostCurrency,
            s.ContextWindowUsed,
            s.ContextWindowSize,
            s.FailureCategory,
            s.ToolCallCount,
            s.ToolErrorCount);
    }

    private static async Task<Dictionary<int, string>> LoadIssueTitlesAsync(
        MohistDbContext db,
        string projectId,
        IEnumerable<int> issueNumbers,
        CancellationToken ct)
    {
        var numbers = issueNumbers.Distinct().ToArray();
        if (numbers.Length == 0) return [];

        var keys = numbers.Select(n => $"{projectId}:{n}").ToArray();
        var rows = await db.IssueStates.AsNoTracking()
            .Where(r => keys.Contains(r.Key))
            .Select(r => r.StateJson)
            .ToListAsync(ct);

        return rows
            .Select(Issue.Storage.IssueSnapshot.DeserializeIssue)
            .Where(i => i is not null && i.ProjectId == projectId)
            .Cast<Issue.Domain.Issue>()
            .Where(i => numbers.Contains(i.Number))
            .ToDictionary(i => i.Number, i => i.Title);
    }

    private static string IssueTitle(IReadOnlyDictionary<int, string> titles, int issueNumber) =>
        titles.TryGetValue(issueNumber, out var title) && !string.IsNullOrWhiteSpace(title)
            ? title
            : $"Issue #{issueNumber}";

    private static ActivityPreviewDto ToPreview(WorkflowAgentSessionEventRow e)
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

    private static WorkflowAgentSessionDto ToWorkflowAgentSessionDto(WorkflowAgentSessionRow s) => new(
        s.Id, s.ProjectId, s.IssueNumber, s.WorkflowRunId, s.SessionName,
        s.WorkId, s.WorkType, s.Stage, s.Title, s.RunnerId, s.AgentSessionId,
        s.Status, s.Model, s.WorkDir, s.ChangeDir, s.ProcessPid,
        s.CreatedAt.ToString("o"), s.StartedAt?.ToString("o"), s.CompletedAt?.ToString("o"),
        s.LastDataAt?.ToString("o"), s.FailureReason, s.ExitCode,
        s.ResolvedModel, s.InputTokens, s.OutputTokens, s.TotalTokens, s.CachedReadTokens, s.ThoughtTokens,
        s.CostAmount, s.CostCurrency, s.ContextWindowUsed, s.ContextWindowSize, s.FailureCategory,
        s.ToolCallCount, s.ToolErrorCount);

    private static WorkflowSessionDto ToWorkflowDto(WorkflowAgentSessionRow s) => new(
        s.Id, s.WorkflowRunId, s.SessionName, s.AgentSessionId,
        s.ProjectId, s.IssueNumber == 0 ? null : s.IssueNumber, s.RunnerId,
        s.Status, s.Model, s.WorkDir, s.ProcessPid,
        s.CreatedAt.ToString("o"), s.StartedAt?.ToString("o"), s.LastDataAt?.ToString("o"),
        s.CompletedAt?.ToString("o"), s.FailureReason, s.ExitCode);

    private static WorkflowAgentSessionSummaryDto ToSummaryDto(WorkflowAgentSessionRow s) => new(
        s.Id, s.SessionName, s.AgentSessionId ?? s.Id, s.WorkId, s.Title,
        s.Status, s.CreatedAt.ToString("o"), s.CompletedAt?.ToString("o"),
        s.Model, null, s.Stage, s.Title,
        s.LastDataAt?.ToString("o"), null, null, s.FailureReason,
        s.ResolvedModel, s.InputTokens, s.OutputTokens, s.TotalTokens, s.CachedReadTokens, s.ThoughtTokens,
        s.CostAmount, s.CostCurrency, s.ContextWindowUsed, s.ContextWindowSize, s.FailureCategory,
        s.ToolCallCount, s.ToolErrorCount);

    private static WorkflowAgentSessionEventDto ToEventDto(WorkflowAgentSessionEventRow e) => new(
        e.Id.ToString(), e.SessionId, e.ProjectId, e.IssueNumber, e.WorkflowRunId,
        e.SessionName, e.AgentSessionId, e.WorkId, e.WorkType, e.Stage,
        e.Sequence, e.Type, ParsePayload(e.PayloadJson), e.CreatedAt.ToString("o"));

    private static async Task<List<WorkflowAgentSessionRow>> ReconcileActiveSessionsAsync(
        MohistDbContext db,
        List<WorkflowAgentSessionRow> sessions,
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
        return rows.ToDictionary(row => row.WorkflowRunId, row => WorkflowLeaseJson.Deserialize(row.StateJson), StringComparer.Ordinal);
    }

    private static bool MatchesLease(WorkflowAgentSessionRow session, WorkLease lease) =>
        string.Equals(session.RunnerId, lease.RunnerId, StringComparison.Ordinal)
        && string.Equals(session.WorkId, lease.WorkId, StringComparison.Ordinal);

    private static bool IsActiveSession(WorkflowAgentSessionRow session) =>
        session.CompletedAt is null
        && session.Status is "created" or "running" or "probing";
}
