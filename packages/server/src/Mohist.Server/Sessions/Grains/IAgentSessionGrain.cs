using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Sessions.Grains;

public interface IAgentSessionGrain : IGrainWithStringKey
{
    Task<AgentSessionInfo> OpenAsync(OpenAgentSessionCommand command);
    Task<AgentSessionInfo> AttachPhysicalSessionAsync(AttachPhysicalSessionCommand command);
    Task<AgentSessionInfo> RecoverMissingRuntimeSessionAsync(RecoverMissingRuntimeSessionCommand command);
    Task<AgentSessionInfo> ReconcileMissingBindingAsync(ReconcileMissingBindingCommand command);
    Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendRuntimeEventsAsync(AppendAgentSessionRuntimeEventsCommand command);
    Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendSystemEventsAsync(AppendAgentSessionSystemEventsCommand command);

    /// <summary>
    /// Synchronous, idempotent terminal-close command issued by an
    /// authoritative AgentJob owner. The
    /// delivery id stored in <see cref="AppendTerminalCloseCommand.DeliveryId"/>
    /// is the correlation key on the persisted terminal <c>session.activity</c>
    /// transcript part; a second call with the same delivery id is a
    /// no-op (the AgentSession already owns the close fact). The method
    /// returns only after the state/events and the transcript part are
    /// durably flushed so the AgentJob can clear its pending delivery
    /// without risking a lost terminal fact. Throws on persistence
    /// failure so the AgentJob reminder / report replay retries the
    /// original delivery.
    /// </summary>
    Task<AppendTerminalCloseResult> AppendTerminalCloseAsync(AppendTerminalCloseCommand command);
    Task<AgentSessionRecoveryResult> CompactAsync(CompactAgentSessionCommand command);
    Task<AgentSessionRecoveryResult> ResetAsync(ResetAgentSessionCommand command);
    Task<AgentSessionRecoveryResult?> GetCompletedRecoveryAsync(SessionCommandKind command, string? idempotencyKey = null);
    Task<SessionCommandRequest> PrepareSessionCommandAsync(SessionCommandKind command, string? idempotencyKey = null);
    Task<SessionCommandRequest> BeginResetAsync(string? idempotencyKey = null);
    Task<AgentSessionRecoveryResult> CompleteCompactAsync(CompleteCompactAgentSessionCommand command);
    Task<AgentSessionRecoveryResult> CompleteResetAsync(CompleteResetAgentSessionCommand command);
    Task AbandonResetAsync(string operationId);
    Task<AgentSessionFollowupReservation> BeginFollowupAsync();
    Task ConfirmFollowupAsync(string operationId);
    Task AbandonFollowupAsync(string operationId);

    Task RecordFollowupTurnAsync(RecordFollowupTurnCommand command);
    Task AbandonFollowupTurnAsync(string inputId, string turnId);

    Task MarkTurnExecutingAsync(string turnId);

    Task MarkTurnTerminalAsync(string turnId, AgentTurnStatus status, AgentTurnResult? result);

    Task<AgentTurnCancelResult> CancelQueuedTurnAsync(string turnId);
    Task CancelTurnAsync(string turnId);

    Task<AgentTurnStopClaimResult> ClaimTurnStopAsync(string turnId);
    Task MarkTurnStopDispatchedAsync(string turnId, string operationId);
    Task AbandonUndispatchedTurnStopAsync(string turnId, string operationId);
    Task CompleteTurnStopAsync(string turnId, string operationId);

    Task<AgentTurnControlState?> ResolveTurnControlAsync(string turnId);

    Task<AgentSessionInfo?> GetAsync();
    Task EnsureRuntimeSessionPresentAsync();
    Task RunnerDisconnectedAsync();

    /// <summary>
    /// Idempotently record the initial input and turn for a launch. The
    /// session is opened from the supplied
    /// metadata when absent; the first <see cref="AgentSessionInputRecord"/>
    /// is recorded as accepted and the first <see cref="AgentTurnRecord"/>
    /// as queued, both linked to the AgentJob id. Re-issuing with the
    /// same ids is a no-op; mismatched content or pre-existing immutable
    /// source metadata raises a conflict. Returns the persisted
    /// session, input, and turn ids so the caller can correlate Job
    /// dispatch with the durable session artifacts.
    /// </summary>
    Task<EnsureInitialLaunchResult> EnsureInitialLaunchAsync(EnsureInitialLaunchCommand command);

    /// <summary>
    /// Mark the initial turn for the given job id as executing.
    /// No-op when the turn is missing or already past queued state.
    /// </summary>
    Task MarkInitialTurnExecutingAsync(string jobId);

    /// <summary>
    /// Mark the initial turn for the given job id as terminal.
    /// </summary>
    Task MarkInitialTurnTerminalAsync(string jobId, AgentTurnStatus status, AgentTurnResult? result);

    /// <summary>
    /// Read the initial input and turn records for the session. Returns
    /// <c>null</c> when the session has not yet been launched. The
    /// composite observation read projects this
    /// shape into the canonical Job+Session+Input+Turn snapshot.
    /// </summary>
    Task<AgentInitialLaunchSnapshot?> GetInitialLaunchAsync();

    Task<IReadOnlyList<AgentTurnRecord>> ListTurnsAsync();

}

[GenerateSerializer]
public sealed record OpenAgentSessionCommand(
    [property: Id(0)] string RunnerId,
    [property: Id(1)] string AgentRuntime,
    [property: Id(2)] string? WorkDir = null,
    [property: Id(3)] string? Model = null,
    [property: Id(4)] AgentSessionMetadata? Metadata = null,
    [property: Id(5)] AgentExecutionDefinition? Definition = null);

[GenerateSerializer]
public sealed record AttachPhysicalSessionCommand(
    [property: Id(0)] string AgentSessionId,
    [property: Id(1)] string? Model = null,
    [property: Id(2)] string? WorkDir = null,
    [property: Id(3)] string? ChangeDir = null,
    [property: Id(4)] int? ProcessPid = null,
    [property: Id(5)] string? Runtime = null,
    [property: Id(6)] string? ExpectedRuntime = null,
    [property: Id(7)] string? ExpectedAgentSessionId = null,
    [property: Id(8)] string? ExpectedRunnerId = null);

[GenerateSerializer]
public sealed record RecoverMissingRuntimeSessionCommand(
    [property: Id(0)] string ExpectedRunnerId,
    [property: Id(1)] string ExpectedRuntime,
    [property: Id(2)] string ExpectedRuntimeSessionId,
    [property: Id(3)] string ReplacementRuntimeSessionId);

[GenerateSerializer]
public sealed record ReconcileMissingBindingCommand(
    [property: Id(0)] string ExpectedRunnerId,
    [property: Id(1)] string ExpectedRuntime,
    [property: Id(2)] string ExpectedRuntimeSessionId,
    [property: Id(3)] string ReplacementRuntimeSessionId);

[GenerateSerializer]
public sealed record AppendAgentSessionRuntimeEventsCommand(
    [property: Id(0)] IReadOnlyList<AgentSessionRuntimeEventInput> RuntimeEvents = null!,
    [property: Id(1)] string RuntimeSessionId = "");

[GenerateSerializer]
public sealed record AppendAgentSessionSystemEventsCommand(
    [property: Id(0)] IReadOnlyList<AgentSessionRuntimeEventInput> RuntimeEvents = null!);

/// <summary>
/// Idempotent AgentSession close command.
/// The AgentJob owns the canonical terminal delivery: every retry across
/// reminder ticks, activation loss, and report replay reuses the same
/// <see cref="DeliveryId"/> so the AgentSession persists at most one
/// terminal <c>session.activity</c> transcript fact per AgentJob-owned close.
/// <see cref="RecordedAt"/> is the AgentJob's terminal timestamp; the
/// AgentSession projects it onto the persisted terminal payload so all
/// observable terminal metadata is identical regardless of which retry
/// observed the durable acknowledgement. <see cref="RuntimeSessionId"/>
/// is the AgentJob's bound runtime at the time the terminal was decided;
/// when the bound runtime has been superseded by a reset the AgentSession
/// drops the close and acknowledges so the AgentJob can clear its
/// pending delivery without retrying forever.
/// </summary>
[GenerateSerializer]
public sealed record AppendTerminalCloseCommand(
    [property: Id(0)] string SessionId,
    [property: Id(1)] string DeliveryId,
    [property: Id(2)] string Status,
    [property: Id(3)] int? ExitCode,
    [property: Id(4)] string? FailureReason,
    [property: Id(5)] string? FailureCategory,
    [property: Id(6)] DateTimeOffset RecordedAt,
    [property: Id(7)] string PayloadJson,
    [property: Id(8)] string? RuntimeSessionId = null);

[GenerateSerializer]
public sealed record AppendTerminalCloseResult(
    [property: Id(0)] string SessionId,
    [property: Id(1)] string DeliveryId,
    [property: Id(2)] bool AlreadyPersisted);

[GenerateSerializer]
public sealed record CompactAgentSessionCommand(
    [property: Id(0)] string? Summary = null,
    [property: Id(1)] int? MaxSummaryChars = null);

[GenerateSerializer]
public sealed record ResetAgentSessionCommand(
    [property: Id(0)] string? ExpectedRuntimeSessionId,
    [property: Id(1)] string ReplacementRuntimeSessionId,
    [property: Id(2)] string ReplacementRuntime = "opencode");

[GenerateSerializer]
public sealed record CompleteResetAgentSessionCommand(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string ReplacementRuntimeSessionId,
    [property: Id(2)] string ReplacementRuntime);

[GenerateSerializer]
public sealed record CompleteCompactAgentSessionCommand(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string? Summary = null,
    [property: Id(2)] int? MaxSummaryChars = null);

[GenerateSerializer]
public sealed record AgentSessionFollowupReservation(
    [property: Id(0)] string? OperationId,
    [property: Id(1)] bool StartsIdleTurn = false,
    [property: Id(2)] bool ConcurrencyPermitHeld = false);

[GenerateSerializer]
public sealed record AgentSessionRuntimeEventInput(
    [property: Id(0)] string Type,
    [property: Id(1)] string PayloadJson);

[GenerateSerializer]
public sealed record AgentSessionInfo(
    [property: Id(0)] string Id,
    [property: Id(1)] string? RunnerId,
    [property: Id(2)] string? AgentSessionId,
    [property: Id(3)] string Status,
    [property: Id(4)] string? Model,
    [property: Id(5)] string? WorkDir,
    [property: Id(6)] string CreatedAt,
    [property: Id(7)] string? StartedAt,
    [property: Id(8)] string? LastDataAt,
    [property: Id(9)] string? ResolvedModel,
    [property: Id(10)] long? InputTokens,
    [property: Id(11)] long? OutputTokens,
    [property: Id(12)] long? TotalTokens,
    [property: Id(13)] long? CachedReadTokens,
    [property: Id(14)] long? ThoughtTokens,
    [property: Id(15)] double? CostAmount,
    [property: Id(16)] string? CostCurrency,
    [property: Id(17)] long? ContextWindowUsed,
    [property: Id(18)] long? ContextWindowSize,
    [property: Id(19)] string? FailureCategory,
    [property: Id(20)] int? ToolCallCount,
    [property: Id(21)] int? ToolErrorCount,
    [property: Id(22)] string? Runtime,
    [property: Id(23)] long? CachedWriteTokens);

[GenerateSerializer]
public sealed record AgentSessionRecoveryResult(
    [property: Id(0)] string Id,
    [property: Id(2)] string Status,
    [property: Id(3)] long? ContextWindowSize,
    [property: Id(4)] long? ContextWindowUsed,
    [property: Id(5)] double? ContextUsagePercent,
    [property: Id(6)] long? ContextWindowUsedBefore,
    [property: Id(7)] string? Operation,
    [property: Id(8)] bool WasCompacted);

[GenerateSerializer]
public sealed record AgentSessionRuntimeEventInfo(
    [property: Id(0)] string Id,
    [property: Id(1)] string SessionId,
    [property: Id(2)] string? RuntimeSessionId,
    [property: Id(3)] long Sequence,
    [property: Id(4)] string Type,
    [property: Id(5)] string PayloadJson,
    [property: Id(6)] string CreatedAt);

/// <summary>
/// Command issued by the coordinator to durably record the initial
/// input and turn on a freshly opened AgentSession. The grain owns
/// no business aggregate for Input or Turn — both are addressable
/// children of the Session aggregate — but the Session must persist
/// the initial children before the AgentJob dispatches so the
/// launch identity is durable at acceptance time. The supplied
/// metadata is used to open the Session if it has not yet been
/// opened by an earlier launch step.
/// </summary>
[GenerateSerializer]
public sealed record EnsureInitialLaunchCommand(
    [property: Id(0)] string InputId,
    [property: Id(1)] string TurnId,
    [property: Id(2)] string Prompt,
    [property: Id(3)] string Source,
    [property: Id(4)] string JobId,
    [property: Id(5)] AgentSessionMetadata? Metadata = null,
    [property: Id(6)] string? Runtime = null,
    [property: Id(7)] string? WorkDir = null);

[GenerateSerializer]
public sealed record EnsureInitialLaunchResult(
    [property: Id(0)] string SessionId,
    [property: Id(1)] string InputId,
    [property: Id(2)] string TurnId,
    [property: Id(3)] bool AlreadyPersisted);

[GenerateSerializer]
public sealed record AgentInitialLaunchSnapshot(
    [property: Id(0)] string SessionId,
    [property: Id(1)] AgentSessionInputRecord? Input,
    [property: Id(2)] AgentTurnRecord? Turn);

/// <summary>
/// Command body for <see cref="IAgentSessionGrain.RecordFollowupTurnAsync"/>.
/// The follow-up route mints a stable <see cref="InputId"/> and
/// <see cref="TurnId"/> and passes them to the Session grain so the
/// durable Turn identity is committed ahead of any Runner dispatch.
/// <see cref="Source"/> identifies the follow-up origin (e.g.
/// <c>generic-followup</c>, <c>workflow-followup</c>).
/// </summary>
[GenerateSerializer]
public sealed record RecordFollowupTurnCommand(
    [property: Id(0)] string InputId,
    [property: Id(1)] string TurnId,
    [property: Id(2)] string Prompt,
    [property: Id(3)] string Source);
