using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Sessions;

public class AgentSessionService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventBus _eventBus;

    public AgentSessionService(IDbContextFactory<MohistDbContext> dbFactory, IEventBus eventBus)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
    }

    public async Task<AgentSessionDto?> CreateForDispatchAsync(string runnerId, WorkDispatch dispatch, CancellationToken ct = default)
    {
        if (dispatch.Uses != "mohist/acp-agent") return null;
        if (dispatch.Issue is null) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AgentSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkflowRunId == dispatch.WorkflowRunId && s.WorkId == dispatch.WorkId, ct);
        if (existing is not null) return ToDto(existing);

        var session = new AgentSession
        {
            ProjectId = dispatch.Issue.ProjectId,
            IssueNumber = dispatch.Issue.IssueNumber,
            WorkflowRunId = dispatch.WorkflowRunId,
            WorkId = dispatch.WorkId,
            WorkType = dispatch.WorkType,
            Stage = dispatch.Stage,
            Title = dispatch.Title,
            RunnerId = runnerId,
            Status = "created",
            CreatedAt = DateTime.UtcNow,
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return ToDto(session);
    }

    public async Task<AgentSessionDto?> MarkStartedAsync(string sessionId, SessionStartedRequest req, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return null;

        var now = DateTime.UtcNow;
        session.Status = "running";
        session.ExternalSessionId = req.ExternalSessionId ?? session.ExternalSessionId;
        session.Model = req.Model ?? session.Model;
        session.WorkDir = req.WorkDir ?? session.WorkDir;
        session.ChangeDir = req.ChangeDir ?? session.ChangeDir;
        session.ProcessPid = req.ProcessPid ?? session.ProcessPid;
        session.StartedAt ??= now;
        session.LastDataAt = now;
        session.LastHeartbeatAt = now;
        await db.SaveChangesAsync(ct);

        var dto = ToDto(session);
        _eventBus.Emit("coder_session_started", new
        {
            issueId = session.IssueNumber.ToString(),
            session.ProjectId,
            coderSessionId = session.Id,
            acpSessionId = session.ExternalSessionId ?? session.Id,
            executionId = session.WorkId,
            session.Model,
            stage = session.Stage,
            taskDescription = session.Title,
            title = session.Title,
        });
        return dto;
    }

    public async Task<IReadOnlyList<AgentSessionEventDto>> AppendEventsAsync(string sessionId, IReadOnlyList<SessionEventRequest> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return [];

        var nextSequence = await db.AgentSessionEvents
            .Where(e => e.SessionId == sessionId)
            .Select(e => (long?)e.Sequence)
            .MaxAsync(ct) ?? 0;

        var now = DateTime.UtcNow;
        var entries = events.Select(e => new AgentSessionEvent
        {
            SessionId = session.Id,
            ProjectId = session.ProjectId,
            IssueNumber = session.IssueNumber,
            WorkflowRunId = session.WorkflowRunId,
            WorkId = session.WorkId,
            Sequence = ++nextSequence,
            Type = e.Type,
            PayloadJson = e.Payload.ValueKind == JsonValueKind.Undefined ? "{}" : e.Payload.GetRawText(),
            CreatedAt = now,
        }).ToList();

        db.AgentSessionEvents.AddRange(entries);
        session.LastDataAt = now;
        session.LastHeartbeatAt = now;
        await db.SaveChangesAsync(ct);

        foreach (var entry in entries)
            EmitSessionEvent(session, entry);

        return entries.Select(ToDto).ToList();
    }

    public async Task<AgentSessionDto?> MarkStatusAsync(string sessionId, SessionStatusRequest req, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return null;

        session.Status = req.Status;
        session.LastDataAt = req.LastDataAt ?? session.LastDataAt;
        session.LastHeartbeatAt = DateTime.UtcNow;
        session.FailureReason = req.FailureReason ?? session.FailureReason;
        await db.SaveChangesAsync(ct);
        EmitStatusChanged(session);
        return ToDto(session);
    }

    public async Task<AgentSessionDto?> MarkCompletedAsync(string sessionId, SessionCompletedRequest req, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return null;

        var terminal = req.Status is "failed" or "cancelled" ? req.Status : "completed";
        session.Status = terminal;
        session.CompletedAt = DateTime.UtcNow;
        session.LastHeartbeatAt = session.CompletedAt;
        session.FailureReason = req.FailureReason ?? session.FailureReason;
        session.ExitCode = req.ExitCode ?? session.ExitCode;
        await db.SaveChangesAsync(ct);

        _eventBus.Emit(terminal switch
        {
            "failed" => "coder_session_failed",
            "cancelled" => "coder_session_cancelled",
            _ => "coder_session_completed"
        }, new
        {
            issueId = session.IssueNumber.ToString(),
            session.ProjectId,
            coderSessionId = session.Id,
            status = terminal,
            reason = session.FailureReason,
            duration = DurationMs(session),
        });

        return ToDto(session);
    }

    public async Task<IReadOnlyList<AgentSessionInfoDto>> ListCurrentAsync(string projectId, string? status = null, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.AgentSessions.AsNoTracking().Where(s => s.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(s => s.Status == status);

        var rows = await query.OrderByDescending(s => s.CreatedAt).Take(limit).ToListAsync(ct);
        return rows.Select(s => new AgentSessionInfoDto(
            s.IssueNumber,
            $"Issue #{s.IssueNumber}",
            s.Stage ?? "",
            s.Id,
            s.Status,
            s.Model,
            s.Title,
            s.CreatedAt.ToString("o"),
            s.CompletedAt?.ToString("o"),
            (s.LastDataAt ?? s.StartedAt ?? s.CreatedAt).ToString("o"))).ToList();
    }

    public async Task<IReadOnlyList<CoderSessionSummaryDto>> ListByIssueAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AgentSessions.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.IssueNumber == issueNumber)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToSummaryDto).ToList();
    }

    public async Task<CoderSessionDetailDto?> GetDetailAsync(string projectId, int issueNumber, string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var session = await db.AgentSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.ProjectId == projectId && s.IssueNumber == issueNumber, ct);
        if (session is null) return null;

        var events = await db.AgentSessionEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);

        var createdAt = session.StartedAt ?? session.CreatedAt;
        var text = string.Concat(events
            .Where(e => e.Type is "agent_message_chunk" or "agent_output_chunk")
            .Select(e => ExtractText(e.PayloadJson)));
        object[] assistant = string.IsNullOrEmpty(text)
            ? []
            : [new { id = $"{session.Id}-text", type = "text", text, startedAt = createdAt.ToString("o"), completedAt = session.CompletedAt?.ToString("o") }];

        var turns = new[]
        {
            new
            {
                id = $"{session.Id}-turn-1",
                startedAt = createdAt.ToString("o"),
                completedAt = session.CompletedAt?.ToString("o"),
                user = new { role = "mohist", text = session.Title ?? session.WorkId, kind = "task", sentAt = createdAt.ToString("o") },
                assistant,
            }
        };

        var metadata = new
        {
            sessionId = session.Id,
            coderSessionId = session.Id,
            issueId = issueNumber.ToString(),
            acpSessionId = session.ExternalSessionId ?? session.Id,
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

        return new CoderSessionDetailDto(
            session.Id,
            session.ExternalSessionId ?? session.Id,
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
            events.Select(e => new WorkflowLogItemDto(e.Id.ToString(), e.Type, ParsePayload(e.PayloadJson), e.CreatedAt.ToString("o"))).ToList());
    }

    private void EmitSessionEvent(AgentSession session, AgentSessionEvent entry)
    {
        var text = ExtractText(entry.PayloadJson);
        if (entry.Type is "agent_message_chunk" or "agent_output_chunk")
        {
            _eventBus.Emit("coder_text_chunk", new
            {
                issueId = session.IssueNumber.ToString(),
                session.ProjectId,
                executionId = session.WorkId,
                acpSessionId = session.ExternalSessionId ?? session.Id,
                coderSessionId = session.Id,
                text,
            });
        }
    }

    private void EmitStatusChanged(AgentSession session) => _eventBus.Emit("coder_session_status_changed", new
    {
        issueId = session.IssueNumber.ToString(),
        session.ProjectId,
        coderSessionId = session.Id,
        acpSessionId = session.ExternalSessionId ?? session.Id,
        status = session.Status,
        lastDataAt = session.LastDataAt?.ToString("o"),
        failureReason = session.FailureReason,
    });

    private static CoderSessionSummaryDto ToSummaryDto(AgentSession s) => new(
        s.Id,
        s.ExternalSessionId ?? s.Id,
        s.WorkId,
        s.Title,
        s.Status,
        s.CreatedAt.ToString("o"),
        s.CompletedAt?.ToString("o"),
        s.Model,
        null,
        s.Stage,
        s.Title,
        s.LastDataAt?.ToString("o"),
        null,
        null,
        s.FailureReason);

    private static AgentSessionDto ToDto(AgentSession s) => new(
        s.Id,
        s.ProjectId,
        s.IssueNumber,
        s.WorkflowRunId,
        s.WorkId,
        s.WorkType,
        s.Stage,
        s.Title,
        s.RunnerId,
        s.ExternalSessionId,
        s.Status,
        s.Model,
        s.WorkDir,
        s.ChangeDir,
        s.ProcessPid,
        s.CreatedAt.ToString("o"),
        s.StartedAt?.ToString("o"),
        s.CompletedAt?.ToString("o"),
        s.LastDataAt?.ToString("o"),
        s.FailureReason,
        s.ExitCode);

    private static AgentSessionEventDto ToDto(AgentSessionEvent e) => new(
        e.Id.ToString(), e.SessionId, e.ProjectId, e.IssueNumber, e.WorkflowRunId, e.WorkId, e.Sequence, e.Type, ParsePayload(e.PayloadJson), e.CreatedAt.ToString("o"));

    private static object? ParsePayload(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static string ExtractText(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(json);
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("text", out var text))
                return text.GetString() ?? string.Empty;
            if (payload.ValueKind == JsonValueKind.String)
                return payload.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static long DurationMs(AgentSession session)
    {
        var start = session.StartedAt ?? session.CreatedAt;
        var end = session.CompletedAt ?? DateTime.UtcNow;
        return Math.Max(0, (long)(end - start).TotalMilliseconds);
    }

}

public sealed record AgentSessionDto(
    string Id,
    string ProjectId,
    int IssueNumber,
    string WorkflowRunId,
    string WorkId,
    string WorkType,
    string? Stage,
    string? Title,
    string RunnerId,
    string? ExternalSessionId,
    string Status,
    string? Model,
    string? WorkDir,
    string? ChangeDir,
    int? ProcessPid,
    string CreatedAt,
    string? StartedAt,
    string? CompletedAt,
    string? LastDataAt,
    string? FailureReason,
    int? ExitCode);

public sealed record AgentSessionEventDto(string Id, string SessionId, string ProjectId, int IssueNumber, string WorkflowRunId, string WorkId, long Sequence, string Type, object? Payload, string CreatedAt);

public sealed record CoderSessionSummaryDto(string Id, string AcpSessionId, string? ExecutionId, string? TaskDescription, string Status, string CreatedAt, string? CompletedAt, string? Model, string? CoderType, string? Stage, string? Title, string? LastDataAt, string? ProbeSentAt, string? ProbeDeadlineAt, string? FailureReason);
public sealed record CoderSessionDetailDto(string Id, string AcpSessionId, string? ExecutionId, string? TaskDescription, string Status, string CreatedAt, string? CompletedAt, string? Model, string? CoderType, string? Stage, string? Title, object Metadata, object Turns, bool Incomplete, IReadOnlyList<WorkflowLogItemDto> WorkflowLogs);
public sealed record WorkflowLogItemDto(string Id, string EventType, object? Data, string CreatedAt);
public sealed record AgentSessionInfoDto(int IssueNumber, string IssueTitle, string IssueStage, string SessionId, string Status, string? Model, string? TaskDescription, string CreatedAt, string? CompletedAt, string? LastActivityAt);

public sealed record SessionStartedRequest(string? ExternalSessionId = null, string? Model = null, string? WorkDir = null, string? ChangeDir = null, int? ProcessPid = null);
public sealed record SessionEventRequest(string Type, JsonElement Payload);
public sealed record SessionStatusRequest(string Status, DateTime? LastDataAt = null, string? FailureReason = null);
public sealed record SessionCompletedRequest(string Status, string? FailureReason = null, int? ExitCode = null);
