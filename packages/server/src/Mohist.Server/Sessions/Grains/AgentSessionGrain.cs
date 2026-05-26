using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Events;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Sessions.Grains;

public sealed class AgentSessionGrain : Grain, IAgentSessionGrain
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<AgentSessionGrain> _log;
    private AgentSession? _session;
    private long _nextSequence;

    public AgentSessionGrain(IDbContextFactory<MohistDbContext> dbFactory, IEventBus eventBus, ILogger<AgentSessionGrain> log)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        _log = log;
    }

    private string SessionId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        _session = await db.AgentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == SessionId, ct);
        if (_session is null) return;
        _nextSequence = await db.AgentSessionEvents
            .Where(e => e.SessionId == SessionId)
            .Select(e => (long?)e.Sequence)
            .MaxAsync(ct) ?? 0;
    }

    public async Task<AgentSessionSnapshot?> EnsureCreatedAsync(EnsureAgentSessionCommand command)
    {
        var runnerId = command.RunnerId;
        var dispatch = command.Dispatch;
        if (dispatch.Uses != "mohist/acp-agent") return null;
        if (dispatch.Issue is null) return null;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == SessionId);
        if (existing is null)
        {
            existing = await db.AgentSessions.FirstOrDefaultAsync(s => s.WorkflowRunId == dispatch.WorkflowRunId && s.WorkId == dispatch.WorkId);
        }

        if (existing is null)
        {
            existing = new AgentSession
            {
                Id = SessionId,
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
            db.AgentSessions.Add(existing);
            await db.SaveChangesAsync();
        }

        _session = Clone(existing);
        _nextSequence = await db.AgentSessionEvents
            .Where(e => e.SessionId == _session.Id)
            .Select(e => (long?)e.Sequence)
            .MaxAsync() ?? 0;
        return ToDto(_session);
    }

    public async Task<AgentSessionSnapshot?> MarkStartedAsync(AgentSessionStartedCommand request)
    {
        var session = await LoadTrackedAsync();
        if (session is null) return null;
        if (IsTerminal(session.Status)) return ToDto(session);

        var now = DateTime.UtcNow;
        session.Status = "running";
        session.ExternalSessionId = request.ExternalSessionId ?? session.ExternalSessionId;
        session.Model = request.Model ?? session.Model;
        session.WorkDir = request.WorkDir ?? session.WorkDir;
        session.ChangeDir = request.ChangeDir ?? session.ChangeDir;
        session.ProcessPid = request.ProcessPid ?? session.ProcessPid;
        session.StartedAt ??= now;
        session.LastDataAt = now;
        session.LastHeartbeatAt = now;
        await SaveSessionAsync(session);

        EmitStarted(session);
        return ToDto(session);
    }

    public async Task<IReadOnlyList<AgentSessionEventSnapshot>> AppendEventsAsync(IReadOnlyList<AgentSessionEventInput> events)
    {
        if (events.Count == 0) return [];
        var session = await LoadTrackedAsync();
        if (session is null) return [];

        var now = DateTime.UtcNow;
        var entries = events.Select(e => new AgentSessionEvent
        {
            SessionId = session.Id,
            ProjectId = session.ProjectId,
            IssueNumber = session.IssueNumber,
            WorkflowRunId = session.WorkflowRunId,
            WorkId = session.WorkId,
            Sequence = ++_nextSequence,
            Type = e.Type,
            PayloadJson = string.IsNullOrWhiteSpace(e.PayloadJson) ? "{}" : e.PayloadJson,
            CreatedAt = now,
        }).ToList();

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.AgentSessionEvents.AddRange(entries);
        var tracked = await db.AgentSessions.FirstAsync(s => s.Id == session.Id);
        tracked.LastDataAt = now;
        tracked.LastHeartbeatAt = now;
        await db.SaveChangesAsync();
        _session = Clone(tracked);

        foreach (var entry in entries)
            EmitSessionEvent(tracked, entry);
        return entries.Select(ToDto).ToList();
    }

    public async Task<AgentSessionSnapshot?> MarkStatusAsync(AgentSessionStatusCommand request)
    {
        var session = await LoadTrackedAsync();
        if (session is null) return null;
        if (IsTerminal(session.Status)) return ToDto(session);

        session.Status = NormalizeNonTerminalStatus(request.Status);
        session.LastDataAt = request.LastDataAt ?? session.LastDataAt;
        session.LastHeartbeatAt = DateTime.UtcNow;
        session.FailureReason = request.FailureReason ?? session.FailureReason;
        await SaveSessionAsync(session);
        EmitStatusChanged(session);
        return ToDto(session);
    }

    public async Task<AgentSessionSnapshot?> MarkCompletedAsync(AgentSessionCompletedCommand request)
    {
        var session = await LoadTrackedAsync();
        if (session is null) return null;
        if (IsTerminal(session.Status)) return ToDto(session);

        var terminal = request.Status is "failed" or "cancelled" ? request.Status : "completed";
        session.Status = terminal;
        session.CompletedAt = DateTime.UtcNow;
        session.LastHeartbeatAt = session.CompletedAt;
        session.FailureReason = request.FailureReason ?? session.FailureReason;
        session.ExitCode = request.ExitCode ?? session.ExitCode;
        await SaveSessionAsync(session);
        EmitTerminal(session, terminal);
        return ToDto(session);
    }

    public async Task<AgentSessionSnapshot?> FailIfRunningAsync(string reason)
    {
        var session = await LoadTrackedAsync();
        if (session is null) return null;
        if (IsTerminal(session.Status)) return ToDto(session);

        session.Status = "failed";
        session.CompletedAt = DateTime.UtcNow;
        session.LastHeartbeatAt = session.CompletedAt;
        session.FailureReason = reason;
        await SaveSessionAsync(session);
        EmitTerminal(session, "failed");
        return ToDto(session);
    }

    public async Task<AgentSessionSnapshot?> GetAsync()
    {
        var session = await LoadDetachedAsync();
        return session is null ? null : ToDto(session);
    }

    private async Task<AgentSession?> LoadDetachedAsync()
    {
        if (_session is not null) return _session;
        await using var db = await _dbFactory.CreateDbContextAsync();
        _session = await db.AgentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == SessionId);
        return _session;
    }

    private async Task<AgentSession?> LoadTrackedAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == SessionId);
    }

    private async Task SaveSessionAsync(AgentSession session)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.AgentSessions.Update(session);
        await db.SaveChangesAsync();
        _session = Clone(session);
    }

    private void EmitStarted(AgentSession session) => _eventBus.Emit("coder_session_started", new
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
        else if (entry.Type is "agent_thought_chunk")
        {
            _eventBus.Emit("coder_thought_chunk", new
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

    private void EmitTerminal(AgentSession session, string terminal) => _eventBus.Emit(terminal switch
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

    private static string NormalizeNonTerminalStatus(string status) => status is "probing" or "running" ? status : "running";
    private static bool IsTerminal(string status) => status is "completed" or "failed" or "cancelled";

    private static AgentSession Clone(AgentSession s) => new()
    {
        Id = s.Id,
        ProjectId = s.ProjectId,
        IssueNumber = s.IssueNumber,
        WorkflowRunId = s.WorkflowRunId,
        WorkId = s.WorkId,
        WorkType = s.WorkType,
        Stage = s.Stage,
        Title = s.Title,
        RunnerId = s.RunnerId,
        ExternalSessionId = s.ExternalSessionId,
        Status = s.Status,
        Model = s.Model,
        WorkDir = s.WorkDir,
        ChangeDir = s.ChangeDir,
        ProcessPid = s.ProcessPid,
        CreatedAt = s.CreatedAt,
        StartedAt = s.StartedAt,
        CompletedAt = s.CompletedAt,
        LastDataAt = s.LastDataAt,
        LastHeartbeatAt = s.LastHeartbeatAt,
        FailureReason = s.FailureReason,
        ExitCode = s.ExitCode,
    };

    private static AgentSessionSnapshot ToDto(AgentSession s) => new(
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

    private static AgentSessionEventSnapshot ToDto(AgentSessionEvent e) => new(
        e.Id.ToString(), e.SessionId, e.ProjectId, e.IssueNumber, e.WorkflowRunId, e.WorkId, e.Sequence, e.Type, e.PayloadJson, e.CreatedAt.ToString("o"));

    private static string ExtractText(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(json);
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("text", out var text))
                return text.GetString() ?? string.Empty;
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object && content.TryGetProperty("text", out var contentText))
                return contentText.GetString() ?? string.Empty;
            if (payload.ValueKind == JsonValueKind.String)
                return payload.GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _ = ex;
        }
        return string.Empty;
    }

    private static long DurationMs(AgentSession session)
    {
        var start = session.StartedAt ?? session.CreatedAt;
        var end = session.CompletedAt ?? DateTime.UtcNow;
        return Math.Max(0, (long)(end - start).TotalMilliseconds);
    }
}
