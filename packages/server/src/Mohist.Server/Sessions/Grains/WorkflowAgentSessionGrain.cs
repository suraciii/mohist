using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Persistence.Sessions;
using Mohist.Server.Sessions.Storage;
using Mohist.Server.Infrastructure.Persistence;
using Mohist.Server.Infrastructure.Persistence.Db;

namespace Mohist.Server.Sessions.Grains;

public sealed class WorkflowAgentSessionGrain : Grain, IWorkflowAgentSessionGrain
{
    private readonly IStateStore<WorkflowAgentSession> _stateStore;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<WorkflowAgentSessionGrain> _log;
    private WorkflowAgentSession? _session;

    public WorkflowAgentSessionGrain(IStateStore<WorkflowAgentSession> stateStore, IDbContextFactory<MohistDbContext> dbFactory, IEventBus eventBus, ILogger<WorkflowAgentSessionGrain> log)
    {
        _stateStore = stateStore;
        _dbFactory = dbFactory;
        _eventBus = eventBus;
        _log = log;
    }

    private string SessionId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _session = await _stateStore.LoadAsync(SessionId);
    }

    public async Task<WorkflowAgentSessionInfo> EnsureAsync(EnsureWorkflowAgentSessionCommand command)
    {
        if (_session is null)
        {
            var existing = await LoadByWorkflowAndSessionAsync(command.WorkflowRunId, command.SessionName);
            if (existing is not null)
            {
                _session = existing;
                if (_session.IsTerminal && command.SessionName != command.WorkId)
                    _session.StartNewWork(command.RunnerId, command.WorkId, command.WorkType, command.Stage, command.Title, command.IssueNumber, DateTime.UtcNow);
                else if (!_session.IsTerminal)
                    _session.MergeContext(command.RunnerId, command.WorkId, command.WorkType, command.Stage, command.Title, command.IssueNumber);
            }
            else
                _session = CreateSession(command);
        }
        else
        {
            if (_session.IsTerminal && command.SessionName != command.WorkId)
                _session.StartNewWork(command.RunnerId, command.WorkId, command.WorkType, command.Stage, command.Title, command.IssueNumber, DateTime.UtcNow);
            else if (!_session.IsTerminal)
                _session.MergeContext(command.RunnerId, command.WorkId, command.WorkType, command.Stage, command.Title, command.IssueNumber);
        }

        await _stateStore.SaveAsync(SessionId, _session);
        return ToInfo(_session);
    }

    private WorkflowAgentSession CreateSession(EnsureWorkflowAgentSessionCommand command) =>
        WorkflowAgentSession.Create(
            SessionId,
            command.ProjectId,
            command.IssueNumber ?? 0,
            command.WorkflowRunId,
            command.SessionName,
            command.RunnerId,
            command.WorkId,
            command.WorkType,
            command.Stage,
            command.Title);

    public async Task<WorkflowAgentSessionInfo> AttachAgentAsync(AttachAgentCommand command)
    {
        var session = await GetOrCreateAsync();

        var now = DateTime.UtcNow;
        if (!session.AttachAgent(command.AgentSessionId, command.Model, command.WorkDir, command.ChangeDir, command.ProcessPid, now))
            return ToInfo(session);

        await _stateStore.SaveAsync(SessionId, session);
        EmitStarted(session);
        return ToInfo(session);
    }

    public async Task<IReadOnlyList<WorkflowAgentSessionEventInfo>> AppendEventsAsync(AppendWorkflowAgentSessionEventsCommand command)
    {
        if (command.Events.Count == 0) return [];

        var session = await GetOrCreateAsync();
        if (session.IsTerminal) return [];

        var now = DateTime.UtcNow;
        session.RecordActivity(now);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var nextSequence = await db.WorkflowAgentSessionEvents
            .Where(e => e.SessionId == session.Id)
            .Select(e => (long?)e.Sequence)
            .MaxAsync() ?? 0;

        var entries = new List<WorkflowAgentSessionEventRow>();

        foreach (var e in command.Events)
        {
            if (e.Type == "agent_liveness_status")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                var status = GetStringProp(payload, "status") ?? "running";
                session.MarkActive(status, now, GetStringProp(payload, "failureReason"));
            }
            else if (e.Type == "agent_session_terminal")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                var status = GetStringProp(payload, "status") ?? "completed";
                var failureReason = GetStringProp(payload, "failureReason");
                var exitCode = GetIntProp(payload, "exitCode");
                if (status == "failed")
                    session.Fail(now, failureReason, exitCode);
                else if (status == "cancelled")
                    session.Cancel(now, failureReason, exitCode);
                else
                    session.Complete(now, exitCode);
            }

            entries.Add(new WorkflowAgentSessionEventRow
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
                Sequence = ++nextSequence,
                Type = e.Type,
                PayloadJson = string.IsNullOrWhiteSpace(e.PayloadJson) ? "{}" : e.PayloadJson,
                CreatedAt = now,
            });
        }

        session.EnsureActive(now);

        var isTerminal = session.IsTerminal;
        var statusChanged = entries.Any(r => r.Type == "agent_liveness_status");

        db.WorkflowAgentSessionEvents.AddRange(entries);
        await db.SaveChangesAsync();
        await _stateStore.SaveAsync(SessionId, session);
        _session = session;

        foreach (var entry in entries)
            EmitTranscriptEntry(session, entry);

        if (statusChanged)
            EmitStatusChanged(session);
        if (isTerminal)
            EmitTerminal(session, AgentSessionStatusNames.ToName(session.Status));

        return entries.Select(e => ToEventInfo(e)).ToList();
    }

    public async Task<WorkflowAgentSessionInfo?> FailIfRunningAsync(string reason)
    {
        var session = await GetOrCreateAsync();
        if (session.IsTerminal) return ToInfo(session);

        session.Fail(DateTime.UtcNow, reason);
        await _stateStore.SaveAsync(SessionId, session);
        EmitTerminal(session, "failed");
        return ToInfo(session);
    }

    public async Task<WorkflowAgentSessionInfo?> GetAsync()
    {
        return _session is null ? null : ToInfo(_session);
    }

    private async Task<WorkflowAgentSession?> LoadByWorkflowAndSessionAsync(string workflowRunId, string sessionName)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.WorkflowAgentSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkflowRunId == workflowRunId && s.SessionName == sessionName);
        return row is null ? null : new WorkflowAgentSession
        {
            Id = row.Id,
            ProjectId = row.ProjectId,
            IssueNumber = row.IssueNumber,
            WorkflowRunId = row.WorkflowRunId,
            SessionName = row.SessionName,
            WorkId = row.WorkId,
            WorkType = row.WorkType,
            Stage = row.Stage,
            Title = row.Title,
            RunnerId = row.RunnerId,
            AgentSessionId = row.AgentSessionId,
            Status = AgentSessionStatusNames.Parse(row.Status),
            Model = row.Model,
            WorkDir = row.WorkDir,
            ChangeDir = row.ChangeDir,
            ProcessPid = row.ProcessPid,
            CreatedAt = row.CreatedAt,
            StartedAt = row.StartedAt,
            LastDataAt = row.LastDataAt,
            LastHeartbeatAt = row.LastHeartbeatAt,
            CompletedAt = row.CompletedAt,
            FailureReason = row.FailureReason,
            ExitCode = row.ExitCode,
        };
    }

    private async Task<WorkflowAgentSession> GetOrCreateAsync()
    {
        if (_session is not null) return _session;

        var parts = SessionId.Split('/');
        var projectId = parts.Length > 0 ? parts[0] : string.Empty;
        var workflowRunId = parts.Length > 1 ? parts[1] : string.Empty;
        var sessionName = parts.Length > 2 ? parts[2] : string.Empty;

        _session = WorkflowAgentSession.Create(SessionId, projectId, 0, workflowRunId, sessionName, null);
        await _stateStore.SaveAsync(SessionId, _session);
        return _session;
    }

    private void EmitStarted(WorkflowAgentSession session) => _eventBus.Emit("coder_session_started", new
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

    private void EmitTranscriptEntry(WorkflowAgentSession session, WorkflowAgentSessionEventRow entry)
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

    private void EmitStatusChanged(WorkflowAgentSession session) => _eventBus.Emit("coder_session_status_changed", new
    {
        issueId = session.IssueNumber.ToString(),
        session.ProjectId,
        coderSessionId = session.Id,
        acpSessionId = session.AgentSessionId ?? session.Id,
        status = AgentSessionStatusNames.ToName(session.Status),
        lastDataAt = session.LastDataAt?.ToString("o"),
        failureReason = session.FailureReason,
    });

    private void EmitTerminal(WorkflowAgentSession session, string terminal) => _eventBus.Emit(terminal switch
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

    private static long DurationMs(WorkflowAgentSession session)
    {
        var start = session.StartedAt ?? session.CreatedAt;
        var end = session.CompletedAt ?? DateTime.UtcNow;
        return Math.Max(0, (long)(end - start).TotalMilliseconds);
    }

    private static WorkflowAgentSessionInfo ToInfo(WorkflowAgentSession s) => new(
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
        AgentSessionStatusNames.ToName(s.Status),
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

    private static WorkflowAgentSessionEventInfo ToEventInfo(WorkflowAgentSessionEventRow e) => new(
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
