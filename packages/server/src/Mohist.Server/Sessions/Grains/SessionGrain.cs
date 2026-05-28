using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Storage;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Sessions.Grains;

public sealed class SessionGrain : Grain, ISessionGrain
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<SessionGrain> _log;
    private SessionRecord? _session;
    private long _nextSequence;

    public SessionGrain(IDbContextFactory<MohistDbContext> dbFactory, IEventBus eventBus, ILogger<SessionGrain> log)
    {
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        _log = log;
    }

    private string SessionId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        _session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == SessionId, ct);
        if (_session is null) return;
        _nextSequence = await db.SessionEvents
            .Where(e => e.SessionId == SessionId)
            .Select(e => (long?)e.Sequence)
            .MaxAsync(ct) ?? 0;
    }

    public async Task<SessionSnapshot> EnsureAsync(EnsureSessionCommand command)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == SessionId)
            ?? await db.Sessions.FirstOrDefaultAsync(s => s.WorkflowRunId == command.WorkflowRunId && s.SessionName == command.SessionName);

        if (session is null)
        {
            session = new SessionRecord
            {
                Id = SessionId,
                ProjectId = command.ProjectId,
                IssueNumber = command.IssueNumber ?? 0,
                WorkflowRunId = command.WorkflowRunId,
                SessionName = command.SessionName,
                WorkId = command.WorkId,
                WorkType = command.WorkType,
                Stage = command.Stage,
                Title = command.Title,
                RunnerId = command.RunnerId,
                Status = "created",
                CreatedAt = DateTime.UtcNow,
            };
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
        }
        else
        {
            session.RunnerId ??= command.RunnerId;
            session.WorkId ??= command.WorkId;
            session.WorkType ??= command.WorkType;
            session.Stage ??= command.Stage;
            session.Title ??= command.Title;
            if (session.IssueNumber == 0 && command.IssueNumber is > 0)
                session.IssueNumber = command.IssueNumber.Value;
            await db.SaveChangesAsync();
        }

        _session = Clone(session);
        _nextSequence = await db.SessionEvents
            .Where(e => e.SessionId == _session.Id)
            .Select(e => (long?)e.Sequence)
            .MaxAsync() ?? 0;
        return ToSnapshot(_session);
    }

    public async Task<SessionSnapshot> AttachAgentAsync(AttachAgentCommand command)
    {
        var session = await LoadTrackedOrCreateAsync();
        if (IsTerminal(session.Status)) return ToSnapshot(session);

        var now = DateTime.UtcNow;
        session.AgentSessionId = command.AgentSessionId;
        session.Model = command.Model ?? session.Model;
        session.WorkDir = command.WorkDir ?? session.WorkDir;
        session.ChangeDir = command.ChangeDir ?? session.ChangeDir;
        session.ProcessPid = command.ProcessPid ?? session.ProcessPid;
        session.StartedAt ??= now;
        session.LastDataAt = now;
        session.LastHeartbeatAt = now;

        var domain = session.ToDomain();
        domain.Start(command.Model, now);
        session.Apply(domain);

        await SaveAsync(session);
        EmitStarted(session);
        return ToSnapshot(session);
    }

    public async Task<IReadOnlyList<SessionEventSnapshot>> AppendEventsAsync(AppendSessionEventsCommand command)
    {
        if (command.Events.Count == 0) return [];

        var session = await LoadTrackedOrCreateAsync();
        if (IsTerminal(session.Status)) return [];

        var now = DateTime.UtcNow;
        var domain = session.ToDomain();
        domain.RecordActivity(now);

        var records = new List<SessionEventRecord>();
        foreach (var e in command.Events)
        {
            if (e.Type == "agent_liveness_status")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                var status = GetStringProp(payload, "status") ?? "running";
                domain.MarkActive(status, now, GetStringProp(payload, "failureReason"));
            }
            else if (e.Type == "agent_session_terminal")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                var status = GetStringProp(payload, "status") ?? "completed";
                var failureReason = GetStringProp(payload, "failureReason");
                var exitCode = GetIntProp(payload, "exitCode");
                if (status == "failed")
                    domain.Fail(now, failureReason, exitCode);
                else if (status == "cancelled")
                    domain.Cancel(now, failureReason, exitCode);
                else
                    domain.Complete(now, exitCode);
            }

            records.Add(new SessionEventRecord
            {
                SessionId = session.Id,
                ProjectId = session.ProjectId,
                IssueNumber = session.IssueNumber,
                WorkflowRunId = session.WorkflowRunId,
                SessionName = session.SessionName,
                AgentSessionId = session.AgentSessionId,
                WorkId = command.WorkId ?? session.WorkId,
                WorkType = command.WorkType ?? session.WorkType,
                Stage = command.Stage ?? session.Stage,
                Sequence = ++_nextSequence,
                Type = e.Type,
                PayloadJson = string.IsNullOrWhiteSpace(e.PayloadJson) ? "{}" : e.PayloadJson,
                CreatedAt = now,
            });
        }

        session.Apply(domain);
        if (session.Status is "created") session.Status = "running";

        var isTerminal = IsTerminal(session.Status);
        var statusChanged = records.Any(r => r.Type == "agent_liveness_status");

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.SessionEvents.AddRange(records);
        db.Sessions.Update(session);
        await db.SaveChangesAsync();
        _session = Clone(session);

        foreach (var entry in records)
            EmitTranscriptEntry(session, entry);

        if (statusChanged)
            EmitStatusChanged(session);
        if (isTerminal)
            EmitTerminal(session, session.Status);

        return records.Select(ToSnapshot).ToList();
    }

    public async Task<SessionSnapshot?> FailIfRunningAsync(string reason)
    {
        var session = await LoadTrackedOrCreateAsync();
        if (IsTerminal(session.Status)) return ToSnapshot(session);

        var domain = session.ToDomain();
        domain.Fail(DateTime.UtcNow, reason);
        session.Apply(domain);
        await SaveAsync(session);
        EmitTerminal(session, "failed");
        return ToSnapshot(session);
    }

    public async Task<SessionSnapshot?> GetAsync()
    {
        if (_session is not null) return ToSnapshot(_session);
        await using var db = await _dbFactory.CreateDbContextAsync();
        _session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == SessionId);
        return _session is null ? null : ToSnapshot(_session);
    }

    private async Task<SessionRecord> LoadTrackedOrCreateAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == SessionId);
        if (session is not null) return session;

        var parts = SessionId.Split('/');
        var projectId = parts.Length > 0 ? parts[0] : string.Empty;
        var workflowRunId = parts.Length > 1 ? parts[1] : string.Empty;
        var sessionName = parts.Length > 2 ? parts[2] : string.Empty;

        session = new SessionRecord
        {
            Id = SessionId,
            ProjectId = projectId,
            WorkflowRunId = workflowRunId,
            SessionName = sessionName,
            Status = "created",
            CreatedAt = DateTime.UtcNow,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        _session = Clone(session);
        return session;
    }

    private async Task SaveAsync(SessionRecord session)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Sessions.Update(session);
        await db.SaveChangesAsync();
        _session = Clone(session);
    }

    private void EmitStarted(SessionRecord session) => _eventBus.Emit("coder_session_started", new
    {
        issueId = session.IssueNumber.ToString(),
        session.ProjectId,
        coderSessionId = session.Id,
        acpSessionId = session.AgentSessionId ?? session.Id,
        executionId = session.WorkId,
        session.Model,
        stage = session.Stage,
        taskDescription = session.Title,
        title = session.Title,
    });

    private void EmitTranscriptEntry(SessionRecord session, SessionEventRecord entry)
    {
        var text = ExtractText(entry.PayloadJson);
        if (entry.Type is "agent_message_chunk" or "agent_output_chunk")
        {
            _eventBus.Emit("coder_text_chunk", new
            {
                issueId = session.IssueNumber.ToString(),
                session.ProjectId,
                executionId = session.WorkId,
                acpSessionId = session.AgentSessionId ?? session.Id,
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
                acpSessionId = session.AgentSessionId ?? session.Id,
                coderSessionId = session.Id,
                text,
            });
        }
    }

    private void EmitStatusChanged(SessionRecord session) => _eventBus.Emit("coder_session_status_changed", new
    {
        issueId = session.IssueNumber.ToString(),
        session.ProjectId,
        coderSessionId = session.Id,
        acpSessionId = session.AgentSessionId ?? session.Id,
        status = session.Status,
        lastDataAt = session.LastDataAt?.ToString("o"),
        failureReason = session.FailureReason,
    });

    private void EmitTerminal(SessionRecord session, string terminal) => _eventBus.Emit(terminal switch
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

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "cancelled";

    private static SessionRecord Clone(SessionRecord s) => new()
    {
        Id = s.Id,
        ProjectId = s.ProjectId,
        IssueNumber = s.IssueNumber,
        WorkflowRunId = s.WorkflowRunId,
        SessionName = s.SessionName,
        WorkId = s.WorkId,
        WorkType = s.WorkType,
        Stage = s.Stage,
        Title = s.Title,
        RunnerId = s.RunnerId,
        AgentSessionId = s.AgentSessionId,
        Status = s.Status,
        Model = s.Model,
        WorkDir = s.WorkDir,
        ChangeDir = s.ChangeDir,
        ProcessPid = s.ProcessPid,
        CreatedAt = s.CreatedAt,
        StartedAt = s.StartedAt,
        LastDataAt = s.LastDataAt,
        LastHeartbeatAt = s.LastHeartbeatAt,
        CompletedAt = s.CompletedAt,
        FailureReason = s.FailureReason,
        ExitCode = s.ExitCode,
    };

    private static SessionSnapshot ToSnapshot(SessionRecord s) => new(
        s.Id,
        s.ProjectId,
        s.IssueNumber == 0 ? null : s.IssueNumber,
        s.WorkflowRunId,
        s.SessionName,
        s.WorkId,
        s.WorkType,
        s.Stage,
        s.Title,
        s.RunnerId,
        s.AgentSessionId,
        s.Status,
        s.Model,
        s.WorkDir,
        s.ChangeDir,
        s.ProcessPid,
        s.CreatedAt.ToString("o"),
        s.StartedAt?.ToString("o"),
        s.LastDataAt?.ToString("o"),
        s.CompletedAt?.ToString("o"),
        s.FailureReason,
        s.ExitCode);

    private static SessionEventSnapshot ToSnapshot(SessionEventRecord e) => new(
        e.Id.ToString(),
        e.SessionId,
        e.ProjectId,
        e.IssueNumber,
        e.WorkflowRunId,
        e.SessionName,
        e.AgentSessionId,
        e.WorkId,
        e.WorkType,
        e.Stage,
        e.Sequence,
        e.Type,
        e.PayloadJson,
        e.CreatedAt.ToString("o"));

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

    private static long DurationMs(SessionRecord session)
    {
        var start = session.StartedAt ?? session.CreatedAt;
        var end = session.CompletedAt ?? DateTime.UtcNow;
        return Math.Max(0, (long)(end - start).TotalMilliseconds);
    }

    private static string? GetStringProp(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static int? GetIntProp(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt32()
            : null;
    }
}