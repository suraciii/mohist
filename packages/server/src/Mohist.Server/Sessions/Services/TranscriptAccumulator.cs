using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

internal sealed class TranscriptAccumulator
{
    internal const int FlushRawEventThreshold = 32;
    internal const int FlushTextLengthThreshold = 4096;
    internal static readonly TimeSpan FlushAgeThreshold = TimeSpan.FromSeconds(2);

    internal static readonly HashSet<string> EventTypes = new(StringComparer.Ordinal)
    {
        RuntimeEventTypes.SessionInput,
        RuntimeEventTypes.MessageDelta,
        RuntimeEventTypes.ReasoningDelta,
        RuntimeEventTypes.ToolCallStarted,
        RuntimeEventTypes.ToolCallUpdated,
        RuntimeEventTypes.ToolCallCompleted,
        RuntimeEventTypes.SessionLiveness,
        RuntimeEventTypes.SessionActivity,
        RuntimeEventTypes.SessionContextReset,
        RuntimeEventTypes.TurnFailed,
        RuntimeEventTypes.UsageUpdated,
        RuntimeEventTypes.ModelResolved,
        RuntimeEventTypes.Compaction,
        RuntimeEventTypes.CompactionEvent,
        RuntimeEventTypes.ContextHealthUpdate,
        RuntimeEventTypes.ProviderRetry,
    };

    internal static bool IsRetiredEventType(string? type) => type is
        "session.closed" or
        "session.followup_completed" or
        "session.followup_failed";

    internal static bool IsAcceptedEventType(string? type) =>
        !string.IsNullOrWhiteSpace(type) && !IsRetiredEventType(type);

    private PendingTextSegment? _pending;
    private readonly List<AgentSessionTranscriptPartDelta> _accumulatedParts = new();

    private string? _promptText;
    private string? _promptKind;
    private string? _runtimeSessionId;
    private DateTime? _inputCreatedAt;

    public bool HasPending => _pending is not null || _accumulatedParts.Count > 0 || _promptText is not null;

    public IReadOnlyList<AgentSessionTranscriptPartDelta> Accept(
        AgentSession session,
        IReadOnlyList<RuntimeEventEnvelope> rows,
        DateTime now)
    {
        var normalizedParts = new List<AgentSessionTranscriptPartDelta>();
        foreach (var row in rows)
        {
            CaptureRuntime(row);
            var textType = ToTextPartType(row.Type);
            if (textType is not null)
            {
                AppendText(row, textType, now, normalizedParts);
                continue;
            }

            if (!IsAcceptedEventType(row.Type))
                continue;

            FlushPendingIntoAccumulated(now, normalizedParts);
            if (row.Type == RuntimeEventTypes.SessionInput)
            {
                CaptureInput(row);
                normalizedParts.Add(new AgentSessionTranscriptPartDelta(
                    TranscriptPartTypes.Input,
                    TranscriptPartTypes.Input,
                    AgentSessionJsonHelper.ExtractCorrelationId(row.PayloadJson),
                    null,
                    string.IsNullOrWhiteSpace(row.PayloadJson) ? "{}" : row.PayloadJson,
                    row.CreatedAt,
                    row.CreatedAt,
                    1));
                continue;
            }

            if (row.Type == RuntimeEventTypes.Compaction || row.Type == RuntimeEventTypes.CompactionEvent)
                CaptureRecoveryRuntime(row);

            var type = ToTranscriptPartType(row.Type);
            var correlationId = type == TranscriptPartTypes.SessionActivity
                ? SessionActivityCorrelationId(row.PayloadJson)
                : AgentSessionJsonHelper.ExtractCorrelationId(row.PayloadJson);
            var part = CreatePartDelta(
                row,
                type,
                CorrelationKey(type, row.PayloadJson),
                correlationId,
                textDelta: null,
                payloadJson: row.PayloadJson,
                rawEventCount: 1,
                firstSeenAt: row.CreatedAt,
                lastSeenAt: row.CreatedAt);
            _accumulatedParts.Add(part);
            normalizedParts.Add(part);
        }

        return normalizedParts;
    }

    public AgentSessionTranscriptFlush? BuildFlush(AgentSession session, DateTime now)
    {
        FlushPendingIntoAccumulated(now);

        if (_accumulatedParts.Count == 0 && _promptText is null)
            return null;

        return new AgentSessionTranscriptFlush(
            _promptText is not null,
            BuildTurn(session, now),
            _accumulatedParts.ToList());
    }

    public void CommitFlush()
    {
        _pending = null;
        _accumulatedParts.Clear();
        _promptText = null;
        _promptKind = null;
        _runtimeSessionId = null;
        _inputCreatedAt = null;
    }

    private void AppendText(
        RuntimeEventEnvelope row,
        string type,
        DateTime now,
        ICollection<AgentSessionTranscriptPartDelta> normalizedParts)
    {
        var text = AgentSessionJsonHelper.ExtractText(row.PayloadJson);
        if (string.IsNullOrEmpty(text))
            return;

        var correlationId = AgentSessionJsonHelper.ExtractCorrelationId(row.PayloadJson);
        if (_pending is not null
            && (!string.Equals(_pending.Type, type, StringComparison.Ordinal)
                || !string.Equals(_pending.CorrelationId, correlationId, StringComparison.Ordinal)))
        {
            FlushPendingIntoAccumulated(row.CreatedAt, normalizedParts);
        }

        _pending ??= new PendingTextSegment(
            type,
            correlationId,
            row.Type,
            row.CreatedAt);

        _pending.Append(text, row.Type, row.CreatedAt);

        if (_pending.RawEventCount >= FlushRawEventThreshold
            || _pending.Text.Length >= FlushTextLengthThreshold
            || now - _pending.StartedAt >= FlushAgeThreshold)
            FlushPendingIntoAccumulated(row.CreatedAt, normalizedParts);
    }

    private void FlushPendingIntoAccumulated(
        DateTime lastSeenAt,
        ICollection<AgentSessionTranscriptPartDelta>? normalizedParts = null)
    {
        if (_pending is null)
            return;

        var payload = JSON.Serialize(new Dictionary<string, object?>
        {
            ["text"] = _pending.Text.ToString(),
            ["sourceEventType"] = _pending.SourceEventType,
            ["rawEventCount"] = _pending.RawEventCount,
            ["correlationId"] = _pending.CorrelationId,
        });

        var row = new RuntimeEventEnvelope
        {
            CreatedAt = lastSeenAt,
        };

        var part = CreatePartDelta(
            row,
            _pending.Type,
            _pending.CorrelationId ?? _pending.Type,
            _pending.CorrelationId,
            _pending.Text.ToString(),
            payload,
            _pending.RawEventCount,
            _pending.StartedAt,
            _pending.UpdatedAt);
        _accumulatedParts.Add(part);
        normalizedParts?.Add(part);
        _pending = null;
    }

    private void CaptureInput(RuntimeEventEnvelope row)
    {
        var payload = AgentSessionJsonHelper.ParsePayload(row.PayloadJson);
        _promptText = AgentSessionJsonHelper.GetStringProp(payload, "text")
            ?? AgentSessionJsonHelper.GetStringProp(payload, "prompt")
            ?? string.Empty;
        _promptKind = AgentSessionJsonHelper.GetStringProp(payload, "kind")
            ?? AgentSessionJsonHelper.GetStringProp(payload, "source")
            ?? "task";
        _runtimeSessionId = row.AgentSessionId;
        _inputCreatedAt = row.CreatedAt;
    }

    private void CaptureRuntime(RuntimeEventEnvelope row)
    {
        if (_runtimeSessionId is null && !string.IsNullOrWhiteSpace(row.AgentSessionId))
            _runtimeSessionId = row.AgentSessionId;
    }

    private void CaptureRecoveryRuntime(RuntimeEventEnvelope row)
    {
        if (_promptText is not null || string.IsNullOrWhiteSpace(row.AgentSessionId))
            return;

        _promptKind = "recovery";
        _runtimeSessionId = row.AgentSessionId;
        _inputCreatedAt = row.CreatedAt;
    }

    private static AgentSessionTranscriptPartDelta CreatePartDelta(
        RuntimeEventEnvelope row,
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

    private AgentSessionTranscriptTurnUpsert BuildTurn(AgentSession session, DateTime now)
    {
        var promptText = _promptText ?? string.Empty;
        var promptKind = _promptKind ?? "task";

        return new AgentSessionTranscriptTurnUpsert(
            session.Id,
            Sequence: 0,
            promptText,
            AgentSessionJsonHelper.NormalizePromptKind(promptKind),
            _inputCreatedAt ?? session.Status.CreatedAt,
            now,
            RuntimeSessionId: _runtimeSessionId);
    }

    private static string? ToTextPartType(string eventType) => eventType switch
    {
        RuntimeEventTypes.MessageDelta => TranscriptPartTypes.Text,
        RuntimeEventTypes.ReasoningDelta => TranscriptPartTypes.Reasoning,
        _ => null
    };

    private static string ToTranscriptPartType(string eventType) => eventType switch
    {
        RuntimeEventTypes.SessionInput => TranscriptPartTypes.Input,
        RuntimeEventTypes.ToolCallStarted or RuntimeEventTypes.ToolCallUpdated or RuntimeEventTypes.ToolCallCompleted => TranscriptPartTypes.Tool,
        RuntimeEventTypes.SessionLiveness => TranscriptPartTypes.Status,
        RuntimeEventTypes.SessionActivity => TranscriptPartTypes.SessionActivity,
        RuntimeEventTypes.SessionContextReset => TranscriptPartTypes.SessionContextReset,
        RuntimeEventTypes.TurnFailed => TranscriptPartTypes.Status,
        RuntimeEventTypes.ProviderRetry => TranscriptPartTypes.ProviderRetry,
        RuntimeEventTypes.UsageUpdated => TranscriptPartTypes.Usage,
        RuntimeEventTypes.ModelResolved => TranscriptPartTypes.Model,
        RuntimeEventTypes.Compaction => TranscriptPartTypes.Compaction,
        _ => eventType
    };

    private static string CorrelationKey(string type, string json) => type switch
    {
        TranscriptPartTypes.Tool => AgentSessionJsonHelper.ExtractCorrelationId(json) ?? TranscriptPartTypes.Tool,
        TranscriptPartTypes.Text or TranscriptPartTypes.Reasoning => AgentSessionJsonHelper.ExtractCorrelationId(json) ?? type,
        TranscriptPartTypes.SessionActivity => SessionActivityCorrelationId(json) ?? TranscriptPartTypes.SessionActivity,
        _ => type,
    };

    private static string? SessionActivityCorrelationId(string json)
    {
        var payload = AgentSessionJsonHelper.ParsePayload(json);
        return payload is { } value
            ? AgentSessionJsonHelper.GetStringProp(value, "deliveryId")
                ?? AgentSessionJsonHelper.GetStringProp(value, "operationId")
            : null;
    }
}

internal sealed record RuntimeEventEnvelope
{
    public long Id { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public string? AgentSessionId { get; init; }
    public long Sequence { get; init; }
    public string Type { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = "{}";
    public DateTime CreatedAt { get; init; }
}

internal sealed class PendingTextSegment
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
