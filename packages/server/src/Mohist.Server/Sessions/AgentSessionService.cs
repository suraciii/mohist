using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Sessions;

public class AgentSessionService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventBus _eventBus;
    private readonly IGrainFactory _grains;

    public AgentSessionService(IDbContextFactory<MohistDbContext> dbFactory, IEventBus eventBus, IGrainFactory grains)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        _grains = grains;
    }

    public async Task<AgentSessionDto?> CreateForDispatchAsync(string runnerId, WorkDispatch dispatch, CancellationToken ct = default)
    {
        var grain = _grains.GetGrain<IAgentSessionGrain>(AgentSessionGrainKeys.ForWork(dispatch.WorkflowRunId, dispatch.WorkId));
        var session = await grain.EnsureCreatedAsync(new EnsureAgentSessionCommand(runnerId, dispatch));
        return session is null ? null : ToDto(session);
    }

    public async Task<AgentSessionDto?> MarkStartedAsync(string sessionId, SessionStartedRequest req, CancellationToken ct = default)
    {
        var grain = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        var session = await grain.MarkStartedAsync(new AgentSessionStartedCommand(req.ExternalSessionId, req.Model, req.WorkDir, req.ChangeDir, req.ProcessPid));
        return session is null ? null : ToDto(session);
    }

    public async Task<IReadOnlyList<AgentSessionEventDto>> AppendEventsAsync(string sessionId, IReadOnlyList<SessionEventRequest> events, CancellationToken ct = default)
    {
        var grain = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        var inputs = events.Select(e => new AgentSessionEventInput(e.Type, e.Payload.ValueKind == JsonValueKind.Undefined ? "{}" : e.Payload.GetRawText())).ToList();
        var saved = await grain.AppendEventsAsync(inputs);
        return saved.Select(ToDto).ToList();
    }

    public async Task<AgentSessionDto?> MarkStatusAsync(string sessionId, SessionStatusRequest req, CancellationToken ct = default)
    {
        var grain = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        var session = await grain.MarkStatusAsync(new AgentSessionStatusCommand(req.Status, req.LastDataAt, req.FailureReason));
        return session is null ? null : ToDto(session);
    }

    public async Task<AgentSessionDto?> MarkCompletedAsync(string sessionId, SessionCompletedRequest req, CancellationToken ct = default)
    {
        var grain = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        var session = await grain.MarkCompletedAsync(new AgentSessionCompletedCommand(req.Status, req.FailureReason, req.ExitCode));
        return session is null ? null : ToDto(session);
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

    private static AgentSessionDto ToDto(AgentSessionSnapshot s) => new(
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
        s.CreatedAt,
        s.StartedAt,
        s.CompletedAt,
        s.LastDataAt,
        s.FailureReason,
        s.ExitCode);

    private static AgentSessionEventDto ToDto(AgentSessionEvent e) => new(
        e.Id.ToString(), e.SessionId, e.ProjectId, e.IssueNumber, e.WorkflowRunId, e.WorkId, e.Sequence, e.Type, ParsePayload(e.PayloadJson), e.CreatedAt.ToString("o"));

    private static AgentSessionEventDto ToDto(AgentSessionEventSnapshot e) => new(
        e.Id, e.SessionId, e.ProjectId, e.IssueNumber, e.WorkflowRunId, e.WorkId, e.Sequence, e.Type, ParsePayload(e.PayloadJson), e.CreatedAt);

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
