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
    private readonly List<AgentSessionTranscriptPartDelta> _accumulatedParts = new();

    private string? _promptText;
    private string? _promptKind;
    private DateTime? _inputCreatedAt;

    public bool HasPending => _pending is not null || _accumulatedParts.Count > 0 || _promptText is not null;

    public void Accept(AgentSession session, IReadOnlyList<RuntimeEventEnvelope> rows, DateTime now)
    {
        foreach (var row in rows)
        {
            var textType = ToTextPartType(row.Type);
            if (textType is not null)
            {
                AppendText(row, textType, now);
                continue;
            }

            if (!EventTypes.Contains(row.Type))
                continue;

            FlushPendingIntoAccumulated(now);
            if (row.Type == "session.input")
            {
                CaptureInput(row);
                continue;
            }

            var type = ToTranscriptPartType(row.Type);
            _accumulatedParts.Add(CreatePartDelta(
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
        _inputCreatedAt = null;
    }

    private void AppendText(RuntimeEventEnvelope row, string type, DateTime now)
    {
        var text = AgentSessionJsonHelper.ExtractText(row.PayloadJson);
        if (string.IsNullOrEmpty(text))
            return;

        var correlationId = AgentSessionJsonHelper.ExtractCorrelationId(row.PayloadJson);
        if (_pending is not null
            && (!string.Equals(_pending.Type, type, StringComparison.Ordinal)
                || !string.Equals(_pending.CorrelationId, correlationId, StringComparison.Ordinal)))
        {
            FlushPendingIntoAccumulated(row.CreatedAt);
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
            FlushPendingIntoAccumulated(row.CreatedAt);
    }

    private void FlushPendingIntoAccumulated(DateTime lastSeenAt)
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

        var row = new RuntimeEventEnvelope
        {
            CreatedAt = lastSeenAt,
        };

        _accumulatedParts.Add(CreatePartDelta(
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

    private void CaptureInput(RuntimeEventEnvelope row)
    {
        var payload = AgentSessionJsonHelper.ParsePayload(row.PayloadJson);
        _promptText = AgentSessionJsonHelper.GetStringProp(payload, "text")
            ?? AgentSessionJsonHelper.GetStringProp(payload, "prompt")
            ?? string.Empty;
        _promptKind = AgentSessionJsonHelper.GetStringProp(payload, "kind")
            ?? AgentSessionJsonHelper.GetStringProp(payload, "source")
            ?? "task";
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
            Sequence: _inputCreatedAt.HasValue ? 1 : 0,
            promptText,
            AgentSessionJsonHelper.NormalizePromptKind(promptKind),
            _inputCreatedAt ?? session.Status.CreatedAt,
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
