using System.Text.Json.Serialization;

namespace Mohist.Server.Api.DirectApi;

/// <summary>
/// The public field-value vocabulary of
/// <see cref="PublicExecutionRead"/>. These strings are the stable
/// external contract; the projection engine maps canonical execution
/// facts onto them and never exposes canonical enum names or internal
/// cause detail.
/// </summary>
public static class PublicExecutionFieldValues
{
    // status (five-state aggregate)
    public const string StatusAccepted = "accepted";
    public const string StatusQueued = "queued";
    public const string StatusRunning = "running";
    public const string StatusTerminal = "terminal";
    public const string StatusUnknown = "unknown";

    // jobStatus
    public const string JobPreparing = "preparing";
    public const string JobQueued = "queued";
    public const string JobRunning = "running";
    public const string JobTerminal = "terminal";
    public const string JobUnknown = "unknown";

    // sessionActivity
    public const string SessionIdle = "idle";
    public const string SessionActive = "active";
    public const string SessionUnknown = "unknown";

    // admission
    public const string AdmissionReady = "ready";
    public const string AdmissionBlocked = "blocked";

    // inputStatus
    public const string InputAccepted = "accepted";
    public const string InputRejected = "rejected";
    public const string InputUnknown = "unknown";

    // turnStatus
    public const string TurnQueued = "queued";
    public const string TurnRunning = "running";
    public const string TurnOutcomePending = "outcome_pending";
    public const string TurnTerminal = "terminal";
    public const string TurnUnknown = "unknown";

    // outcome
    public const string OutcomeCompleted = "completed";
    public const string OutcomeRejected = "rejected";
    public const string OutcomeFailed = "failed";
    public const string OutcomeCancelled = "cancelled";
    public const string OutcomeBlocked = "blocked";

    /// <summary>The stable public reason codes the projection emits.</summary>
    public static class Reasons
    {
        public const string QueueFull = "queue_full";
        public const string ContextReset = "context_reset";
        public const string StopOutcomeUnknown = "stop_outcome_unknown";
    }
}

/// <summary>
/// The only execution-shaped object the direct external Agent API ever
/// returns: a strict 22-key allowlist with every key always present and
/// explicit nulls. IDs and timestamps are null only where the canonical
/// fact does not exist (a follow-up has a null <c>jobId</c>; a durable
/// rejection that created no live records has null Input/Turn IDs;
/// <c>sequence</c> is null only before any Session public event could
/// exist). No internal read shape — launch, operation, session, or
/// transcript — is ever serialized into this contract, and no Runner,
/// Runtime, binding, prompt, workspace, or raw provider detail appears
/// in it.
/// </summary>
public sealed record PublicExecutionRead
{
    [JsonPropertyName("projectId")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("agentId")]
    public required string? AgentId { get; init; }

    [JsonPropertyName("jobId")]
    public required string? JobId { get; init; }

    [JsonPropertyName("sessionId")]
    public required string? SessionId { get; init; }

    [JsonPropertyName("inputId")]
    public required string? InputId { get; init; }

    [JsonPropertyName("turnId")]
    public required string? TurnId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("jobStatus")]
    public required string? JobStatus { get; init; }

    [JsonPropertyName("sessionActivity")]
    public required string? SessionActivity { get; init; }

    [JsonPropertyName("admission")]
    public required string? Admission { get; init; }

    [JsonPropertyName("inputStatus")]
    public required string? InputStatus { get; init; }

    [JsonPropertyName("turnStatus")]
    public required string? TurnStatus { get; init; }

    [JsonPropertyName("outcome")]
    public required string? Outcome { get; init; }

    [JsonPropertyName("reasonCode")]
    public required string? ReasonCode { get; init; }

    [JsonPropertyName("output")]
    public required PublicExecutionOutput? Output { get; init; }

    [JsonPropertyName("error")]
    public required PublicExecutionError? Error { get; init; }

    [JsonPropertyName("acceptedAt")]
    public required DateTimeOffset? AcceptedAt { get; init; }

    [JsonPropertyName("queuedAt")]
    public required DateTimeOffset? QueuedAt { get; init; }

    [JsonPropertyName("startedAt")]
    public required DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("terminalAt")]
    public required DateTimeOffset? TerminalAt { get; init; }

    [JsonPropertyName("observedAt")]
    public required DateTimeOffset ObservedAt { get; init; }

    [JsonPropertyName("sequence")]
    public required long? Sequence { get; init; }
}

/// <summary>
/// The public final-output shape: null, or an object whose only key is
/// <c>text</c> carrying the persisted public final output. It is never
/// a transcript, a raw provider response, or a partial result.
/// </summary>
public sealed record PublicExecutionOutput
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>
/// The safe public error shape carried inside
/// <see cref="PublicExecutionRead"/>: a stable public code and a safe
/// public message. Stack traces, provider errors, paths, and opaque
/// internal identities never appear in it.
/// </summary>
public sealed record PublicExecutionError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>
/// The smaller session-only payload of the
/// <c>session.context_reset</c> public event: exactly six keys, with no
/// Job, Input, Turn, output, error, prompt, runtime, path, raw payload,
/// or operation/binding data.
/// </summary>
public sealed record PublicSessionEventPayload
{
    [JsonPropertyName("projectId")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("agentId")]
    public required string? AgentId { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("sessionActivity")]
    public required string? SessionActivity { get; init; }

    [JsonPropertyName("admission")]
    public required string? Admission { get; init; }

    [JsonPropertyName("reasonCode")]
    public required string? ReasonCode { get; init; }
}
