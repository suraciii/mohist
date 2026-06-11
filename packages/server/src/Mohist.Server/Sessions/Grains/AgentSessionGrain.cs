using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Domain.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Sessions.Grains;

public sealed class AgentSessionGrain : Grain, IAgentSessionGrain
{
    private const int TranscriptFlushRawEventThreshold = 32;
    private const int TranscriptFlushTextLengthThreshold = 4096;
    private static readonly TimeSpan TranscriptFlushAgeThreshold = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> TranscriptEventTypes = new(StringComparer.Ordinal)
    {
        "mohist_prompt",
        "coder_text_chunk",
        "coder_thought_chunk",
        "coder_tool_call",
        "agent_message_chunk",
        "agent_thought_chunk",
        "tool_call",
        "tool_call_update",
        "ralph_task_update",
        "ralph_loop_progress",
        "agent_liveness_status",
        "agent_usage_update",
        "agent_session_model_resolved",
        "agent_session_terminal",
    };

    private readonly IAgentSessionStore _stateStore;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventPublisher _eventPublisher;
    private readonly ITranscriptEventPublisher _transcriptPublisher;
    private readonly ILogger<AgentSessionGrain> _log;
    private readonly TranscriptAccumulator _transcript = new();
    private AgentSession? _session;
    private long _realtimeSequence;

    public AgentSessionGrain(
        IAgentSessionStore stateStore,
        IDbContextFactory<MohistDbContext> dbFactory,
        IEventPublisher eventPublisher,
        ITranscriptEventPublisher transcriptPublisher,
        ILogger<AgentSessionGrain> log)
    {
        _stateStore = stateStore;
        _dbFactory = dbFactory;
        _eventPublisher = eventPublisher;
        _transcriptPublisher = transcriptPublisher;
        _log = log;
    }

    private string SessionId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _session = await _stateStore.LoadAsync(SessionId);
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        if (_session is null || !_transcript.HasPending)
            return;

        var now = DateTime.UtcNow;
        await EnsureTranscriptInitializedAsync(_session.Id);
        var segments = _transcript.Flush(_session, now);
        if (segments.Count == 0)
            return;

        await _stateStore.SaveTranscriptAsync(SessionId, _session, [], segments, ct);
    }

    public async Task<AgentSessionInfo> EnsureAsync(EnsureAgentSessionCommand command)
    {
        var wasTerminal = _session?.IsTerminal ?? false;
        if (_session is null)
        {
            var existing = await LoadByWorkflowAndSessionAsync(command.WorkflowRunId, command.SessionName);
            if (existing is not null)
            {
                _session = existing;
                wasTerminal = _session.IsTerminal;
                if (!_session.IsTerminal)
                    _ = _session.MergeContext(command.RunnerId, command.WorkId, command.WorkType, command.Stage, command.Title, command.IssueNumber);
            }
            else
                _session = CreateSession(command);
        }
        else
        {
            if (!_session.IsTerminal)
                _ = _session.MergeContext(command.RunnerId, command.WorkId, command.WorkType, command.Stage, command.Title, command.IssueNumber);
        }

        await _stateStore.SaveAsync(SessionId, _session);
        if (!wasTerminal)
            await UpdateProjectionAsync(command);
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
            .WithLabel(AgentSessionMetadataKeys.SourceKind, AgentSessionKey.Workflow)
            .WithLabel(AgentSessionMetadataKeys.SourceId, command.WorkflowRunId)
            .WithLabel(AgentSessionMetadataKeys.SessionName, command.SessionName)
            .WithLabel(AgentSessionMetadataKeys.WorkId, command.WorkId)
            .WithLabel(AgentSessionMetadataKeys.WorkType, command.WorkType)
            .WithLabel(AgentSessionMetadataKeys.Stage, command.Stage)
            .WithAnnotation(AgentSessionMetadataKeys.Title, command.Title);

    private async Task UpdateProjectionAsync(EnsureAgentSessionCommand command)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.AgentSessions.FindAsync(SessionId);
        if (row is null) return;

        row.WorkId ??= command.WorkId;
        row.WorkType ??= command.WorkType;
        row.Stage ??= command.Stage;
        await db.SaveChangesAsync();
    }

    public async Task<AgentSessionInfo> AttachAgentAsync(AttachAgentCommand command)
    {
        var session = await GetOrCreateAsync();

        var now = DateTime.UtcNow;
        var events = session.AttachAgent(command.AgentSessionId, command.Model, command.WorkDir, command.ChangeDir, command.ProcessPid, now);
        if (events.Count == 0)
            return await ToInfoAsync(session);

        await CommitAsync(session, events);
        return await ToInfoAsync(session);
    }

    public async Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendRuntimeEventsAsync(AppendAgentSessionRuntimeEventsCommand command)
    {
        if (command.Events.Count == 0) return [];

        var session = await GetOrCreateAsync();
        var wasTerminal = session.IsTerminal;
        var phaseBefore = session.Status.Phase;
        var statusBefore = AgentSessionStatusNames.ToName(phaseBefore);

        var now = DateTime.UtcNow;
        var events = new List<AgentSessionEvent>();
        if (!wasTerminal)
            events.AddRange(session.RecordActivity(now));
        var projection = await LoadProjectionAsync(session.Id);

        await EnsureTranscriptInitializedAsync(session.Id);

        var entries = new List<AgentSessionRuntimeEventRow>();
        var statusChangedThisCall = new List<string>();
        var terminalThisCall = false;
        AgentSessionEvent? terminalEvent = null;
        string? terminalStatus = null;

        foreach (var e in command.Events)
        {
            if (!wasTerminal)
            {
                if (e.Type == "agent_liveness_status")
                {
                    var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                    var status = GetStringProp(payload, "status") ?? "running";
                    var failureReason = GetStringProp(payload, "failureReason");
                    var activatedEvents = session.MarkActive(status, now, failureReason);
                    events.AddRange(activatedEvents);
                    if (activatedEvents.Count > 0)
                        statusChangedThisCall.Add(AgentSessionStatusNames.ToName(session.Status.Phase));
                }
                else if (e.Type == "agent_session_terminal")
                {
                    var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                    var status = GetStringProp(payload, "status") ?? "completed";
                    var failureReason = GetStringProp(payload, "failureReason");
                    var failureCategory = GetStringProp(payload, "failureCategory");
                    var exitCode = GetIntProp(payload, "exitCode");
                    AgentSessionEvent? terminalEvt = null;
                    if (status == "failed")
                        terminalEvt = EmitAndCapture(session.Fail(now, failureReason, exitCode), events);
                    else if (status == "cancelled")
                        terminalEvt = EmitAndCapture(session.Cancel(now, failureReason, exitCode), events);
                    else
                        terminalEvt = EmitAndCapture(session.Complete(now, exitCode), events);
                    if (terminalEvt is not null && !terminalThisCall)
                    {
                        terminalThisCall = true;
                        terminalEvent = terminalEvt;
                        terminalStatus = status;
                    }
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

                    events.AddRange(session.ApplyUsage(inputTokens, outputTokens, totalTokens, cachedReadTokens, thoughtTokens, costAmount, costCurrency, contextWindowUsed, contextWindowSize));
                }
                else if (e.Type == "agent_session_model_resolved")
                {
                    // Resolved model is an event-level observation; consumers project it from transcript segments.
                }
                else if (e.Type == "tool_call")
                {
                    // Tool calls are transcript events, not AgentSession domain state.
                }
                else if (e.Type == "tool_call_update")
                {
                    // Tool call state is projected from transcript segments.
                }
            }

            entries.Add(new AgentSessionRuntimeEventRow
            {
                Id = -(_realtimeSequence + 1),
                SessionId = session.Id,
                ProjectId = session.ProjectId,
                IssueNumber = session.IssueNumber,
                WorkflowRunId = session.RunId,
                SessionName = session.SessionName,
                AgentSessionId = session.Status.AgentRuntimeSessionId,
                WorkId = command.WorkId ?? projection?.WorkId,
                WorkType = command.WorkType ?? projection?.WorkType,
                Stage = command.Stage ?? projection?.Stage,
                Sequence = ++_realtimeSequence,
                Type = e.Type,
                PayloadJson = string.IsNullOrWhiteSpace(e.PayloadJson) ? "{}" : e.PayloadJson,
                CreatedAt = now,
            });
        }

        if (!wasTerminal)
            events.AddRange(session.EnsureActive(now));

        var firstRowThisCall = !wasTerminal
            && phaseBefore == AgentSessionStatus.Created
            && session.Status.StartedAt is null;

        var transcriptSegments = _transcript.Accept(
            session,
            entries,
            now,
            forceFlushPending: terminalThisCall || session.IsTerminal || command.Events.Count > 1);

        if (events.Count > 0 || transcriptSegments.Count > 0)
            await _stateStore.SaveTranscriptAsync(SessionId, session, events, transcriptSegments);
        _session = session;

        await FanOutRealtimeAsync(
            session,
            entries,
            firstRowThisCall,
            terminalThisCall,
            terminalEvent,
            terminalStatus,
            statusChangedThisCall,
            statusBefore);

        return entries.Select(e => ToEventInfo(e)).ToList();
    }

    private static AgentSessionEvent? EmitAndCapture(IReadOnlyList<AgentSessionEvent> domainEvents, List<AgentSessionEvent> sink)
    {
        if (domainEvents.Count == 0) return null;
        sink.AddRange(domainEvents);
        return domainEvents[domainEvents.Count - 1];
    }

    private async Task FanOutRealtimeAsync(
        AgentSession session,
        IReadOnlyList<AgentSessionRuntimeEventRow> entries,
        bool firstRowThisCall,
        bool terminalThisCall,
        AgentSessionEvent? terminalEvent,
        string? terminalStatus,
        IReadOnlyList<string> statusChangedThisCall,
        string statusBefore)
    {
        var source = AgentSessionSource(session);

        if (firstRowThisCall)
        {
            try
            {
                var startedEvent = new AgentSessionStarted(session.Status.AgentRuntimeSessionId ?? string.Empty);
                var type = AgentSessionEventSerializer.BusType(startedEvent);
                var data = AgentSessionEventSerializer.ToData(startedEvent);
                await _eventPublisher.PublishAsync(
                    data,
                    type,
                    source,
                    subject: session.SessionName,
                    ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to publish AgentSessionStarted for {SessionId}",
                    session.Id);
            }
        }

        if (terminalThisCall && terminalEvent is not null)
        {
            try
            {
                await PublishAgentSessionLifecycleAsync(terminalEvent, source, session.SessionName);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to publish {Status} for {SessionId}",
                    terminalStatus ?? "terminal", session.Id);
            }
        }

        var lastDistinctStatus = statusBefore;
        foreach (var newStatus in statusChangedThisCall)
        {
            if (string.Equals(newStatus, lastDistinctStatus, StringComparison.Ordinal))
                continue;
            lastDistinctStatus = newStatus;

            try
            {
                var statusEvent = new AgentSessionStatusChanged(newStatus, session.Status.FailureReason);
                var type = AgentSessionEventSerializer.BusType(statusEvent);
                var data = AgentSessionEventSerializer.ToData(statusEvent);
                await _eventPublisher.PublishAsync(
                    data,
                    type,
                    source,
                    subject: session.SessionName,
                    ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to publish AgentSessionStatusChanged for {SessionId}",
                    session.Id);
            }
        }

        if (entries.Count == 0) return;

        foreach (var row in entries)
        {
            if (!TranscriptEventTypes.Contains(row.Type))
                continue;

            JsonElement payload;
            try
            {
                payload = JsonSerializer.Deserialize<JsonElement>(row.PayloadJson);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "AgentSessionGrain failed to deserialise transcript payload for {Type} on {SessionId}",
                    row.Type, session.Id);
                continue;
            }

            var envelope = new TranscriptEnvelope(
                Id: row.Id.ToString(),
                SessionId: row.SessionId,
                ProjectId: row.ProjectId,
                IssueNumber: row.IssueNumber,
                WorkflowRunId: row.WorkflowRunId,
                SessionName: row.SessionName,
                AgentSessionId: row.AgentSessionId,
                WorkId: row.WorkId,
                WorkType: row.WorkType,
                Stage: row.Stage,
                Sequence: row.Sequence,
                Type: row.Type,
                Payload: payload,
                CreatedAt: row.CreatedAt.ToString("o"));

            try
            {
                await _transcriptPublisher.PublishAsync(envelope, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "AgentSessionGrain transcript publish failed for {Type} on {SessionId}",
                    row.Type, session.Id);
            }
        }
    }

    public async Task<AgentSessionInfo?> FailIfRunningAsync(string reason)
    {
        var session = await GetOrCreateAsync();
        if (session.IsTerminal) return await ToInfoAsync(session);

        var events = session.Fail(DateTime.UtcNow, reason);
        await CommitAsync(session, events);
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

    private async Task EnsureTranscriptInitializedAsync(string sessionId)
    {
        if (_transcript.IsInitialized)
            return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var latestTranscriptSequence = await db.AgentSessionTranscriptSegments
            .Where(e => e.SessionId == sessionId)
            .Select(e => (long?)e.Sequence)
            .MaxAsync() ?? 0;
        var latestRuntimeSequence = await db.AgentSessionRuntimeEvents
            .Where(e => e.SessionId == sessionId)
            .Select(e => (long?)e.Sequence)
            .MaxAsync() ?? 0;
        var latestSequence = Math.Max(latestTranscriptSequence, latestRuntimeSequence);
        _transcript.Initialize(latestSequence + 1);
        _realtimeSequence = Math.Max(_realtimeSequence, latestSequence);
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
            .WithLabel(AgentSessionMetadataKeys.SourceKind, AgentSessionKey.Workflow)
            .WithLabel(AgentSessionMetadataKeys.SourceId, workflowRunId)
            .WithLabel(AgentSessionMetadataKeys.SessionName, sessionName);
        _session = AgentSession.Create(SessionId, string.Empty, "opencode", null, metadata: metadata);
        await _stateStore.SaveAsync(SessionId, _session);
        return _session;
    }

    private async Task CommitAsync(AgentSession session, IReadOnlyList<AgentSessionEvent> events)
    {
        await _stateStore.SaveAsync(SessionId, session, events);
        _session = session;

        var source = AgentSessionSource(session);
        foreach (var domainEvent in events)
        {
            try
            {
                await PublishAgentSessionLifecycleAsync(domainEvent, source, session.SessionName);
            }
            catch (InvalidOperationException)
            {
                // Non-lifecycle AgentSession events are persisted but are not CloudEventBus notifications.
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to publish lifecycle event for {SessionId}",
                    session.Id);
            }
        }
    }

    private static string AgentSessionSource(AgentSession session) =>
        $"/mohist/agent-session/{session.Id}";

    private async Task PublishAgentSessionLifecycleAsync(AgentSessionEvent? domainEvent, string source, string subject)
    {
        if (domainEvent is null) return;
        var concrete = domainEvent.Value;
        var type = AgentSessionEventSerializer.BusType(concrete);
        var data = AgentSessionEventSerializer.ToData(concrete);
        await _eventPublisher.PublishAsync(
            data,
            type,
            source,
            subject: subject,
            ct: CancellationToken.None);
    }

    private static long DurationMs(AgentSession session)
    {
        var start = session.Status.StartedAt ?? session.Status.CreatedAt;
        var end = session.Status.CompletedAt ?? DateTime.UtcNow;
        return Math.Max(0, (long)(end - start).TotalMilliseconds);
    }

    private async Task<AgentSessionInfo> ToInfoAsync(AgentSession s)
    {
        var eventSummary = await LoadEventSummaryAsync(s.Id);
        var row = await LoadProjectionAsync(s.Id);
        var usage = s.Status.UsageSummary ?? new AgentUsageSummary();
        return new AgentSessionInfo(
        s.Id,
        s.ProjectId,
        s.IssueNumber == 0 ? null : s.IssueNumber,
        s.RunId,
        s.SessionName,
        row?.WorkId,
        row?.WorkType,
        row?.Stage,
        null,
        s.Runtime.RunnerId,
        s.Status.AgentRuntimeSessionId,
        AgentSessionStatusNames.ToName(s.Status.Phase),
        s.Settings.Model,
        s.Runtime.WorkDir,
        null,
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

    private async Task<AgentSessionRuntimeEventSummary> LoadEventSummaryAsync(string sessionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var transcriptSegments = await db.AgentSessionTranscriptSegments.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .ToListAsync();
        var runtimeEvents = await db.AgentSessionRuntimeEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .ToListAsync();
        var events = runtimeEvents
            .Concat(transcriptSegments.Select(ToRuntimeLike))
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToList();

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

        return new AgentSessionRuntimeEventSummary(
            resolvedModel,
            failureCategory,
            toolCalls == 0 ? null : toolCalls,
            toolErrors == 0 ? null : Math.Min(toolErrors, Math.Max(toolCalls, toolErrors)));
    }

    private async Task<AgentSessionRow?> LoadProjectionAsync(string sessionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AgentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId);
    }

    private static AgentSessionRuntimeEventInfo ToEventInfo(AgentSessionRuntimeEventRow e) => new(
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

    private static AgentSessionRuntimeEventRow ToRuntimeLike(AgentSessionTranscriptSegmentRow segment) => new()
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

    private sealed record AgentSessionRuntimeEventSummary(
        string? ResolvedModel,
        string? FailureCategory,
        int? ToolCallCount,
        int? ToolErrorCount);

    private sealed class TranscriptAccumulator
    {
        private long _nextSequence = 1;
        private PendingTextSegment? _pending;

        public bool IsInitialized { get; private set; }
        public bool HasPending => _pending is not null;

        public void Initialize(long nextSequence)
        {
            if (IsInitialized) return;
            _nextSequence = Math.Max(1, nextSequence);
            IsInitialized = true;
        }

        public IReadOnlyList<AgentSessionTranscriptSegmentRow> Accept(
            AgentSession session,
            IReadOnlyList<AgentSessionRuntimeEventRow> rows,
            DateTime now,
            bool forceFlushPending)
        {
            var segments = new List<AgentSessionTranscriptSegmentRow>();
            foreach (var row in rows)
            {
                var textKind = ToTextSegmentKind(row.Type);
                if (textKind is not null)
                {
                    AppendText(row, textKind, now, segments);
                    continue;
                }

                if (!TranscriptEventTypes.Contains(row.Type))
                    continue;

                FlushInto(session, now, segments);
                segments.Add(CreateSegment(row, row.Type, row.PayloadJson, correlationId: ExtractCorrelationId(row.PayloadJson), rawEventCount: 1, startedAt: row.CreatedAt, updatedAt: row.CreatedAt, completedAt: row.CreatedAt));
            }

            if (forceFlushPending)
                FlushInto(session, now, segments);

            return segments;
        }

        public IReadOnlyList<AgentSessionTranscriptSegmentRow> Flush(AgentSession session, DateTime now)
        {
            var segments = new List<AgentSessionTranscriptSegmentRow>();
            FlushInto(session, now, segments);
            return segments;
        }

        private void AppendText(AgentSessionRuntimeEventRow row, string kind, DateTime now, List<AgentSessionTranscriptSegmentRow> segments)
        {
            var text = ExtractText(row.PayloadJson);
            if (string.IsNullOrEmpty(text))
                return;

            var correlationId = ExtractCorrelationId(row.PayloadJson);
            if (_pending is not null
                && (!string.Equals(_pending.Kind, kind, StringComparison.Ordinal)
                    || !string.Equals(_pending.CorrelationId, correlationId, StringComparison.Ordinal)))
            {
                FlushPending(row, segments);
            }

            _pending ??= new PendingTextSegment(
                row.SessionId,
                row.ProjectId,
                row.IssueNumber,
                row.WorkflowRunId,
                row.SessionName,
                row.AgentSessionId,
                row.WorkId,
                row.WorkType,
                row.Stage,
                kind,
                correlationId,
                row.Type,
                row.CreatedAt);

            _pending.Append(text, row.Type, row.CreatedAt);

            if (_pending.RawEventCount >= TranscriptFlushRawEventThreshold
                || _pending.Text.Length >= TranscriptFlushTextLengthThreshold
                || now - _pending.StartedAt >= TranscriptFlushAgeThreshold)
                FlushPending(row, segments);
        }

        private void FlushInto(AgentSession session, DateTime now, List<AgentSessionTranscriptSegmentRow> segments)
        {
            if (_pending is null)
                return;

            var row = new AgentSessionRuntimeEventRow
            {
                SessionId = _pending.SessionId,
                ProjectId = _pending.ProjectId,
                IssueNumber = _pending.IssueNumber,
                WorkflowRunId = _pending.WorkflowRunId,
                SessionName = _pending.SessionName,
                AgentSessionId = _pending.AgentSessionId ?? session.Status.AgentRuntimeSessionId,
                WorkId = _pending.WorkId ?? session.TaskId,
                WorkType = _pending.WorkType ?? session.TaskKind,
                Stage = _pending.Stage ?? session.Phase,
                CreatedAt = now,
            };
            FlushPending(row, segments);
        }

        private void FlushPending(AgentSessionRuntimeEventRow row, List<AgentSessionTranscriptSegmentRow> segments)
        {
            if (_pending is null)
                return;

            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["text"] = _pending.Text.ToString(),
                ["sourceEventType"] = _pending.SourceEventType,
                ["rawEventCount"] = _pending.RawEventCount,
                ["correlationId"] = _pending.CorrelationId,
            });

            segments.Add(CreateSegment(
                row,
                _pending.Kind,
                payload,
                _pending.CorrelationId,
                _pending.RawEventCount,
                _pending.StartedAt,
                _pending.UpdatedAt,
                _pending.UpdatedAt));
            _pending = null;
        }

        private AgentSessionTranscriptSegmentRow CreateSegment(
            AgentSessionRuntimeEventRow row,
            string kind,
            string payloadJson,
            string? correlationId,
            int rawEventCount,
            DateTime startedAt,
            DateTime updatedAt,
            DateTime? completedAt) => new()
            {
                SessionId = row.SessionId,
                ProjectId = row.ProjectId,
                IssueNumber = row.IssueNumber,
                WorkflowRunId = row.WorkflowRunId,
                SessionName = row.SessionName,
                AgentSessionId = row.AgentSessionId,
                WorkId = row.WorkId,
                WorkType = row.WorkType,
                Stage = row.Stage,
                Sequence = _nextSequence++,
                Kind = kind,
                CorrelationId = correlationId,
                PayloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
                StartedAt = startedAt,
                UpdatedAt = updatedAt,
                CompletedAt = completedAt,
                RawEventCount = rawEventCount,
            };

        private static string? ToTextSegmentKind(string eventType) => eventType switch
        {
            "agent_message_chunk" or "agent_output_chunk" or "coder_text_chunk" => "agent_message",
            "agent_thought_chunk" or "coder_thought_chunk" => "agent_thought",
            _ => null
        };

        private static string? ExtractCorrelationId(string json)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(json);
                return GetStringProp(payload, "messageId")
                    ?? GetStringProp(payload, "partId")
                    ?? GetStringProp(payload, "id")
                    ?? GetStringProp(payload, "callId")
                    ?? GetStringProp(payload, "toolCallId");
            }
            catch
            {
                return null;
            }
        }
    }

    private sealed class PendingTextSegment
    {
        public PendingTextSegment(
            string sessionId,
            string projectId,
            int issueNumber,
            string workflowRunId,
            string sessionName,
            string? agentSessionId,
            string? workId,
            string? workType,
            string? stage,
            string kind,
            string? correlationId,
            string sourceEventType,
            DateTime startedAt)
        {
            SessionId = sessionId;
            ProjectId = projectId;
            IssueNumber = issueNumber;
            WorkflowRunId = workflowRunId;
            SessionName = sessionName;
            AgentSessionId = agentSessionId;
            WorkId = workId;
            WorkType = workType;
            Stage = stage;
            Kind = kind;
            CorrelationId = correlationId;
            SourceEventType = sourceEventType;
            StartedAt = startedAt;
            UpdatedAt = startedAt;
        }

        public string SessionId { get; }
        public string ProjectId { get; }
        public int IssueNumber { get; }
        public string WorkflowRunId { get; }
        public string SessionName { get; }
        public string? AgentSessionId { get; }
        public string? WorkId { get; }
        public string? WorkType { get; }
        public string? Stage { get; }
        public string Kind { get; }
        public string? CorrelationId { get; }
        public string SourceEventType { get; private set; }
        public DateTime StartedAt { get; }
        public DateTime UpdatedAt { get; private set; }
        public int RawEventCount { get; private set; }
        public System.Text.StringBuilder Text { get; } = new();

        public void Append(string text, string sourceEventType, DateTime at)
        {
            Text.Append(text);
            SourceEventType = sourceEventType;
            UpdatedAt = at;
            RawEventCount += 1;
        }
    }
}
