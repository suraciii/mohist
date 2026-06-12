using System.Text.Json;
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

    private PendingTextSegment? _pending;

    public bool HasPending => _pending is not null;

    public AgentSessionTranscriptFlush? Accept(
        AgentSession session,
        IReadOnlyList<RuntimeEventEnvelope> rows,
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

            if (!EventTypes.Contains(row.Type))
                continue;

            FlushInto(session, now, parts);
            var type = ToTranscriptPartType(row.Type);
            if (type == "input")
                continue;

            parts.Add(CreatePartDelta(
                row,
                type,
                CorrelationKey(type, row.PayloadJson),
                AgentSessionJsonHelper.ExtractCorrelationId(row.PayloadJson),
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

    private void AppendText(RuntimeEventEnvelope row, string type, DateTime now, List<AgentSessionTranscriptPartDelta> parts)
    {
        var text = AgentSessionJsonHelper.ExtractText(row.PayloadJson);
        if (string.IsNullOrEmpty(text))
            return;

        var correlationId = AgentSessionJsonHelper.ExtractCorrelationId(row.PayloadJson);
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

        if (_pending.RawEventCount >= FlushRawEventThreshold
            || _pending.Text.Length >= FlushTextLengthThreshold
            || now - _pending.StartedAt >= FlushAgeThreshold)
            FlushPending(row, parts);
    }

    private void FlushInto(AgentSession session, DateTime now, List<AgentSessionTranscriptPartDelta> parts)
    {
        if (_pending is null)
            return;

        var row = new RuntimeEventEnvelope
        {
            CreatedAt = now,
        };
        FlushPending(row, parts);
    }

    private void FlushPending(RuntimeEventEnvelope row, List<AgentSessionTranscriptPartDelta> parts)
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

    private static AgentSessionTranscriptFlush? ToFlush(
        AgentSession session,
        IReadOnlyList<RuntimeEventEnvelope> rows,
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
        RuntimeEventEnvelope? input,
        DateTime now)
    {
        var payload = input is null ? default(JsonElement?) : AgentSessionJsonHelper.ParsePayload(input.PayloadJson);
        var promptText = payload is null
            ? string.Empty
            : AgentSessionJsonHelper.GetStringProp(payload.Value, "text") ?? AgentSessionJsonHelper.GetStringProp(payload.Value, "prompt") ?? string.Empty;
        var promptKind = payload is null
            ? "task"
            : AgentSessionJsonHelper.GetStringProp(payload.Value, "kind") ?? AgentSessionJsonHelper.GetStringProp(payload.Value, "source") ?? "task";

        return new AgentSessionTranscriptTurnUpsert(
            session.Id,
            Sequence: input is null ? 0 : 1,
            promptText,
            AgentSessionJsonHelper.NormalizePromptKind(promptKind),
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
        "tool" => AgentSessionJsonHelper.ExtractCorrelationId(json) ?? "tool",
        "text" or "reasoning" => AgentSessionJsonHelper.ExtractCorrelationId(json) ?? type,
        _ => type,
    };
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
