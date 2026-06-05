using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Domain.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Sessions.Grains;

public sealed class AgentSessionGrain : Grain, IAgentSessionGrain
{
    private readonly IStateStore<AgentSession> _stateStore;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventBus _eventBus;
    private readonly ILogger<AgentSessionGrain> _log;
    private AgentSession? _session;

    public AgentSessionGrain(IStateStore<AgentSession> stateStore, IDbContextFactory<MohistDbContext> dbFactory, IEventBus eventBus, ILogger<AgentSessionGrain> log)
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

    public async Task<AgentSessionInfo> EnsureAsync(EnsureAgentSessionCommand command)
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
            else
                _session.MergeContext(command.RunnerId, command.WorkId, command.WorkType, command.Stage, command.Title, command.IssueNumber);
        }

        await _stateStore.SaveAsync(SessionId, _session);
        return await ToInfoAsync(_session);
    }

    private AgentSession CreateSession(EnsureAgentSessionCommand command) =>
        AgentSession.Create(
            SessionId,
            command.RunnerId ?? string.Empty,
            "opencode",
            null,
            metadata: BuildMetadata(command));

    private static AgentSessionMetadata BuildMetadata(EnsureAgentSessionCommand command) =>
        new AgentSessionMetadata()
            .WithLabel(AgentSessionMetadataKeys.ProjectId, command.ProjectId)
            .WithLabel(AgentSessionMetadataKeys.IssueNumber, command.IssueNumber is > 0 ? command.IssueNumber.Value.ToString() : null)
            .WithLabel(AgentSessionMetadataKeys.SourceKind, "workflow")
            .WithLabel(AgentSessionMetadataKeys.SourceId, command.WorkflowRunId)
            .WithLabel(AgentSessionMetadataKeys.SessionName, command.SessionName)
            .WithAnnotation(AgentSessionMetadataKeys.TaskId, command.WorkId)
            .WithAnnotation(AgentSessionMetadataKeys.TaskKind, command.WorkType)
            .WithAnnotation(AgentSessionMetadataKeys.Phase, command.Stage)
            .WithAnnotation(AgentSessionMetadataKeys.Title, command.Title);

    public async Task<AgentSessionInfo> AttachAgentAsync(AttachAgentCommand command)
    {
        var session = await GetOrCreateAsync();

        var now = DateTime.UtcNow;
        if (!session.AttachAgent(command.AgentSessionId, command.Model, command.WorkDir, command.ChangeDir, command.ProcessPid, now))
            return await ToInfoAsync(session);

        await _stateStore.SaveAsync(SessionId, session);
        EmitStarted(session);
        return await ToInfoAsync(session);
    }

    public async Task<IReadOnlyList<AgentSessionEventInfo>> AppendEventsAsync(AppendAgentSessionEventsCommand command)
    {
        if (command.Events.Count == 0) return [];

        var session = await GetOrCreateAsync();
        var wasTerminal = session.IsTerminal;

        var now = DateTime.UtcNow;
        if (!wasTerminal)
            session.RecordActivity(now);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var nextSequence = await db.AgentSessionEvents
            .Where(e => e.SessionId == session.Id)
            .Select(e => (long?)e.Sequence)
            .MaxAsync() ?? 0;

        var entries = new List<AgentSessionEventRow>();

        foreach (var e in command.Events)
        {
            if (!wasTerminal)
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
                    var failureCategory = GetStringProp(payload, "failureCategory");
                    var exitCode = GetIntProp(payload, "exitCode");
                    if (status == "failed")
                        session.Fail(now, failureReason, exitCode);
                    else if (status == "cancelled")
                        session.Cancel(now, failureReason, exitCode);
                    else
                        session.Complete(now, exitCode);
                }
                else if (e.Type == "agent_usage_update")
                {
                    var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                    var inputTokens = GetLongProp(payload, "inputTokens");
                    var outputTokens = GetLongProp(payload, "outputTokens");
                    var totalTokens = GetLongProp(payload, "totalTokens");
                    var cachedReadTokens = GetLongProp(payload, "cachedReadTokens");
                    var thoughtTokens = GetLongProp(payload, "thoughtTokens");

                    var costAmount = GetDoubleProp(payload, "costAmount");
                    var costCurrency = GetStringProp(payload, "costCurrency");
                    if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("cost", out var costProp) && costProp.ValueKind == JsonValueKind.Object)
                    {
                        costAmount ??= GetDoubleProp(costProp, "amount");
                        costCurrency ??= GetStringProp(costProp, "currency");
                    }

                    var contextWindowSize = GetLongProp(payload, "contextWindowSize");
                    var contextWindowUsed = GetLongProp(payload, "contextWindowUsed");
                    if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("contextWindow", out var cwProp) && cwProp.ValueKind == JsonValueKind.Object)
                    {
                        contextWindowSize ??= GetLongProp(cwProp, "size");
                        contextWindowUsed ??= GetLongProp(cwProp, "used");
                    }

                    session.ApplyUsage(inputTokens, outputTokens, totalTokens, cachedReadTokens, thoughtTokens, costAmount, costCurrency, contextWindowUsed, contextWindowSize);
                }
                else if (e.Type == "agent_session_model_resolved")
                {
                    // Resolved model is an event-level observation; consumers project it from AgentSessionEvents.
                }
                else if (e.Type == "tool_call")
                {
                    // Tool calls are transcript events, not AgentSession domain state.
                }
                else if (e.Type == "tool_call_update")
                {
                    // Tool call state is projected from AgentSessionEvents.
                }
            }

            entries.Add(new AgentSessionEventRow
            {
                SessionId = session.Id,
                ProjectId = session.ProjectId,
                IssueNumber = session.IssueNumber,
                WorkflowRunId = session.RunId,
                SessionName = session.SessionName,
                AgentSessionId = session.Status.AgentRuntimeSessionId,
                WorkId = command.WorkId ?? session.TaskId,
                WorkType = command.WorkType ?? session.TaskKind,
                Stage = command.Stage ?? session.Phase,
                Sequence = ++nextSequence,
                Type = e.Type,
                PayloadJson = string.IsNullOrWhiteSpace(e.PayloadJson) ? "{}" : e.PayloadJson,
                CreatedAt = now,
            });
        }

        if (!wasTerminal)
            session.EnsureActive(now);

        var isTerminal = session.IsTerminal;
        var statusChanged = entries.Any(r => r.Type == "agent_liveness_status");

        db.AgentSessionEvents.AddRange(entries);
        await db.SaveChangesAsync();
        await _stateStore.SaveAsync(SessionId, session);
        _session = session;

        foreach (var entry in entries)
            EmitTranscriptEntry(session, entry);

        if (statusChanged)
            EmitStatusChanged(session);
        if (isTerminal)
            EmitTerminal(session, AgentSessionStatusNames.ToName(session.Status.Phase));

        return entries.Select(e => ToEventInfo(e)).ToList();
    }

    public async Task<AgentSessionInfo?> FailIfRunningAsync(string reason)
    {
        var session = await GetOrCreateAsync();
        if (session.IsTerminal) return await ToInfoAsync(session);

        session.Fail(DateTime.UtcNow, reason);
        await _stateStore.SaveAsync(SessionId, session);
        EmitTerminal(session, "failed");
        return await ToInfoAsync(session);
    }

    public async Task<AgentSessionInfo?> GetAsync()
    {
        return _session is null ? null : await ToInfoAsync(_session);
    }

    private async Task<AgentSession?> LoadByWorkflowAndSessionAsync(string workflowRunId, string sessionName)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.AgentSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkflowRunId == workflowRunId && s.SessionName == sessionName);
        return row is null ? null : AgentSessionJson.Deserialize(row);
    }

    private async Task<AgentSession> GetOrCreateAsync()
    {
        if (_session is not null) return _session;

        var parts = SessionId.Split('/');
        var projectId = parts.Length > 0 ? parts[0] : string.Empty;
        var workflowRunId = parts.Length > 1 ? parts[1] : string.Empty;
        var sessionName = parts.Length > 2 ? parts[2] : string.Empty;

        var metadata = new AgentSessionMetadata()
            .WithLabel(AgentSessionMetadataKeys.ProjectId, projectId)
            .WithLabel(AgentSessionMetadataKeys.SourceKind, "workflow")
            .WithLabel(AgentSessionMetadataKeys.SourceId, workflowRunId)
            .WithLabel(AgentSessionMetadataKeys.SessionName, sessionName);
        _session = AgentSession.Create(SessionId, string.Empty, "opencode", null, metadata: metadata);
        await _stateStore.SaveAsync(SessionId, _session);
        return _session;
    }

    private void EmitStarted(AgentSession session) => _eventBus.Emit("coder_session_started", new CoderSessionStartedEvent(
        session.IssueNumber.ToString(),
        session.ProjectId,
        session.Id,
        session.Status.AgentRuntimeSessionId ?? session.Id,
        session.TaskId,
        session.Settings.Model,
        session.Phase,
        session.Title,
        session.Title));

    private void EmitTranscriptEntry(AgentSession session, AgentSessionEventRow entry)
    {
        var text = ExtractText(entry.PayloadJson);
        if (entry.Type is "agent_message_chunk" or "agent_output_chunk")
        {
            _eventBus.Emit("coder_text_chunk", new CoderTranscriptEntryEvent(
                session.IssueNumber.ToString(),
                session.ProjectId,
                session.TaskId,
                session.Status.AgentRuntimeSessionId ?? session.Id,
                session.Id,
                text));
        }
        else if (entry.Type is "agent_thought_chunk")
        {
            _eventBus.Emit("coder_thought_chunk", new CoderTranscriptEntryEvent(
                session.IssueNumber.ToString(),
                session.ProjectId,
                session.TaskId,
                session.Status.AgentRuntimeSessionId ?? session.Id,
                session.Id,
                text));
        }
        else if (entry.Type is "tool_call" or "tool_call_update")
        {
            var tool = ParseToolCall(entry.PayloadJson, entry.Type);
            if (tool is null) return;

            _eventBus.Emit("coder_tool_call", new CoderToolCallEvent(
                session.IssueNumber.ToString(),
                session.ProjectId,
                session.TaskId,
                session.Status.AgentRuntimeSessionId ?? session.Id,
                session.Id,
                tool.ToolName,
                tool.State,
                tool.ToolCallId,
                tool.Title,
                tool.RawInput,
                tool.RawOutput,
                tool.Metadata,
                tool.Details,
                tool.NormalizedName,
                tool.DisplayTitle,
                tool.DisplaySubtitle,
                tool.Category));
        }
    }

    private void EmitStatusChanged(AgentSession session) => _eventBus.Emit("coder_session_status_changed", new CoderSessionStatusChangedEvent(
        session.IssueNumber.ToString(),
        session.ProjectId,
        session.Id,
        session.Status.AgentRuntimeSessionId ?? session.Id,
        AgentSessionStatusNames.ToName(session.Status.Phase),
        session.Status.LastDataAt?.ToString("o"),
        session.Status.FailureReason));

    private void EmitTerminal(AgentSession session, string terminal) => _eventBus.Emit(terminal switch
    {
        "failed" => "coder_session_failed",
        "cancelled" => "coder_session_cancelled",
        _ => "coder_session_completed"
    }, new CoderSessionTerminalEvent(
        session.IssueNumber.ToString(),
        session.ProjectId,
        session.Id,
        terminal,
        session.Status.FailureReason,
        DurationMs(session)));

    private static long DurationMs(AgentSession session)
    {
        var start = session.Status.StartedAt ?? session.Status.CreatedAt;
        var end = session.Status.CompletedAt ?? DateTime.UtcNow;
        return Math.Max(0, (long)(end - start).TotalMilliseconds);
    }

    private async Task<AgentSessionInfo> ToInfoAsync(AgentSession s)
    {
        var eventSummary = await LoadEventSummaryAsync(s.Id);
        var usage = s.Status.UsageSummary ?? new AgentUsageSummary();
        return new AgentSessionInfo(
        s.Id,
        s.ProjectId,
        s.IssueNumber == 0 ? null : s.IssueNumber,
        s.RunId,
        s.SessionName,
        s.TaskId,
        s.TaskKind,
        s.Phase,
        s.Title,
        s.Runtime.RunnerId,
        s.Status.AgentRuntimeSessionId,
        AgentSessionStatusNames.ToName(s.Status.Phase),
        s.Settings.Model,
        s.Runtime.WorkDir,
        s.ChangeDir,
        null,
        s.Status.CreatedAt.ToString("o"),
        s.Status.StartedAt?.ToString("o"),
        s.Status.LastDataAt?.ToString("o"),
        s.Status.CompletedAt?.ToString("o"),
        s.Status.FailureReason,
        s.Status.ExitCode,
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
        eventSummary.ToolErrorCount);
    }

    private async Task<AgentSessionEventSummary> LoadEventSummaryAsync(string sessionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var events = await db.AgentSessionEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .ToListAsync();

        string? resolvedModel = null;
        string? failureCategory = null;
        var toolCalls = 0;
        var toolErrors = 0;

        foreach (var e in events)
        {
            if (e.Type == "agent_session_model_resolved")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                resolvedModel = GetStringProp(payload, "resolvedModel") ?? resolvedModel;
            }
            else if (e.Type == "agent_session_terminal")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                failureCategory = GetStringProp(payload, "failureCategory") ?? failureCategory;
            }
            else if (e.Type == "tool_call")
            {
                toolCalls++;
            }
            else if (e.Type == "tool_call_update")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                var status = GetStringProp(payload, "status") ?? GetStringProp(payload, "state");
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    toolErrors++;
            }
        }

        return new AgentSessionEventSummary(
            resolvedModel,
            failureCategory,
            toolCalls == 0 ? null : toolCalls,
            toolErrors == 0 ? null : Math.Min(toolErrors, Math.Max(toolCalls, toolErrors)));
    }

    private static AgentSessionEventInfo ToEventInfo(AgentSessionEventRow e) => new(
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
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static int? GetIntProp(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt32()
            : null;
    }

    private static long? GetLongProp(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt64()
            : null;
    }

    private static double? GetDoubleProp(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetDouble()
            : null;
    }

    private static ToolCallProjection? ParseToolCall(string json, string eventType)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var payload = doc.RootElement;
            var nested = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("toolCall", out var toolCall)
                ? toolCall
                : default;

            var toolCallId = GetStringProp(nested, "toolCallId")
                ?? GetStringProp(payload, "toolCallId")
                ?? GetStringProp(payload, "id")
                ?? GetStringProp(payload, "callId");
            if (string.IsNullOrWhiteSpace(toolCallId)) return null;

            var toolName = GetStringProp(nested, "toolName")
                ?? GetStringProp(payload, "toolName")
                ?? GetStringProp(payload, "name")
                ?? GetStringProp(payload, "kind")
                ?? "unknown";
            var status = GetStringProp(nested, "status")
                ?? GetStringProp(payload, "status")
                ?? (eventType == "tool_call_update" ? "completed" : "started");

            return new ToolCallProjection(
                toolName,
                MapToolState(status),
                toolCallId,
                GetStringProp(nested, "title") ?? GetStringProp(payload, "title"),
                CloneProperty(nested, "input") ?? CloneProperty(payload, "rawInput") ?? CloneProperty(payload, "input"),
                CloneProperty(nested, "output") ?? CloneProperty(payload, "rawOutput") ?? CloneProperty(payload, "output"),
                CloneProperty(nested, "metadata") ?? CloneProperty(payload, "metadata") ?? CloneProperty(payload, "rawOutputMetadata"),
                CloneProperty(nested, "details") ?? CloneProperty(payload, "details"),
                GetStringProp(nested, "normalizedName") ?? GetStringProp(payload, "normalizedName"),
                GetStringProp(nested, "displayTitle") ?? GetStringProp(payload, "displayTitle"),
                GetStringProp(nested, "displaySubtitle") ?? GetStringProp(payload, "displaySubtitle"),
                GetStringProp(nested, "category") ?? GetStringProp(payload, "category"));
        }
        catch
        {
            return null;
        }
    }

    private static string MapToolState(string status) => status switch
    {
        "pending" or "in_progress" or "running" => "started",
        "completed" or "failed" or "cancelled" or "timeout" => status,
        _ => status
    };

    private static JsonElement? CloneProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.Clone();
    }

    private sealed record ToolCallProjection(
        string ToolName,
        string State,
        string ToolCallId,
        string? Title,
        JsonElement? RawInput,
        JsonElement? RawOutput,
        JsonElement? Metadata,
        JsonElement? Details,
        string? NormalizedName,
        string? DisplayTitle,
        string? DisplaySubtitle,
        string? Category);

    private sealed record AgentSessionEventSummary(
        string? ResolvedModel,
        string? FailureCategory,
        int? ToolCallCount,
        int? ToolErrorCount);
}
