using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Infrastructure.Events;

public sealed record StageChangedEvent(
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("workflowRunId")] string WorkflowRunId,
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("timestamp")] string Timestamp) : IProjectScoped;
