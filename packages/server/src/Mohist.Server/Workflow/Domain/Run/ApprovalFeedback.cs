using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Workflow.Domain.Run;

[JsonConverter(typeof(ApprovalFeedbackStatusJsonConverter))]
public enum ApprovalFeedbackStatus { Open, Resolved }

/// <summary>
/// JSON converter that emits the enum as lowercase strings (e.g. "open",
/// "resolved") to match the agent-readable feedback JSON contract
/// (<see cref="Mohist.Server.Workflow.Grains.WorkflowFeedbackRecord"/>).
/// </summary>
internal sealed class ApprovalFeedbackStatusJsonConverter : JsonConverter<ApprovalFeedbackStatus>
{
    public override ApprovalFeedbackStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected string for {typeToConvert.Name}, got {reader.TokenType}");
        }
        var raw = reader.GetString();
        return raw switch
        {
            null => throw new JsonException("Expected non-null string for ApprovalFeedbackStatus"),
            "open" => ApprovalFeedbackStatus.Open,
            "resolved" => ApprovalFeedbackStatus.Resolved,
            // Accept the legacy PascalCase form for back-compat with
            // payloads persisted before the lowercase casing was adopted.
            "Open" => ApprovalFeedbackStatus.Open,
            "Resolved" => ApprovalFeedbackStatus.Resolved,
            _ => throw new JsonException($"Unknown ApprovalFeedbackStatus value '{raw}'"),
        };
    }

    public override void Write(Utf8JsonWriter writer, ApprovalFeedbackStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            ApprovalFeedbackStatus.Open => "open",
            ApprovalFeedbackStatus.Resolved => "resolved",
            _ => throw new JsonException($"Unknown ApprovalFeedbackStatus value '{value}'"),
        });
    }
}

public sealed record ApprovalFeedback(
    string Id,
    string WorkflowRunId,
    string Stage,
    string Body,
    ApprovalFeedbackStatus Status,
    DateTimeOffset CreatedAt,
    string? ResolutionTaskId = null,
    DateTimeOffset? ResolvedAt = null,
    string? ResolutionSummary = null);
