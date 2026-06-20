using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Issue.Services;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Workflow.Services.Sessions;

/// <summary>
/// Queries <see cref="AgentSession"/> records in the context of workflow runs.
/// </summary>
/// <remarks>
/// <see cref="AgentSession"/> is a peer-level aggregate root, NOT a child of <see cref="WorkflowRun"/>.
/// The association between a session and a workflow run is by reference only — a <see cref="TaskRun"/>
/// refers to a session and the run is the aggregate root that contains that task.
/// No ownership relationship exists.
/// </remarks>
public class AgentSessionQuerier
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly WorkflowQuerier _workflowQuerier;
    private readonly AgentSessionQuery _sessionQuery;

    public AgentSessionQuerier(IDbContextFactory<MohistDbContext> dbFactory, WorkflowQuerier workflowQuerier, AgentSessionQuery sessionQuery)
    {
        _dbFactory = dbFactory;
        _workflowQuerier = workflowQuerier;
        _sessionQuery = sessionQuery;
    }

    public async Task<IReadOnlyList<WorkflowSessionDto>> ListByWorkflowAsync(string workflowRunId, CancellationToken ct = default)
    {
        var sessions = await _sessionQuery.ListByLabelsAsync(
            Labels((AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId)),
            ct: ct);
        return sessions.Select(ToWorkflowDto).ToList();
    }

    public async Task<WorkflowSessionDetailDto?> GetByWorkflowAsync(string workflowRunId, string sessionName, CancellationToken ct = default)
    {
        var session = await _sessionQuery.FirstByLabelsAsync(
            Labels(
                (AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId),
                (AgentSessionQueryMetadataKeys.SessionName, sessionName)),
            ct: ct);
        if (session is null) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var transcript = await LoadTranscriptAsync(db, session.Session.Id, ct);
        return new WorkflowSessionDetailDto(ToWorkflowDto(session), SessionTranscriptBuilder.Build(transcript));
    }

    public async Task<IReadOnlyList<WorkflowSessionDto>> ListByIssueAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        var sessions = await _sessionQuery.ListByLabelsAsync(
            Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString())),
            ct: ct);
        return sessions.Select(ToWorkflowDto).ToList();
    }

    public async Task<IReadOnlyList<AgentSessionInfoDto>> ListCurrentAsync(string projectId, string? status = null, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessions = (await _sessionQuery.ListByLabelsAsync(
                Labels((AgentSessionQueryMetadataKeys.ProjectId, projectId)),
                AgentSessionQueryOrder.CreatedDescending,
                ct: ct))
            .Where(session => MatchesStatus(session.Session, status))
            .Take(limit)
            .ToList();
        sessions = await ReconcileActiveSessionsAsync(db, sessions, ct);
        var issueTitles = await LoadIssueTitlesAsync(db, projectId, sessions.Select(IssueNumber), ct);
        var eventSummaries = await LoadEventSummariesAsync(db, sessions.Select(r => r.Session.Id), ct);
        return sessions.Select(record =>
        {
            var s = record.Session;
            var events = eventSummaries.GetValueOrDefault(s.Id);
            var usage = AgentSessionJsonHelper.Usage(s);
            var issueNumber = IssueNumber(record);
            return new AgentSessionInfoDto(
            issueNumber,
            IssueTitle(issueTitles, issueNumber),
            Label(record, AgentSessionQueryMetadataKeys.Stage) ?? string.Empty,
            s.Id,
            AgentSessionJsonHelper.StatusName(s),
            s.Settings.Model,
            null,
            s.Status.CreatedAt.ToString("o"),
            null,
            AgentSessionJsonHelper.LastActivityAt(s).ToString("o"),
            ToEventSummaryDto(events),
            ToUsageDto(usage));
        }).ToList();
    }

    public async Task<IReadOnlyList<AgentSessionSummaryDto>> ListSummariesByIssueAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        var sessions = await _sessionQuery.ListByLabelsAsync(
            Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString())),
            ct: ct);
        return sessions.Select(ToSummaryDto).ToList();
    }

    public async Task<string?> ResolveIssueSessionIdAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var record = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        return record?.Session.Id;
    }

    public async Task<AgentSessionMetadataDto?> GetSessionMetadataAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (session is null) return null;

        var domainSession = session.Session;
        var transcript = await LoadTranscriptAsync(db, domainSession.Id, ct);
        var sessionByTurnId = transcript.Turns.ToDictionary(t => t.Id, t => t.SessionId);
        var transcriptEvents = transcript.Parts
            .Where(part => sessionByTurnId.ContainsKey(part.TurnId))
            .Select(part => ToProjection(sessionByTurnId[part.TurnId], part))
            .ToList();
        var partCount = transcriptEvents.Count;
        var eventSummary = TranscriptEventSummaryProjector.Summarize(
            transcriptEvents.Select(e => new TranscriptSummaryEvent(e.Sequence, e.Type, e.PayloadJson)));
        var toolCount = eventSummary.ToolCallCount ?? 0;
        var usage = AgentSessionJsonHelper.Usage(domainSession);

        return new AgentSessionMetadataDto(
            domainSession.Id,
            Label(session, AgentSessionQueryMetadataKeys.SessionName) ?? sessionName,
            domainSession.Status.AgentRuntimeSessionId ?? domainSession.Id,
            AgentSessionJsonHelper.StatusName(domainSession),
            domainSession.Settings.Model,
            Label(session, AgentSessionQueryMetadataKeys.Stage),
            Annotation(domainSession, AgentSessionQueryMetadataKeys.Title),
            domainSession.Status.CreatedAt.ToString("o"),
            null,
            ToEventSummaryDto(eventSummary),
            ToUsageDto(usage),
            new AgentSessionMetadataCounts(partCount, toolCount));
    }

    public async Task<AgentSessionTranscriptResponse?> GetSessionTranscriptAsync(string projectId, int issueNumber, string sessionName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await FindCurrentSessionAsync(db, projectId, issueNumber, sessionName, ct);
        if (session is null) return null;

        var transcript = await LoadTranscriptAsync(db, session.Session.Id, ct);
        return SessionTranscriptBuilder.Build(transcript);
    }

    private async Task<AgentSessionRecord?> FindCurrentSessionAsync(
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

        return await _sessionQuery.FirstByLabelsAsync(
            Labels(
                (AgentSessionQueryMetadataKeys.ProjectId, projectId),
                (AgentSessionQueryMetadataKeys.IssueNumber, issueNumber.ToString()),
                (AgentSessionQueryMetadataKeys.WorkflowRunId, workflowRunId),
                (AgentSessionQueryMetadataKeys.SessionName, sessionName)),
            ct: ct);
    }

    public async Task<ActivityDto> GetActivityAsync(string projectId, int? limit = null, IReadOnlyList<ActivityWaitingCardDto>? waiting = null, IReadOnlyList<string>? runnerIds = null, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sessions = (await _sessionQuery.ListByLabelsAsync(
                Labels((AgentSessionQueryMetadataKeys.ProjectId, projectId)),
                AgentSessionQueryOrder.CreatedDescending,
                take,
                ct: ct))
            .ToList();
        sessions = await ReconcileActiveSessionsAsync(db, sessions, ct);

        var sessionIds = sessions.Select(s => s.Session.Id).ToArray();
        var latestEvents = await LoadLatestEventsAsync(db, sessionIds, ct);
        var eventSummaries = await LoadEventSummariesAsync(db, sessionIds, ct);
        var issueTitles = await LoadIssueTitlesAsync(db, projectId, sessions.Select(IssueNumber), ct);
        var taskProgressMap = await BuildTaskProgressMapAsync(sessions, ct);

        var cards = sessions
            .Select(record => ToActivityCard(record, latestEvents.GetValueOrDefault(record.Session.Id), eventSummaries.GetValueOrDefault(record.Session.Id), IssueTitle(issueTitles, IssueNumber(record)), taskProgressMap.GetValueOrDefault(record.Session.Id)))
            .ToList();

        waiting ??= [];
        var summary = new ActivitySummaryDto(
            cards.Count(c => c.Status == "active"),
            waiting.Count,
            0,
            0,
            new ActivitySlotUsageDto(cards.Count(c => c.Status == "active"), (runnerIds?.Count ?? 0) + 1));

        return new ActivityDto(summary, cards, waiting.ToList());
    }

    private async Task<Dictionary<string, ActivityTaskProgressDto>> BuildTaskProgressMapAsync(List<AgentSessionRecord> sessions, CancellationToken ct)
    {
        var result = new Dictionary<string, ActivityTaskProgressDto>(StringComparer.Ordinal);
        var workflowRunIds = sessions
            .Select(s => Label(s, AgentSessionQueryMetadataKeys.WorkflowRunId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
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
            var workflowRunId = Label(session, AgentSessionQueryMetadataKeys.WorkflowRunId);
            if (workflowRunId is null || !statusByWorkflow.TryGetValue(workflowRunId, out var status) || status is null)
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
                result[session.Session.Id] = new ActivityTaskProgressDto(completed, total);
        }

        return result;
    }

    public async Task<AgentUsageTimeseriesDto> GetUsageTimeseriesAsync(string projectId, CancellationToken ct = default)
    {
        var rangeTo = DateTime.UtcNow.Date.AddDays(1);
        var rangeFrom = rangeTo.AddDays(-7);

        var sessions = await _sessionQuery.ListByLabelsAsync(
            Labels((AgentSessionQueryMetadataKeys.ProjectId, projectId)),
            AgentSessionQueryOrder.CreatedAscending,
            from: rangeFrom,
            to: rangeTo,
            ct: ct);

        var buckets = new UsageBucketData[7];
        for (var i = 0; i < 7; i++)
            buckets[i] = new UsageBucketData(rangeFrom.AddDays(i), rangeFrom.AddDays(i + 1));

        foreach (var record in sessions)
        {
            var usage = AgentSessionJsonHelper.Usage(record.Session);
            if (!HasUsage(usage)) continue;

            var createdAt = record.Session.Status.CreatedAt;
            var bucketIndex = (int)(createdAt.Date - rangeFrom.Date).Days;
            if (bucketIndex < 0 || bucketIndex >= 7) continue;

            var bucket = buckets[bucketIndex];
            bucket.InputTokens += usage.InputTokens ?? 0;
            bucket.OutputTokens += usage.OutputTokens ?? 0;
            bucket.TotalTokens += usage.TotalTokens ?? 0;
            bucket.CostAmount += usage.CostAmount ?? 0d;
            bucket.CostCurrency ??= usage.CostCurrency;
        }

        return new AgentUsageTimeseriesDto(
            rangeFrom,
            rangeTo,
            "day",
            buckets.Select(b => b.ToDto()).ToList());
    }

    private static bool HasUsage(AgentUsageSummary usage)
    {
        return usage.InputTokens.HasValue
            || usage.OutputTokens.HasValue
            || usage.TotalTokens.HasValue
            || usage.CostAmount.HasValue;
    }

    private sealed class UsageBucketData
    {
        private readonly DateTime _bucketStart;
        private readonly DateTime _bucketEnd;

        public long InputTokens;
        public long OutputTokens;
        public long TotalTokens;
        public double CostAmount;
        public string? CostCurrency;

        public UsageBucketData(DateTime bucketStart, DateTime bucketEnd)
        {
            _bucketStart = bucketStart;
            _bucketEnd = bucketEnd;
        }

        public UsageBucketDto ToDto() => new(
            _bucketStart,
            _bucketEnd,
            InputTokens,
            OutputTokens,
            TotalTokens,
            CostAmount,
            CostCurrency);
    }

    private static async Task<Dictionary<string, TranscriptEventProjection>> LoadLatestEventsAsync(
        MohistDbContext db, string[] sessionIds, CancellationToken ct)
    {
        if (sessionIds.Length == 0) return [];

        var turns = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(t => sessionIds.Contains(t.SessionId))
            .ToListAsync(ct);
        var turnIds = turns.Select(t => t.Id).ToArray();
        if (turnIds.Length == 0) return [];

        var sessionByTurnId = turns.ToDictionary(t => t.Id, t => t.SessionId);
        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(e => turnIds.Contains(e.TurnId))
            .OrderBy(e => e.LastSeenAt)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var result = new Dictionary<string, TranscriptEventProjection>(StringComparer.Ordinal);
        foreach (var part in parts)
            if (sessionByTurnId.TryGetValue(part.TurnId, out var sessionId))
                result[sessionId] = ToProjection(sessionId, part);

        return result;
    }

    private static async Task<Dictionary<string, AgentSessionTranscriptSummary>> LoadEventSummariesAsync(
        MohistDbContext db, IEnumerable<string> sessionIds, CancellationToken ct)
    {
        var ids = sessionIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return [];

        var turns = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(t => ids.Contains(t.SessionId))
            .ToListAsync(ct);
        var turnIds = turns.Select(t => t.Id).ToArray();
        if (turnIds.Length == 0) return [];

        var sessionByTurnId = turns.ToDictionary(t => t.Id, t => t.SessionId);
        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(e => turnIds.Contains(e.TurnId))
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);

        return parts
            .Where(part => sessionByTurnId.ContainsKey(part.TurnId))
            .Select(part => ToProjection(sessionByTurnId[part.TurnId], part))
            .OrderBy(e => e.Sequence)
            .GroupBy(e => e.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => TranscriptEventSummaryProjector.Summarize(
                g.Select(e => new TranscriptSummaryEvent(e.Sequence, e.Type, e.PayloadJson))), StringComparer.Ordinal);
    }

    private static ActivityCardDto ToActivityCard(AgentSessionRecord record, TranscriptEventProjection? latestEvent, AgentSessionTranscriptSummary? eventSummary, string issueTitle, ActivityTaskProgressDto? taskProgress)
    {
        var s = record.Session;
        var lastActivityAt = AgentSessionJsonHelper.LastActivityAt(s).ToString("o");
        var usage = AgentSessionJsonHelper.Usage(s);
        var issueNumber = IssueNumber(record);
        var projectId = Label(record, AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty;
        var sessionName = Label(record, AgentSessionQueryMetadataKeys.SessionName) ?? s.Id;
        var stage = Label(record, AgentSessionQueryMetadataKeys.Stage);
        var workId = Label(record, AgentSessionQueryMetadataKeys.WorkId);
        var workType = Label(record, AgentSessionQueryMetadataKeys.WorkType);
        return new ActivityCardDto(
            $"issue_{projectId}_{issueNumber}",
            issueNumber,
            issueTitle,
            stage ?? string.Empty,
            null,
            s.Id,
            AgentSessionJsonHelper.StatusName(s),
            s.Settings.Model,
            null,
            s.Status.CreatedAt.ToString("o"),
            null,
            lastActivityAt,
            new ActivityWorkItemDto(workType ?? "task", workId ?? sessionName, workId ?? sessionName, stage, null),
            taskProgress,
            latestEvent is null ? null : ToPreview(latestEvent),
            null,
            ToEventSummaryDto(eventSummary),
            ToUsageDto(usage));
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

    private static AgentSessionDto ToAgentSessionDto(AgentSessionRecord record)
    {
        var s = record.Session;
        var usage = AgentSessionJsonHelper.Usage(s);
        return new AgentSessionDto(
            s.Id,
            Label(record, AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty,
            IssueNumber(record),
            Label(record, AgentSessionQueryMetadataKeys.WorkflowRunId) ?? string.Empty,
            Label(record, AgentSessionQueryMetadataKeys.SessionName) ?? string.Empty,
            Label(record, AgentSessionQueryMetadataKeys.WorkId),
            Label(record, AgentSessionQueryMetadataKeys.WorkType),
            Label(record, AgentSessionQueryMetadataKeys.Stage),
            Annotation(s, AgentSessionQueryMetadataKeys.Title), s.Runtime.RunnerId, s.Status.AgentRuntimeSessionId,
            AgentSessionJsonHelper.StatusName(s), s.Settings.Model, s.Runtime.WorkDir, null, null,
            s.Status.CreatedAt.ToString("o"), s.Status.BoundAt?.ToString("o"), null,
            s.Status.LastDataAt?.ToString("o"), null, null,
            new AgentEventSummaryDto(null, null, null, null, null, null),
            ToUsageDto(usage));
    }

    private static WorkflowSessionDto ToWorkflowDto(AgentSessionRecord record)
    {
        var s = record.Session;
        var issueNumber = IssueNumber(record);
        return new(
        s.Id,
        Label(record, AgentSessionQueryMetadataKeys.WorkflowRunId) ?? string.Empty,
        Label(record, AgentSessionQueryMetadataKeys.SessionName) ?? string.Empty,
        s.Status.AgentRuntimeSessionId,
        Label(record, AgentSessionQueryMetadataKeys.ProjectId),
        issueNumber == 0 ? null : issueNumber,
        s.Runtime.RunnerId,
        AgentSessionJsonHelper.StatusName(s), s.Settings.Model, s.Runtime.WorkDir, null,
        s.Status.CreatedAt.ToString("o"), s.Status.BoundAt?.ToString("o"), s.Status.LastDataAt?.ToString("o"),
        null, null, null,
        new AgentEventSummaryDto(null, null, null, null, null, null),
        ToUsageDto(s));
    }

    private static AgentSessionSummaryDto ToSummaryDto(AgentSessionRecord record)
    {
        var s = record.Session;
        return new AgentSessionSummaryDto(
            s.Id,
            Label(record, AgentSessionQueryMetadataKeys.SessionName) ?? string.Empty,
            s.Status.AgentRuntimeSessionId ?? s.Id,
            Label(record, AgentSessionQueryMetadataKeys.WorkId),
            Annotation(s, AgentSessionQueryMetadataKeys.Title),
            AgentSessionJsonHelper.StatusName(s), s.Status.CreatedAt.ToString("o"), null,
            s.Settings.Model, null, Label(record, AgentSessionQueryMetadataKeys.Stage), Annotation(s, AgentSessionQueryMetadataKeys.Title),
            s.Status.LastDataAt?.ToString("o"), null, null, null,
            new AgentEventSummaryDto(null, null, null, null, null, null),
            ToUsageDto(s));
    }

    private static string? Label(AgentSessionRecord record, string key) =>
        record.Label(key) ?? record.Session.Metadata.Label(key);

    private static int IssueNumber(AgentSessionRecord record) =>
        int.TryParse(Label(record, AgentSessionQueryMetadataKeys.IssueNumber), out var issueNumber)
            ? issueNumber
            : 0;

    private static string? Annotation(AgentSession session, string key) => session.Metadata.Annotation(key);

    private static AgentUsageDto ToUsageDto(AgentSession s) =>
        ToUsageDto(AgentSessionJsonHelper.Usage(s));

    private static AgentUsageDto ToUsageDto(AgentUsageSummary u) =>
        new(u.InputTokens, u.OutputTokens, u.TotalTokens, u.CachedReadTokens, u.ThoughtTokens,
            u.CostAmount, u.CostCurrency, u.ContextWindowUsed, u.ContextWindowSize,
            AgentSessionJsonHelper.ContextUsagePercent(u.ContextWindowUsed, u.ContextWindowSize),
            ContextHealthClassifier.Classify(AgentSessionJsonHelper.ContextUsagePercent(u.ContextWindowUsed, u.ContextWindowSize)));

    private static AgentEventSummaryDto ToEventSummaryDto(AgentSessionTranscriptSummary? s) =>
        s is null
            ? new AgentEventSummaryDto(null, null, null, null, null, null)
            : new(
                s.ResolvedModel,
                s.FailureCategory,
                string.Equals(s.FailureCategory, ContextExhaustionClassifier.ContextExhaustionCategory, StringComparison.Ordinal) ? true : null,
                string.Equals(s.FailureCategory, ContextExhaustionClassifier.SuspectedContextExhaustionCategory, StringComparison.Ordinal) ? true : null,
                s.ToolCallCount,
                s.ToolErrorCount);

    private static IReadOnlyDictionary<string, string> Labels(params (string Key, string? Value)[] values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            result[key] = value;
        }
        return result;
    }

    private static bool MatchesStatus(AgentSession session, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return true;
        return status.Trim().ToLowerInvariant() switch
        {
            "active" => AgentSessionJsonHelper.StatusName(session) == "active",
            "inactive" => AgentSessionJsonHelper.StatusName(session) == "inactive",
            _ => true,
        };
    }

    private static async Task<AgentSessionTranscriptData> LoadTranscriptAsync(MohistDbContext db, string sessionId, CancellationToken ct)
    {
        var turns = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);
        var turnIds = turns.Select(t => t.Id).ToArray();
        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(e => turnIds.Contains(e.TurnId))
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);
        return new AgentSessionTranscriptData(turns, parts);
    }

    private static TranscriptEventProjection ToProjection(string sessionId, AgentSessionTranscriptPartRow part) => new()
    {
        Id = part.Id,
        SessionId = sessionId,
        Sequence = part.Sequence,
        Type = part.Type,
        PayloadJson = part.Type is "text" or "reasoning"
            ? JsonSerializer.Serialize(new { text = part.Text })
            : part.PayloadJson,
        CreatedAt = part.LastSeenAt,
    };

    private static async Task<List<AgentSessionRecord>> ReconcileActiveSessionsAsync(
        MohistDbContext db,
        List<AgentSessionRecord> sessions,
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
            var workflowRunId = Label(activeSession, AgentSessionQueryMetadataKeys.WorkflowRunId);
            if (workflowRunId is null || !runsByWorkflow.TryGetValue(workflowRunId, out var run) || run is null)
            {
                allowedSessionIds.Add(activeSession.Session.Id);
                continue;
            }

            if (IsSessionAssociatedWithRun(run, activeSession))
                allowedSessionIds.Add(activeSession.Session.Id);
        }

        return sessions
            .Where(s => !IsActiveSession(s) || allowedSessionIds.Contains(s.Session.Id))
            .ToList();
    }

    private static WorkflowRun? DeserializeWorkflowRun(string json)
    {
        try { return JsonSerializer.Deserialize<WorkflowRun>(json, Infrastructure.Data.Sessions.AgentSessionJson.JsonOptions); }
        catch { return null; }
    }

    private static async Task<Dictionary<string, WorkflowRun?>> LoadWorkflowRunsForReconciliationAsync(
        MohistDbContext db, List<AgentSessionRecord> sessions, CancellationToken ct)
    {
        var workflowIds = sessions
            .Select(s => Label(s, AgentSessionQueryMetadataKeys.WorkflowRunId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
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

    /// <summary>
    /// Determines whether <paramref name="session"/> is associated with <paramref name="run"/>
    /// by reference through a <see cref="TaskRun"/>, not by ownership.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentSession"/> is a peer aggregate, never owned by <see cref="WorkflowRun"/>.
    /// The association is established through a <see cref="TaskRun"/> session reference and
    /// relies on the single-runner claim invariant: if the run is claimed, the session MUST
    /// belong to the same runner (<see cref="WorkflowClaimInfo.RunnerId"/> == session.RunnerId)
    /// and the task identified by <see cref="AgentSessionQueryMetadataKeys.WorkId"/> (if running)
    /// MUST match the session's work item (the task whose reference links them).
    /// When the run has no claim yet (<see cref="WorkflowRun.ClaimedBy"/> is null), any active
    /// session known by workflow-run-id is provisionally accepted.
    /// </remarks>
    private static bool IsSessionAssociatedWithRun(WorkflowRun run, AgentSessionRecord session)
    {
        if (run.ClaimedBy is null) return true;

        if (!string.Equals(run.ClaimedBy, session.Row.RunnerId, StringComparison.Ordinal))
            return false;

        var runningTask = run.Stages
            .SelectMany(s => s.Tasks)
            .FirstOrDefault(t => t.Status == Workflow.Domain.Run.TaskRunStatus.Running);

        return runningTask is null || string.Equals(runningTask.Id, Label(session, AgentSessionQueryMetadataKeys.WorkId), StringComparison.Ordinal);
    }

    private static bool IsActiveSession(AgentSessionRecord session) =>
        session.Row.AgentSessionId is not null;
}

internal sealed record TranscriptEventProjection
{
    public long Id { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public long Sequence { get; init; }
    public string Type { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = "{}";
    public DateTime CreatedAt { get; init; }
}

internal sealed record AgentSessionTranscriptData(
    IReadOnlyList<AgentSessionTranscriptTurnRow> Turns,
    IReadOnlyList<AgentSessionTranscriptPartRow> Parts);
