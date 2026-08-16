namespace Mohist.Server.Api.DirectApi;

/// <summary>
/// The complete public Session event vocabulary: the seven execution
/// event types plus <c>session.context_reset</c>. Every execution type
/// carries an <c>execution</c> payload that is exactly
/// <see cref="PublicExecutionRead"/>; <c>session.context_reset</c>
/// carries only the six-key session payload
/// (<see cref="PublicSessionEventPayload"/>). No other public event
/// type exists, and no raw canonical event data is ever exposed.
/// </summary>
public static class PublicSessionEventTypes
{
    public const string InputAccepted = "input.accepted";
    public const string InputRejected = "input.rejected";
    public const string TurnQueued = "turn.queued";
    public const string TurnRunning = "turn.running";
    public const string TurnOutcomePending = "turn.outcome_pending";
    public const string TurnTerminal = "turn.terminal";
    public const string SessionUnknown = "session.unknown";
    public const string ContextReset = "session.context_reset";

    /// <summary>The execution event types that carry PublicExecutionRead.</summary>
    public static readonly IReadOnlyList<string> Execution =
    [
        InputAccepted,
        InputRejected,
        TurnQueued,
        TurnRunning,
        TurnOutcomePending,
        TurnTerminal,
        SessionUnknown,
    ];

    /// <summary>Every event type the public stream may contain, in stable order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        InputAccepted,
        InputRejected,
        TurnQueued,
        TurnRunning,
        TurnOutcomePending,
        TurnTerminal,
        SessionUnknown,
        ContextReset,
    ];
}
