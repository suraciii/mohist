using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Storage;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Infrastructure.Persistence.Workflow;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Sessions.Queries;

public class WorkflowAgentSessionQueryService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public WorkflowAgentSessionQueryService(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
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
            (s.LastDataAt ?? s.StartedAt ?? s.CreatedAt).ToString("o"))).ToList();
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

    public async Task<WorkflowAgentSessionTranscript?> GetTranscriptAsync(string projectId, int issueNumber, string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await db.WorkflowAgentSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.ProjectId == projectId && s.IssueNumber == issueNumber, ct);
        if (session is null) return null;

        return await BuildTranscriptAsync(db, session, ct);
    }

    public async Task<WorkflowAgentSessionTranscript?> GetCurrentWorkflowTranscriptAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var issue = await db.IssueStates.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == $"{projectId}:{issueNumber}", ct);
        var workflowRunId = issue is null
            ? null
            : IssueSnapshot.DeserializeIssue(issue.StateJson)?.WorkflowRunId;
        if (workflowRunId is null) return null;

        var session = await db.WorkflowAgentSessions.AsNoTracking()
            .FirstOrDefaultAsync(s =>
                s.ProjectId == projectId
                && s.IssueNumber == issueNumber
                && s.WorkflowRunId == workflowRunId
                && s.SessionName == sessionName,
                ct);
        if (session is null) return null;

        return await BuildTranscriptAsync(db, session, ct);
    }

    private static async Task<WorkflowAgentSessionTranscript> BuildTranscriptAsync(MohistDbContext db, WorkflowAgentSessionRow session, CancellationToken ct)
    {
        var events = await db.WorkflowAgentSessionEvents.AsNoTracking()
            .Where(e => e.SessionId == session.Id)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);

        var createdAt = session.StartedAt ?? session.CreatedAt;
        var assistant = BuildAssistantParts(session, events, createdAt);

        var turns = new[]
        {
            new
            {
                id = $"{session.Id}-turn-1",
                startedAt = createdAt.ToString("o"),
                completedAt = session.CompletedAt?.ToString("o"),
                user = new { role = "mohist", text = session.Title ?? session.SessionName, kind = "task", sentAt = createdAt.ToString("o") },
                assistant,
            }
        };

        var metadata = new
        {
            sessionId = session.Id,
            sessionName = session.SessionName,
            coderSessionId = session.Id,
            issueId = session.IssueNumber.ToString(),
            acpSessionId = session.AgentSessionId ?? session.Id,
            executionId = session.WorkId,
            title = session.Title,
            status = session.Status,
            model = session.Model,
            stage = session.Stage,
            createdAt = session.CreatedAt.ToString("o"),
            completedAt = session.CompletedAt?.ToString("o"),
            cwd = session.WorkDir,
            worktree = session.WorkDir,
            firstPromptSentAt = createdAt.ToString("o"),
            lastActivityAt = (session.LastDataAt ?? session.StartedAt ?? session.CreatedAt).ToString("o"),
            lastDataAt = session.LastDataAt?.ToString("o"),
            failureReason = session.FailureReason,
            eventCount = events.Count,
            toolCount = events.Count(e => e.Type is "tool_call" or "tool_call_update"),
            turnCount = turns.Length,
        };

        return new WorkflowAgentSessionTranscript(
            session.Id,
            session.SessionName,
            session.AgentSessionId ?? session.Id,
            session.WorkId,
            session.Title,
            session.Status,
            session.CreatedAt.ToString("o"),
            session.CompletedAt?.ToString("o"),
            session.Model,
            null,
            session.Stage,
            session.Title,
            metadata,
            turns,
            session.CompletedAt is null,
            events.Select(e => new WorkflowAgentSessionTranscriptItem(e.Id.ToString(), e.Type, ParsePayload(e.PayloadJson), e.CreatedAt.ToString("o"))).ToList());
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

        var cards = sessions
            .Select(s => ToActivityCard(s, latestEvents.GetValueOrDefault(s.Id), IssueTitle(issueTitles, s.IssueNumber)))
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

    private static async Task<Dictionary<string, WorkflowAgentSessionEventRow>> LoadLatestEventsAsync(
        MohistDbContext db, string[] sessionIds, CancellationToken ct)
    {
        if (sessionIds.Length == 0) return [];

        var latestSeqs = await db.WorkflowAgentSessionEvents.AsNoTracking()
            .Where(e => sessionIds.Contains(e.SessionId) && e.Type != "agent_session_terminal" && e.Type != "agent_liveness_status")
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

    private static ActivityCardDto ToActivityCard(WorkflowAgentSessionRow s, WorkflowAgentSessionEventRow? latestEvent, string issueTitle)
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
            null,
            latestEvent is null ? null : ToPreview(latestEvent),
            s.FailureReason);
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

    private static string ExtractText(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(json);
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("text", out var text))
                return text.GetString() ?? string.Empty;
            if (payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("text", out var contentText))
                return contentText.GetString() ?? string.Empty;
            if (payload.ValueKind == JsonValueKind.String)
                return payload.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static List<JsonElement> BuildAssistantParts(WorkflowAgentSessionRow session, IReadOnlyList<WorkflowAgentSessionEventRow> events, DateTime createdAt)
    {
        var parts = new List<JsonElement>();
        var openText = new TextAccumulator("text", createdAt);
        var openReasoning = new TextAccumulator("reasoning", createdAt);
        var tools = new Dictionary<string, int>(StringComparer.Ordinal);
        var toolParts = new Dictionary<string, ToolPartProjection>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            if (e.Type is "agent_message_chunk" or "agent_output_chunk")
            {
                AppendTextPart(parts, openText, ExtractText(e.PayloadJson), e.CreatedAt, session.CompletedAt);
                continue;
            }

            if (e.Type == "agent_thought_chunk")
            {
                AppendTextPart(parts, openReasoning, ExtractText(e.PayloadJson), e.CreatedAt, session.CompletedAt);
                continue;
            }

            if (e.Type is "tool_call" or "tool_call_update")
            {
                var tool = ParseToolCall(e.PayloadJson, e.Type, e.CreatedAt);
                if (tool is null) continue;

                var toolCallId = tool.Tool.ToolCallId;
                if (tools.TryGetValue(toolCallId, out var index))
                {
                    var merged = MergeToolPart(toolParts[toolCallId], tool);
                    toolParts[toolCallId] = merged;
                    parts[index] = ToJsonElement(merged);
                }
                else
                {
                    tools[toolCallId] = parts.Count;
                    toolParts[toolCallId] = tool;
                    parts.Add(ToJsonElement(tool));
                }
            }
        }

        if (parts.Count == 0 && session.CompletedAt is null)
            return parts;

        return parts;
    }

    private static void AppendTextPart(List<JsonElement> parts, TextAccumulator accumulator, string text, DateTime at, DateTime? completedAt)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (string.IsNullOrEmpty(accumulator.Text))
        {
            accumulator.StartedAt = at;
            accumulator.Index = parts.Count;
            parts.Add(accumulator.ToPart(completedAt));
        }

        accumulator.Text += text;
        parts[accumulator.Index] = accumulator.ToPart(completedAt);
    }

    private static ToolPartProjection MergeToolPart(ToolPartProjection existing, ToolPartProjection next)
    {
        var tool = existing.Tool;
        var update = next.Tool;
        var status = update.Status is "pending" ? tool.Status : update.Status;
        return existing with
        {
            Tool = tool with
            {
                NormalizedName = update.NormalizedName ?? tool.NormalizedName,
                DisplayTitle = update.DisplayTitle ?? tool.DisplayTitle,
                DisplaySubtitle = update.DisplaySubtitle ?? tool.DisplaySubtitle,
                Category = update.Category ?? tool.Category,
                ToolName = update.ToolName == "unknown" ? tool.ToolName : update.ToolName,
                Status = status,
                Title = update.Title ?? tool.Title,
                Input = update.Input ?? tool.Input,
                Output = update.Output ?? tool.Output,
                Error = update.Error ?? tool.Error,
                CompletedAt = IsTerminalToolStatus(status) ? update.CompletedAt ?? tool.CompletedAt : tool.CompletedAt,
                RawInput = update.RawInput ?? tool.RawInput,
                RawOutput = update.RawOutput ?? tool.RawOutput,
                Metadata = update.Metadata ?? tool.Metadata,
                Details = update.Details ?? tool.Details,
            }
        };
    }

    private static ToolPartProjection? ParseToolCall(string json, string eventType, DateTime createdAt)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var payload = doc.RootElement;
            var nested = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("toolCall", out var toolCall)
                ? toolCall
                : default;
            var toolCallId = GetString(nested, "toolCallId")
                ?? GetString(payload, "toolCallId")
                ?? GetString(payload, "id")
                ?? GetString(payload, "callId");
            if (string.IsNullOrWhiteSpace(toolCallId)) return null;

            var toolName = GetString(nested, "toolName")
                ?? GetString(payload, "toolName")
                ?? GetString(payload, "name")
                ?? GetString(payload, "kind")
                ?? "unknown";
            var status = GetString(nested, "status")
                ?? GetString(payload, "status")
                ?? (eventType == "tool_call_update" ? "completed" : "pending");
            var state = MapToolStatus(status);
            var title = GetString(nested, "title") ?? GetString(payload, "title");
            var rawInput = CloneProperty(nested, "input") ?? CloneProperty(payload, "rawInput") ?? CloneProperty(payload, "input");
            var rawOutput = CloneProperty(nested, "output") ?? CloneProperty(payload, "rawOutput") ?? CloneProperty(payload, "output");

            return new ToolPartProjection(
                $"{toolCallId}-{eventType}-{createdAt.Ticks}",
                "tool",
                new ToolProjection(
                    toolCallId,
                    GetString(nested, "normalizedName") ?? GetString(payload, "normalizedName"),
                    GetString(nested, "displayTitle") ?? GetString(payload, "displayTitle"),
                    GetString(nested, "displaySubtitle") ?? GetString(payload, "displaySubtitle"),
                    GetString(nested, "category") ?? GetString(payload, "category"),
                    toolName,
                    state,
                    title,
                    null,
                    rawInput.HasValue ? rawInput.Value.GetRawText() : null,
                    rawOutput.HasValue ? rawOutput.Value.GetRawText() : null,
                    state == "failed" && rawOutput.HasValue ? rawOutput.Value.GetRawText() : null,
                    createdAt.ToString("o"),
                    IsTerminalToolStatus(state) ? createdAt.ToString("o") : null,
                    rawInput,
                    rawOutput,
                    CloneProperty(nested, "metadata") ?? CloneProperty(payload, "metadata") ?? CloneProperty(payload, "rawOutputMetadata"),
                    CloneProperty(nested, "details") ?? CloneProperty(payload, "details")));
        }
        catch
        {
            return null;
        }
    }

    private static string MapToolStatus(string status) => status switch
    {
        "pending" => "pending",
        "in_progress" or "running" or "started" => "running",
        "completed" => "completed",
        "failed" or "timeout" => "failed",
        "cancelled" => "cancelled",
        _ => "pending"
    };

    private static bool IsTerminalToolStatus(string status) =>
        status is "completed" or "failed" or "cancelled";

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static JsonElement? CloneProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.Clone();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "\u2026";

    private static JsonElement ToJsonElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class TextAccumulator(string type, DateTime startedAt)
    {
        public int Index { get; set; } = -1;
        public string Text { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; } = startedAt;

        public JsonElement ToPart(DateTime? completedAt) => ToJsonElement(new
        {
            id = $"{type}-{StartedAt.Ticks}",
            type,
            text = Text,
            startedAt = StartedAt.ToString("o"),
            completedAt = completedAt?.ToString("o")
        });
    }

    private sealed record ToolPartProjection(string Id, string Type, ToolProjection Tool);

    private sealed record ToolProjection(
        string ToolCallId,
        string? NormalizedName,
        string? DisplayTitle,
        string? DisplaySubtitle,
        string? Category,
        string ToolName,
        string Status,
        string? Title,
        string? Target,
        string? Input,
        string? Output,
        string? Error,
        string StartedAt,
        string? CompletedAt,
        JsonElement? RawInput,
        JsonElement? RawOutput,
        JsonElement? Metadata,
        JsonElement? Details);

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
        s.LastDataAt?.ToString("o"), s.FailureReason, s.ExitCode);

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
        s.LastDataAt?.ToString("o"), null, null, s.FailureReason);

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
        var rows = await db.WorkflowQueue.AsNoTracking()
            .Where(row => workflowIds.Contains(row.WorkflowRunId))
            .ToListAsync(ct);
        return rows.ToDictionary(row => row.WorkflowRunId, QueueLease, StringComparer.Ordinal);
    }

    private static WorkLease? QueueLease(WorkflowQueueRow row)
    {
        if (row.State != WorkflowQueueStates.Leased
            || string.IsNullOrWhiteSpace(row.WorkId)
            || string.IsNullOrWhiteSpace(row.WorkType)
            || string.IsNullOrWhiteSpace(row.Stage)
            || string.IsNullOrWhiteSpace(row.LogicalId))
            return null;

        return new WorkLease(row.WorkId, row.WorkType, row.Stage, row.LogicalId, row.Title, row.RunnerId);
    }

    private static bool MatchesLease(WorkflowAgentSessionRow session, WorkLease lease) =>
        string.Equals(session.RunnerId, lease.RunnerId, StringComparison.Ordinal)
        && string.Equals(session.WorkId, lease.WorkId, StringComparison.Ordinal);

    private static bool IsActiveSession(WorkflowAgentSessionRow session) =>
        session.CompletedAt is null
        && session.Status is "created" or "running" or "probing";
}
