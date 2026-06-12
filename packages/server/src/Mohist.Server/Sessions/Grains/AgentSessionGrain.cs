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
        "session.input",
        "message.delta",
        "reasoning.delta",
        "tool_call.started",
        "tool_call.updated",
        "tool_call.completed",
        "session.liveness",
        "usage.updated",
        "model.resolved",
        "session.closed",
    };

    private readonly IAgentSessionStore _stateStore;
    private readonly IAgentSessionTranscriptStore _transcriptStore;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventPublisher _eventPublisher;
    private readonly ITranscriptEventPublisher _transcriptPublisher;
    private readonly ILogger<AgentSessionGrain> _log;
    private readonly TranscriptAccumulator _transcript = new();
    private AgentSession? _session;
    private long _realtimeSequence;

    public AgentSessionGrain(
        IAgentSessionStore stateStore,
        IAgentSessionTranscriptStore transcriptStore,
        IDbContextFactory<MohistDbContext> dbFactory,
        IEventPublisher eventPublisher,
        ITranscriptEventPublisher transcriptPublisher,
        ILogger<AgentSessionGrain> log)
    {
        _stateStore = stateStore;
        _transcriptStore = transcriptStore;
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
        var transcript = _transcript.Flush(_session, now);
        if (transcript is null)
            return;

        await _transcriptStore.SaveAsync(transcript, ct);
    }

    public async Task<AgentSessionInfo> OpenAsync(OpenAgentSessionCommand command)
    {
        if (_session is null)
        {
            _session = CreateSession(command);
        }
        else
        {
            _ = _session.MergeMetadata(command.Metadata);
        }

        await _stateStore.SaveAsync(SessionId, _session);
        return await ToInfoAsync(_session);
    }

    private AgentSession CreateSession(OpenAgentSessionCommand command) =>
        AgentSession.Create(
            SessionId,
            command.RunnerId ?? string.Empty,
            string.IsNullOrWhiteSpace(command.AgentRuntime) ? "opencode" : command.AgentRuntime,
            command.WorkDir,
            command.Model,
            command.Metadata);

    public async Task<AgentSessionInfo> AttachPhysicalSessionAsync(AttachPhysicalSessionCommand command)
    {
        var session = await GetRequiredAsync();

        var now = DateTime.UtcNow;
        var events = session.AttachPhysicalSession(command.AgentSessionId, command.Model, command.WorkDir, command.ChangeDir, command.ProcessPid, now);
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            return await ToInfoAsync(session);
        }

        await CommitAsync(session, events);
        return await ToInfoAsync(session);
    }

    public async Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendRuntimeEventsAsync(AppendAgentSessionRuntimeEventsCommand command)
    {
        if (command.RuntimeEvents.Count == 0) return [];

        var session = await GetRequiredAsync();

        var now = DateTime.UtcNow;
        var events = new List<AgentSessionEvent>();
        events.AddRange(session.RecordActivity(now));

        var entries = new List<AgentSessionRuntimeEventEnvelope>();
        foreach (var e in command.RuntimeEvents)
        {
            events.AddRange(ApplyRuntimeEventToDomain(session, e, now));

            entries.Add(new AgentSessionRuntimeEventEnvelope
            {
                Id = -(_realtimeSequence + 1),
                SessionId = session.Id,
                AgentSessionId = session.Status.AgentRuntimeSessionId,
                Sequence = ++_realtimeSequence,
                Type = e.Type,
                PayloadJson = string.IsNullOrWhiteSpace(e.PayloadJson) ? "{}" : e.PayloadJson,
                CreatedAt = now,
            });
        }

        var transcript = _transcript.Accept(
            session,
            entries,
            now,
            forceFlushPending: command.RuntimeEvents.Count > 1);

        await _stateStore.SaveAsync(SessionId, session, events);
        if (transcript is not null)
            await _transcriptStore.SaveAsync(transcript);
        _session = session;

        await FanOutRealtimeAsync(
            session,
            entries,
            events);

        return entries.Select(e => ToEventInfo(e)).ToList();
    }

    private async Task FanOutRealtimeAsync(
        AgentSession session,
        IReadOnlyList<AgentSessionRuntimeEventEnvelope> entries,
        IReadOnlyList<AgentSessionEvent> domainEvents)
    {
        var source = AgentSessionSource(session);

        foreach (var domainEvent in domainEvents)
        {
            try
            {
                await PublishAgentSessionLifecycleAsync(domainEvent, source, session.Id);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to publish domain event for {SessionId}",
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
                AgentSessionId: row.AgentSessionId,
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

    public async Task<AgentSessionInfo?> GetAsync()
    {
        return _session is null ? null : await ToInfoAsync(_session);
    }

    private async Task<AgentSession> GetRequiredAsync()
    {
        if (_session is not null) return _session;

        _session = await _stateStore.LoadAsync(SessionId);
        return _session ?? throw new InvalidOperationException($"Agent session {SessionId} does not exist.");
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
                await PublishAgentSessionLifecycleAsync(domainEvent, source, session.Id);
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
        var start = session.Status.BoundAt ?? session.Status.CreatedAt;
        var end = DateTime.UtcNow;
        return Math.Max(0, (long)(end - start).TotalMilliseconds);
    }

    private async Task<AgentSessionInfo> ToInfoAsync(AgentSession s)
    {
        var eventSummary = await LoadEventSummaryAsync(s.Id);
        var usage = s.Status.UsageSummary ?? new AgentUsageSummary();
        return new AgentSessionInfo(
        s.Id,
        s.Runtime.RunnerId,
        s.Status.AgentRuntimeSessionId,
        StatusName(s),
        s.Settings.Model,
        s.Runtime.WorkDir,
        null,
        null,
        s.Status.CreatedAt.ToString("o"),
        s.Status.BoundAt?.ToString("o"),
        s.Status.LastDataAt?.ToString("o"),
        null,
        null,
        null,
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

    private static string StatusName(AgentSession session) =>
        session.Status.AgentRuntimeSessionId is not null
        && session.Status.LastDataAt is not null
        && DateTime.UtcNow - session.Status.LastDataAt.Value <= TimeSpan.FromMinutes(5)
            ? "active"
            : "inactive";

    private async Task<AgentSessionEventSummary> LoadEventSummaryAsync(string sessionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var turnIds = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.Id)
            .ToListAsync();
        var transcriptParts = turnIds.Count == 0
            ? new List<AgentSessionTranscriptPartRow>()
            : await db.AgentSessionTranscriptParts.AsNoTracking()
                .Where(e => turnIds.Contains(e.TurnId))
                .OrderBy(e => e.Sequence)
                .ToListAsync();
        var events = transcriptParts
            .Select(ToEventEnvelope)
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToList();

        string? resolvedModel = null;
        string? failureCategory = null;
        var toolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var failedToolCallIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            if (e.Type == "model.resolved" || e.Type == "model")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                resolvedModel = GetStringProp(payload, "resolvedModel") ?? resolvedModel;
            }
            else if (e.Type == "session.closed" || e.Type == "session_closed")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                failureCategory = GetStringProp(payload, "failureCategory") ?? failureCategory;
            }
            else if (e.Type == "tool_call.started" || e.Type == "tool_call.updated" || e.Type == "tool_call.completed" || e.Type == "tool_call" || e.Type == "tool")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
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

        return new AgentSessionEventSummary(
            resolvedModel,
            failureCategory,
            toolCallIds.Count == 0 ? null : toolCallIds.Count,
            failedToolCallIds.Count == 0 ? null : failedToolCallIds.Count);
    }

    private async Task<AgentSessionRow?> LoadProjectionAsync(string sessionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AgentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId);
    }

    private static AgentSessionRuntimeEventInfo ToEventInfo(AgentSessionRuntimeEventEnvelope e) => new(
        e.Id.ToString(),
        e.SessionId,
        e.AgentSessionId,
        e.Sequence,
        e.Type,
        e.PayloadJson,
        e.CreatedAt.ToString("o"));

    private static AgentSessionRuntimeEventEnvelope ToEventEnvelope(AgentSessionTranscriptPartRow part) => new()
    {
        Id = part.Id,
        Sequence = part.Sequence,
        Type = part.Type,
        PayloadJson = part.PayloadJson,
        CreatedAt = part.FirstSeenAt,
    };

    private static IReadOnlyList<AgentSessionEvent> ApplyRuntimeEventToDomain(
        AgentSession session,
        AgentSessionRuntimeEventInput runtimeEvent,
        DateTime now)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(runtimeEvent.PayloadJson);
        return runtimeEvent.Type switch
        {
            "session.liveness" => session.RecordActivity(now),
            "session.closed" => session.RecordActivity(now),
            "usage.updated" => session.ApplyUsage(
                GetLongProp(payload, "inputTokens"),
                GetLongProp(payload, "outputTokens"),
                GetLongProp(payload, "totalTokens"),
                GetLongProp(payload, "cachedReadTokens"),
                GetLongProp(payload, "thoughtTokens"),
                GetCostAmount(payload),
                GetCostCurrency(payload),
                GetContextWindowUsed(payload),
                GetContextWindowSize(payload)),
            "model.resolved" => session.ResolveModel(
                GetStringProp(payload, "resolvedModel") ?? GetStringProp(payload, "model"),
                now),
            _ => []
        };
    }

    private static double? GetCostAmount(JsonElement payload)
    {
        var direct = GetDoubleProp(payload, "costAmount");
        if (direct is not null) return direct;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("cost", out var costProp)
            && costProp.ValueKind == JsonValueKind.Object
            ? GetDoubleProp(costProp, "amount")
            : null;
    }

    private static string? GetCostCurrency(JsonElement payload)
    {
        var direct = GetStringProp(payload, "costCurrency");
        if (direct is not null) return direct;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("cost", out var costProp)
            && costProp.ValueKind == JsonValueKind.Object
            ? GetStringProp(costProp, "currency")
            : null;
    }

    private static long? GetContextWindowUsed(JsonElement payload)
    {
        var direct = GetLongProp(payload, "contextWindowUsed");
        if (direct is not null) return direct;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("contextWindow", out var cwProp)
            && cwProp.ValueKind == JsonValueKind.Object
            ? GetLongProp(cwProp, "used")
            : null;
    }

    private static long? GetContextWindowSize(JsonElement payload)
    {
        var direct = GetLongProp(payload, "contextWindowSize");
        if (direct is not null) return direct;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("contextWindow", out var cwProp)
            && cwProp.ValueKind == JsonValueKind.Object
            ? GetLongProp(cwProp, "size")
            : null;
    }

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

    private static string? GetToolStringProp(JsonElement payload, string name)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("toolCall", out var toolCall))
        {
            var nested = GetStringProp(toolCall, name);
            if (nested is not null) return nested;
        }

        return GetStringProp(payload, name);
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

    private sealed record AgentSessionEventSummary(
        string? ResolvedModel,
        string? FailureCategory,
        int? ToolCallCount,
        int? ToolErrorCount);

    private sealed record AgentSessionRuntimeEventEnvelope
    {
        public long Id { get; init; }
        public string SessionId { get; init; } = string.Empty;
        public string? AgentSessionId { get; init; }
        public long Sequence { get; init; }
        public string Type { get; init; } = string.Empty;
        public string PayloadJson { get; init; } = "{}";
        public DateTime CreatedAt { get; init; }
    }

    private sealed class TranscriptAccumulator
    {
        private PendingTextSegment? _pending;

        public bool HasPending => _pending is not null;

        public AgentSessionTranscriptFlush? Accept(
            AgentSession session,
            IReadOnlyList<AgentSessionRuntimeEventEnvelope> rows,
            DateTime now,
            bool forceFlushPending)
        {
            var parts = new List<AgentSessionTranscriptPartDelta>();
            foreach (var row in rows)
            {
                var textType = ToTextPartType(row.Type);
                if (textType is not null)
                {
                    AppendText(row, textType, now, parts);
                    continue;
                }

                if (!TranscriptEventTypes.Contains(row.Type))
                    continue;

                FlushInto(session, now, parts);
                var type = ToTranscriptPartType(row.Type);
                if (type == "input")
                    continue;

                parts.Add(CreatePartDelta(
                    row,
                    type,
                    CorrelationKey(type, row.PayloadJson),
                    ExtractCorrelationId(row.PayloadJson),
                    textDelta: null,
                    payloadJson: row.PayloadJson,
                    rawEventCount: 1,
                    firstSeenAt: row.CreatedAt,
                    lastSeenAt: row.CreatedAt));
            }

            if (forceFlushPending)
                FlushInto(session, now, parts);

            return ToFlush(session, rows, parts, now);
        }

        public AgentSessionTranscriptFlush? Flush(AgentSession session, DateTime now)
        {
            var parts = new List<AgentSessionTranscriptPartDelta>();
            FlushInto(session, now, parts);
            return parts.Count == 0 ? null : new AgentSessionTranscriptFlush(false, BuildTurn(session, null, now), parts);
        }

        private void AppendText(AgentSessionRuntimeEventEnvelope row, string type, DateTime now, List<AgentSessionTranscriptPartDelta> parts)
        {
            var text = ExtractText(row.PayloadJson);
            if (string.IsNullOrEmpty(text))
                return;

            var correlationId = ExtractCorrelationId(row.PayloadJson);
            if (_pending is not null
                && (!string.Equals(_pending.Type, type, StringComparison.Ordinal)
                    || !string.Equals(_pending.CorrelationId, correlationId, StringComparison.Ordinal)))
            {
                FlushPending(row, parts);
            }

            _pending ??= new PendingTextSegment(
                type,
                correlationId,
                row.Type,
                row.CreatedAt);

            _pending.Append(text, row.Type, row.CreatedAt);

            if (_pending.RawEventCount >= TranscriptFlushRawEventThreshold
                || _pending.Text.Length >= TranscriptFlushTextLengthThreshold
                || now - _pending.StartedAt >= TranscriptFlushAgeThreshold)
                FlushPending(row, parts);
        }

        private void FlushInto(AgentSession session, DateTime now, List<AgentSessionTranscriptPartDelta> parts)
        {
            if (_pending is null)
                return;

            var row = new AgentSessionRuntimeEventEnvelope
            {
                CreatedAt = now,
            };
            FlushPending(row, parts);
        }

        private void FlushPending(AgentSessionRuntimeEventEnvelope row, List<AgentSessionTranscriptPartDelta> parts)
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

            parts.Add(CreatePartDelta(
                row,
                _pending.Type,
                _pending.CorrelationId ?? _pending.Type,
                _pending.CorrelationId,
                _pending.Text.ToString(),
                payload,
                _pending.RawEventCount,
                _pending.StartedAt,
                _pending.UpdatedAt));
            _pending = null;
        }

        private static AgentSessionTranscriptPartDelta CreatePartDelta(
            AgentSessionRuntimeEventEnvelope row,
            string type,
            string correlationKey,
            string? correlationId,
            string? textDelta,
            string payloadJson,
            int rawEventCount,
            DateTime firstSeenAt,
            DateTime lastSeenAt) => new(
                type,
                correlationKey,
                correlationId,
                textDelta,
                string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson,
                firstSeenAt,
                lastSeenAt,
                rawEventCount);

        private static AgentSessionTranscriptFlush? ToFlush(
            AgentSession session,
            IReadOnlyList<AgentSessionRuntimeEventEnvelope> rows,
            IReadOnlyList<AgentSessionTranscriptPartDelta> parts,
            DateTime now)
        {
            var input = rows.FirstOrDefault(row => row.Type == "session.input");
            if (parts.Count == 0 && input is null)
                return null;
            return new AgentSessionTranscriptFlush(input is not null, BuildTurn(session, input, now), parts);
        }

        private static AgentSessionTranscriptTurnUpsert BuildTurn(
            AgentSession session,
            AgentSessionRuntimeEventEnvelope? input,
            DateTime now)
        {
            var payload = input is null ? default(JsonElement?) : ParsePayload(input.PayloadJson);
            var promptText = payload is null
                ? string.Empty
                : GetStringProp(payload.Value, "text") ?? GetStringProp(payload.Value, "prompt") ?? string.Empty;
            var promptKind = payload is null
                ? "task"
                : GetStringProp(payload.Value, "kind") ?? GetStringProp(payload.Value, "source") ?? "task";

            return new AgentSessionTranscriptTurnUpsert(
                session.Id,
                Sequence: input is null ? 0 : 1,
                promptText,
                NormalizePromptKind(promptKind),
                input?.CreatedAt ?? session.Status.CreatedAt,
                now);
        }

        private static string? ToTextPartType(string eventType) => eventType switch
        {
            "message.delta" => "text",
            "reasoning.delta" => "reasoning",
            _ => null
        };

        private static string ToTranscriptPartType(string eventType) => eventType switch
        {
            "session.input" => "input",
            "tool_call.started" or "tool_call.updated" or "tool_call.completed" => "tool",
            "session.liveness" => "status",
            "usage.updated" => "usage",
            "model.resolved" => "model",
            "session.closed" => "session_closed",
            _ => eventType
        };

        private static string CorrelationKey(string type, string json) => type switch
        {
            "tool" => ExtractCorrelationId(json) ?? "tool",
            "text" or "reasoning" => ExtractCorrelationId(json) ?? type,
            _ => type,
        };

        private static string NormalizePromptKind(string? kind) => kind switch
        {
            "initial" or "task" or "retry" or "followup" or "recovery" => kind,
            _ => "task"
        };

        private static JsonElement? ParsePayload(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<JsonElement>(json);
            }
            catch
            {
                return null;
            }
        }

        private static string? ExtractCorrelationId(string json)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(json);
                return GetStringProp(payload, "messageId")
                    ?? GetStringProp(payload, "partId")
                    ?? GetToolStringProp(payload, "toolCallId")
                    ?? GetToolStringProp(payload, "id")
                    ?? GetToolStringProp(payload, "callId");
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
            string type,
            string? correlationId,
            string sourceEventType,
            DateTime startedAt)
        {
            Type = type;
            CorrelationId = correlationId;
            SourceEventType = sourceEventType;
            StartedAt = startedAt;
            UpdatedAt = startedAt;
        }

        public string Type { get; }
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
