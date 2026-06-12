using System.Text.Json;
using System.Text.Json.Serialization;
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

        var segments = await LoadTranscriptSegmentsAsync(db, session.Id, ct);
        var domainSession = AgentSessionJson.Deserialize(session);
        return domainSession is null ? null : new WorkflowSessionDetailDto(ToWorkflowDto(domainSession), BuildTranscriptResponse(segments));
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

        var transcriptEvents = (await LoadTranscriptSegmentsAsync(db, session.Id, ct)).Select(ToProjection).ToList();
        var segmentCount = transcriptEvents.Count;
        var eventSummary = BuildEventSummary(transcriptEvents);
        var toolCount = eventSummary.ToolCallCount ?? 0;
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
            new AgentSessionMetadataCounts(segmentCount, toolCount));
    }

    public async Task<AgentSessionTranscriptResponse?> GetSessionTranscriptAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (session is null) return null;

        var segments = await LoadTranscriptSegmentsAsync(db, session.Id, ct);
        return BuildTranscriptResponse(segments);
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

    private static async Task<Dictionary<string, TranscriptEventProjection>> LoadLatestEventsAsync(
        MohistDbContext db, string[] sessionIds, CancellationToken ct)
    {
        if (sessionIds.Length == 0) return [];

        var latestSegmentSeqs = await db.AgentSessionTranscriptSegments.AsNoTracking()
            .Where(e => sessionIds.Contains(e.SessionId))
            .GroupBy(e => e.SessionId)
            .Select(g => new { SessionId = g.Key, Sequence = g.Max(e => e.Sequence) })
            .ToListAsync(ct);

        var result = new Dictionary<string, TranscriptEventProjection>(StringComparer.Ordinal);
        if (latestSegmentSeqs.Count > 0)
        {
            var latestBySession = latestSegmentSeqs.ToDictionary(e => e.SessionId, e => e.Sequence);
            var segments = await db.AgentSessionTranscriptSegments.AsNoTracking()
                .Where(e => sessionIds.Contains(e.SessionId))
                .ToListAsync(ct);

            foreach (var e in segments.Where(e => latestBySession.TryGetValue(e.SessionId, out var seq) && e.Sequence == seq))
                result[e.SessionId] = ToProjection(e);
        }

        return result;
    }

    private static async Task<Dictionary<string, TranscriptEventSummary>> LoadEventSummariesAsync(
        MohistDbContext db, IEnumerable<string> sessionIds, CancellationToken ct)
    {
        var ids = sessionIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return [];

        var segments = await db.AgentSessionTranscriptSegments.AsNoTracking()
            .Where(e => ids.Contains(e.SessionId))
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);

        return segments
            .Select(ToProjection)
            .OrderBy(e => e.Sequence)
            .GroupBy(e => e.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => BuildEventSummary(g), StringComparer.Ordinal);
    }

    private static TranscriptEventSummary BuildEventSummary(IEnumerable<TranscriptEventProjection> events)
    {
        string? resolvedModel = null;
        string? failureCategory = null;
        var toolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var failedToolCallIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            if (e.Type == "model.resolved" || e.Type == "model")
            {
                var payload = ParsePayload(e.PayloadJson);
                resolvedModel = GetStringProp(payload, "resolvedModel") ?? resolvedModel;
            }
            else if (e.Type == "session.closed" || e.Type == "session_closed")
            {
                var payload = ParsePayload(e.PayloadJson);
                failureCategory = GetStringProp(payload, "failureCategory") ?? failureCategory;
            }
            else if (e.Type == "tool_call.started" || e.Type == "tool_call.updated" || e.Type == "tool_call.completed" || e.Type == "tool_call")
            {
                var payload = ParsePayload(e.PayloadJson);
                var toolCallId = GetToolStringProp(payload, "toolCallId")
                    ?? GetToolStringProp(payload, "id")
                    ?? GetToolStringProp(payload, "callId")
                    ?? e.Sequence.ToString();
                toolCallIds.Add(toolCallId);
                var status = GetToolStringProp(payload, "status") ?? GetToolStringProp(payload, "state");
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    failedToolCallIds.Add(toolCallId);
            }
        }

        return new TranscriptEventSummary(
            resolvedModel,
            failureCategory,
            toolCallIds.Count == 0 ? null : toolCallIds.Count,
            failedToolCallIds.Count == 0 ? null : failedToolCallIds.Count);
    }

    private static ActivityCardDto ToActivityCard(AgentSession s, AgentSessionRow row, TranscriptEventProjection? latestEvent, TranscriptEventSummary? eventSummary, string issueTitle, ActivityTaskProgressDto? taskProgress)
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

    private static ActivityPreviewDto ToPreview(TranscriptEventProjection e)
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

    private static JsonElement ParsePayloadOrEmpty(string json) =>
        ParsePayload(json) ?? JsonDocument.Parse("{}").RootElement.Clone();

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

    private static async Task<List<AgentSessionTranscriptSegmentRow>> LoadTranscriptSegmentsAsync(MohistDbContext db, string sessionId, CancellationToken ct) =>
        await db.AgentSessionTranscriptSegments.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

    private static AgentSessionTranscriptResponse BuildTranscriptResponse(IReadOnlyList<AgentSessionTranscriptSegmentRow> segments)
    {
        var turns = new List<AgentSessionTranscriptTurnDto>();
        var toolPartIndex = new Dictionary<string, AgentSessionTranscriptPartDto>(StringComparer.Ordinal);
        AgentSessionTranscriptTurnDto? current = null;
        var turnIndex = 0;
        var partIndex = 0;

        void FinalizeCurrent(string? completedAt)
        {
            if (current is null) return;
            if (completedAt is not null)
            {
                CloseOpenTextParts(current, completedAt);
                current.CompletedAt ??= completedAt;
            }
            current.Incomplete = false;
            turns.Add(current);
            current = null;
            toolPartIndex.Clear();
        }

        foreach (var segment in segments)
        {
            var payload = ParsePayloadOrEmpty(segment.PayloadJson);
            var at = segment.StartedAt.ToString("o");

            if (segment.Kind == "input")
            {
                FinalizeCurrent(at);
                current = new AgentSessionTranscriptTurnDto
                {
                    Id = $"turn-{turnIndex++}",
                    StartedAt = at,
                    CompletedAt = null,
                    Incomplete = false,
                    User = new AgentSessionTranscriptUserDto
                    {
                        Text = GetStringProp(payload, "text") ?? string.Empty,
                        Kind = NormalizePromptKind(GetStringProp(payload, "kind") ?? GetStringProp(payload, "source")),
                        SentAt = at,
                    },
                };
                partIndex = 0;
                continue;
            }

            current ??= new AgentSessionTranscriptTurnDto
            {
                Id = "turn-legacy-missing",
                StartedAt = at,
                CompletedAt = null,
                Incomplete = true,
                User = new AgentSessionTranscriptUserDto
                {
                    Text = string.Empty,
                    Kind = "legacy-missing",
                    SentAt = at,
                },
            };

            if (segment.Kind == "assistant_text" || segment.Kind == "assistant_reasoning")
            {
                var text = GetStringProp(payload, "text");
                if (!string.IsNullOrEmpty(text))
                {
                    current.Assistant.Add(new AgentSessionTranscriptPartDto
                    {
                        Id = $"{current.Id}-p{++partIndex}",
                        Type = segment.Kind == "assistant_text" ? "text" : "reasoning",
                        Text = text,
                        StartedAt = at,
                        CompletedAt = (segment.CompletedAt ?? segment.UpdatedAt).ToString("o"),
                    });
                }
                continue;
            }

            if (segment.Kind == "tool_call")
            {
                UpsertTranscriptToolPart(current, toolPartIndex, segment, payload, at, ref partIndex);
                continue;
            }

            if (segment.Kind == "status")
            {
                if (GetStringProp(payload, "status") == "failed")
                {
                    current.Assistant.Add(new AgentSessionTranscriptPartDto
                    {
                        Id = $"{current.Id}-p{++partIndex}",
                        Type = "error",
                        Message = GetStringProp(payload, "failureReason") ?? "Liveness failed",
                        Kind = "recovery",
                        At = at,
                    });
                }
                continue;
            }

            if (segment.Kind == "session_closed")
            {
                var status = GetStringProp(payload, "status") ?? "completed";
                current.CompletedAt = at;
                if (status is "failed" or "cancelled")
                {
                    current.Assistant.Add(new AgentSessionTranscriptPartDto
                    {
                        Id = $"{current.Id}-p{++partIndex}",
                        Type = "error",
                        Message = GetStringProp(payload, "failureReason") ?? $"Session {status}",
                        Kind = status == "cancelled" ? "cancelled" : "failed",
                        At = at,
                    });
                }
                continue;
            }
        }

        if (current is not null)
        {
            var completedAt = segments.Count > 0 ? segments[^1].UpdatedAt.ToString("o") : null;
            FinalizeCurrent(completedAt);
        }

        return new AgentSessionTranscriptResponse
        {
            Turns = turns,
            SegmentCount = segments.Count,
            LastActivityAt = segments.Count > 0 ? segments[^1].UpdatedAt.ToString("o") : null,
        };
    }

    private static void CloseOpenTextParts(AgentSessionTranscriptTurnDto turn, string completedAt)
    {
        foreach (var part in turn.Assistant)
        {
            if ((part.Type == "text" || part.Type == "reasoning") && part.CompletedAt is null)
                part.CompletedAt = completedAt;
        }
    }

    private static void UpsertTranscriptToolPart(
        AgentSessionTranscriptTurnDto turn,
        IDictionary<string, AgentSessionTranscriptPartDto> toolPartIndex,
        AgentSessionTranscriptSegmentRow segment,
        JsonElement payload,
        string at,
        ref int partIndex)
    {
        var toolCallId = GetToolStringProp(payload, "toolCallId")
            ?? GetToolStringProp(payload, "id")
            ?? GetToolStringProp(payload, "callId");
        if (string.IsNullOrWhiteSpace(toolCallId))
            return;

        var status = MapTranscriptToolStatus(GetToolStringProp(payload, "status") ?? GetToolStringProp(payload, "state"));
        var rawInput = GetToolRaw(payload, "rawInput") ?? GetToolRaw(payload, "input");
        var rawOutput = GetToolRaw(payload, "rawOutput") ?? GetToolRaw(payload, "output");

        if (toolPartIndex.TryGetValue(toolCallId, out var existing) && existing.Tool is not null)
        {
            existing.Tool.Status = status;
            existing.Tool.Title = GetToolStringProp(payload, "title") ?? existing.Tool.Title;
            existing.Tool.Input = rawInput ?? existing.Tool.Input;
            existing.Tool.Output = rawOutput ?? existing.Tool.Output;
            existing.Tool.RawInput = rawInput ?? existing.Tool.RawInput;
            existing.Tool.RawOutput = rawOutput ?? existing.Tool.RawOutput;
            existing.Tool.Error = status == "failed" ? rawOutput : existing.Tool.Error;
            if (status is "completed" or "failed" or "cancelled")
            {
                existing.Tool.CompletedAt = at;
                existing.CompletedAt = at;
            }
            return;
        }

        var toolName = GetToolStringProp(payload, "toolName")
            ?? GetToolStringProp(payload, "kind")
            ?? GetToolStringProp(payload, "name")
            ?? "unknown";
        var title = GetToolStringProp(payload, "title");
        var completedAt = status is "completed" or "failed" or "cancelled" ? at : null;
        var part = new AgentSessionTranscriptPartDto
        {
            Id = $"{turn.Id}-p{++partIndex}",
            Type = "tool",
            StartedAt = at,
            CompletedAt = completedAt,
            Tool = new AgentSessionTranscriptToolDto
            {
                ToolCallId = toolCallId,
                ToolName = toolName,
                NormalizedName = NormalizeToolName(toolName, title),
                Status = status,
                Title = title,
                Input = rawInput,
                Output = rawOutput,
                RawInput = rawInput,
                RawOutput = rawOutput,
                Error = status == "failed" ? rawOutput : null,
                StartedAt = at,
                CompletedAt = completedAt,
            },
        };
        toolPartIndex[toolCallId] = part;
        turn.Assistant.Add(part);
    }

    private static string NormalizePromptKind(string? kind) => kind switch
    {
        "initial" or "task" or "retry" or "followup" or "recovery" or "legacy-missing" => kind,
        _ => "task"
    };

    private static string MapTranscriptToolStatus(string? status) => status switch
    {
        "completed" => "completed",
        "failed" or "timeout" => "failed",
        "cancelled" => "cancelled",
        "running" or "in_progress" or "started" => "running",
        _ => "pending"
    };

    private static string NormalizeToolName(string toolName, string? title)
    {
        var value = !string.IsNullOrWhiteSpace(toolName) ? toolName : title;
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        return value.Trim().ToLowerInvariant().Replace(' ', '_');
    }

    private static string? GetToolStringProp(JsonElement payload, string name)
    {
        var direct = GetStringProp(payload, name);
        if (direct is not null) return direct;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("toolCall", out var toolCall)
            ? GetStringProp(toolCall, name)
            : null;
    }

    private static string? GetToolStringProp(JsonElement? payload, string name) =>
        payload is null ? null : GetToolStringProp(payload.Value, name);

    private static string? GetToolRaw(JsonElement payload, string name)
    {
        if (TryGetRaw(payload, name, out var raw)) return raw;
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("toolCall", out var toolCall)
            && TryGetRaw(toolCall, name, out raw))
            return raw;
        return null;
    }

    private static bool TryGetRaw(JsonElement payload, string name, out string? raw)
    {
        raw = null;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var prop))
            return false;
        raw = prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.GetRawText();
        return true;
    }

    private static TranscriptEventProjection ToProjection(AgentSessionTranscriptSegmentRow segment) => new()
    {
        Id = segment.Id,
        SessionId = segment.SessionId,
        ProjectId = segment.ProjectId,
        IssueNumber = segment.IssueNumber,
        WorkflowRunId = segment.WorkflowRunId,
        SessionName = segment.SessionName,
        AgentSessionId = segment.AgentSessionId,
        WorkId = segment.WorkId,
        WorkType = segment.WorkType,
        Stage = segment.Stage,
        Sequence = segment.Sequence,
        Type = segment.Kind,
        PayloadJson = segment.PayloadJson,
        CreatedAt = segment.StartedAt,
    };

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

        var runsByWorkflow = await LoadWorkflowRunsForReconciliationAsync(db, activeRows, ct);
        if (runsByWorkflow.Count == 0) return sessions;

        var allowedSessionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var activeSession in activeRows)
        {
            if (!runsByWorkflow.TryGetValue(activeSession.WorkflowRunId, out var run) || run is null)
            {
                allowedSessionIds.Add(activeSession.Id);
                continue;
            }

            if (WorkflowRunOwnsSession(run, activeSession))
                allowedSessionIds.Add(activeSession.Id);
        }

        return sessions
            .Where(s => !IsActiveSession(s) || allowedSessionIds.Contains(s.Id))
            .ToList();
    }

    private static WorkflowRun? DeserializeWorkflowRun(string json)
    {
        try { return JsonSerializer.Deserialize<WorkflowRun>(json, RunJsonOptions); }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions RunJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static async Task<Dictionary<string, WorkflowRun?>> LoadWorkflowRunsForReconciliationAsync(
        MohistDbContext db, List<AgentSessionRow> sessions, CancellationToken ct)
    {
        var workflowIds = sessions
            .Select(s => s.WorkflowRunId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var rows = await db.WorkflowRuns.AsNoTracking()
            .Where(r => workflowIds.Contains(r.WorkflowRunId))
            .ToListAsync(ct);

        var runs = new Dictionary<string, WorkflowRun?>(StringComparer.Ordinal);
        foreach (var row in rows)
            runs[row.WorkflowRunId] = DeserializeWorkflowRun(row.State);
        return runs;
    }

    private static bool WorkflowRunOwnsSession(WorkflowRun run, AgentSessionRow session)
    {
        if (run.ClaimedBy is null) return true;

        if (!string.Equals(run.ClaimedBy, session.RunnerId, StringComparison.Ordinal))
            return false;

        var runningTask = run.Stages
            .SelectMany(s => s.Tasks)
            .FirstOrDefault(t => t.Status == Workflow.Domain.Run.TaskRunStatus.Running);

        return runningTask is null || string.Equals(runningTask.Id, session.WorkId, StringComparison.Ordinal);
    }

    private static bool IsActiveSession(AgentSessionRow session) =>
        session.CompletedAt is null
        && session.Status is "created" or "running" or "probing";
}

internal sealed record TranscriptEventProjection
{
    public long Id { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public int IssueNumber { get; init; }
    public string WorkflowRunId { get; init; } = string.Empty;
    public string SessionName { get; init; } = string.Empty;
    public string? AgentSessionId { get; init; }
    public string? WorkId { get; init; }
    public string? WorkType { get; init; }
    public string? Stage { get; init; }
    public long Sequence { get; init; }
    public string Type { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = "{}";
    public DateTime CreatedAt { get; init; }
}

internal sealed record TranscriptEventSummary(
    string? ResolvedModel,
    string? FailureCategory,
    int? ToolCallCount,
    int? ToolErrorCount);

internal sealed record AgentSessionProjection(AgentSessionRow Row, AgentSession Session);
