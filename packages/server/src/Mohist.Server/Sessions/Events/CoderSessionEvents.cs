using System.Text.Json.Serialization;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Sessions.Events;

public sealed record CoderSessionStartedEvent(
    [property: JsonPropertyName("issueId")] string IssueId,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("coderSessionId")] string CoderSessionId,
    [property: JsonPropertyName("acpSessionId")] string AcpSessionId,
    [property: JsonPropertyName("executionId")] string? ExecutionId,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("taskDescription")] string? TaskDescription,
    [property: JsonPropertyName("title")] string? Title) : IProjectScoped;

public sealed record CoderTranscriptEntryEvent(
    [property: JsonPropertyName("issueId")] string IssueId,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("executionId")] string? ExecutionId,
    [property: JsonPropertyName("acpSessionId")] string AcpSessionId,
    [property: JsonPropertyName("coderSessionId")] string CoderSessionId,
    [property: JsonPropertyName("text")] string? Text) : IProjectScoped;

public sealed record CoderSessionStatusChangedEvent(
    [property: JsonPropertyName("issueId")] string IssueId,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("coderSessionId")] string CoderSessionId,
    [property: JsonPropertyName("acpSessionId")] string AcpSessionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("lastDataAt")] string? LastDataAt,
    [property: JsonPropertyName("failureReason")] string? FailureReason) : IProjectScoped;

public sealed record CoderSessionTerminalEvent(
    [property: JsonPropertyName("issueId")] string IssueId,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("coderSessionId")] string CoderSessionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("duration")] long Duration) : IProjectScoped;
