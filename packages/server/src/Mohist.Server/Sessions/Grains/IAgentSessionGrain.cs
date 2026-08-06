using Mohist.Server.Contracts;
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

    Task<AgentSessionFollowupAcceptResult> AcceptFollowupAsync(AcceptFollowupCommand command);
    Task<AgentSessionFollowupDispatch?> BeginNextFollowupDispatchAsync();
    Task ReleaseFollowupDispatchAsync(string operationId);
    Task MarkFollowupTurnExecutingAsync(string operationId);
    Task MarkFollowupTurnTerminalAsync(
        string operationId,
        AgentTurnStatus status,
        AgentTurnResult? result);

    Task RecordFollowupTurnAsync(RecordFollowupTurnCommand command);
    Task AbandonFollowupTurnAsync(string inputId, string turnId);

    Task MarkTurnExecutingAsync(string turnId);

    Task MarkTurnTerminalAsync(string turnId, AgentTurnStatus status, AgentTurnResult? result);

    Task<AgentTurnCancelResult> CancelQueuedTurnAsync(string turnId);
    Task CancelTurnAsync(string turnId);

    Task<AgentTurnStopClaimResult> ClaimTurnStopAsync(string turnId, string? operationId = null);
    Task MarkTurnStopDispatchedAsync(string turnId, string operationId);
    Task AbandonUndispatchedTurnStopAsync(string turnId, string operationId);
    Task CompleteTurnStopAsync(string turnId, string operationId);

    Task<AgentTurnControlState?> ResolveTurnControlAsync(string turnId);
    Task<AgentTurnControlState?> ResolveCurrentTurnControlAsync();

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
    Task<EnsureParentLinkResult> EnsureParentLinkAsync(EnsureParentLinkCommand command);
    Task<ApplyParentLinkAttachResult> ApplyParentLinkAttachAsync(ApplyParentLinkAttachCommand command);
    Task<AcquireChildAttachBindingResult> AcquireChildAttachBindingAsync(AcquireChildAttachBindingCommand command);
    Task<ReleaseChildAttachBindingResult> ReleaseChildAttachBindingAsync(ReleaseChildAttachBindingCommand command);
    Task<ClaimSubagentTerminalReportResult> ClaimSubagentTerminalReportAsync(ClaimSubagentTerminalReportCommand command);
    Task<RecordSubagentTerminalReportDeliveredResult> RecordSubagentTerminalReportDeliveredAsync(RecordSubagentTerminalReportDeliveredCommand command);
    Task<ApplyParentLinkDetachResult> ApplyParentLinkDetachAsync(ApplyParentLinkDetachCommand command);
    Task PromoteProvisionalLaunchAsync();
    Task AbortProvisionalLaunchAsync(string jobId, string turnId, string reason);

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

    /// <summary>
    /// Create a scheduled input (or replay an existing schedule for the
    /// same idempotency key). Idempotent by
    /// <c>(ProjectId, SessionId, IdempotencyKey)</c>: same normalized body
    /// returns the original schedule, different body throws
    /// <see cref="ScheduleIdempotencyConflictException"/>. A due time not
    /// strictly after the injected clock throws
    /// <see cref="ScheduleDueInPastException"/>.
    /// </summary>
    Task<CreateSessionScheduleResult> CreateScheduleAsync(CreateSessionScheduleCommand command);

    /// <summary>
    /// All schedules of this session ordered by <see cref="SessionScheduleRecord.DueAt"/> ascending.
    /// </summary>
    Task<IReadOnlyList<SessionScheduleRecord>> ListSchedulesAsync();

    /// <summary>
    /// Cancel a schedule by target state: <c>scheduled</c> / <c>pending-delivery</c>
    /// advance to <c>cancelled</c>; <c>delivered</c> / <c>cancelled</c> return the
    /// current record unchanged. Unknown schedule id throws
    /// <see cref="ScheduleNotFoundException"/>.
    /// </summary>
    Task<CancelSessionScheduleResult> CancelScheduleAsync(CancelSessionScheduleCommand command);

    /// <summary>
    /// Deterministic recovery seam for scheduled-input delivery: scans this
    /// session's non-terminal schedules, delivers everything due (or still
    /// pending delivery) through the same idempotent follow-up path, and
    /// re-registers reminders. Driven by the recovery reminder; tests call it
    /// directly with a fake clock instead of waiting on real reminder timers.
    /// </summary>
    Task RunScheduledInputRecoveryAsync();

}

[GenerateSerializer]
public sealed record OpenAgentSessionCommand(
    [property: Id(0)] string RunnerId,
    [property: Id(1)] string AgentRuntime,
    [property: Id(2)] string? WorkDir = null,
    [property: Id(3)] string? Model = null,
    [property: Id(4)] AgentSessionMetadata? Metadata = null,
    [property: Id(5)] AgentExecutionDefinition? Definition = null,
    [property: Id(6)] AgentSessionStartup? AgentSessionStartup = null,
    [property: Id(7)] AgentLaunchVisibility LaunchVisibility = AgentLaunchVisibility.Visible);

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
    [property: Id(2)] string ReplacementRuntime = "opencode",
    [property: Id(3)] long? ExpectedBindingEpoch = null);

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
public sealed record AcceptFollowupCommand(
    [property: Id(0)] string Text,
    [property: Id(1)] string Source,
    [property: Id(2)] string IdempotencyKey,
    /// <summary>
    /// Accepted attachments the route already validated and bound to
    /// the input id the coordinator mints. Persisted on the input
    /// record so the accepted set is authoritative across restart
    /// and the dispatch payload can carry the descriptors.
    /// Append-only Orleans field id (next free after
    /// <see cref="IdempotencyKey"/>).
    /// </summary>
    [property: Id(3)] IReadOnlyList<AgentSessionInputAttachmentDescriptor>? Attachments = null,
    /// <summary>
    /// Optional pre-minted input id the route wants the Session grain
    /// to adopt verbatim. Required when attachments are supplied so
    /// binding keys on the same id the Session record will carry.
    /// Append-only Orleans field id (next free after
    /// <see cref="Attachments"/>).
    /// </summary>
    [property: Id(4)] string? PreMintedInputId = null,
    /// <summary>
    /// Optional pre-minted turn id mirroring
    /// <see cref="PreMintedInputId"/>. Append-only Orleans field id
    /// (next free after <see cref="PreMintedInputId"/>).
    /// </summary>
    [property: Id(5)] string? PreMintedTurnId = null,
    /// <summary>
    /// Per-attachment verdicts the route validated at acceptance.
    /// Echoed through the accept result so the API layer can render
    /// rejected files alongside the accepted set; the grain only
    /// persists the descriptors it stored, the verdict set is
    /// response-only metadata. Append-only Orleans field id (next
    /// free after <see cref="PreMintedTurnId"/>).
    /// </summary>
    [property: Id(6)] IReadOnlyList<AgentInputAttachmentAcceptance>? AttachmentResults = null,
    [property: Id(7)] AgentSessionInputProvenance? Provenance = null);

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
    [property: Id(23)] long? CachedWriteTokens,
    [property: Id(24)] long BindingEpoch = 0,
    /// <summary>
    /// Durable Project Repository workspace source, if any. <c>null</c>
    /// for sessions without a Project-backed source; otherwise carries
    /// the immutable snapshot and the <c>unconfirmed/confirmed/rejected</c>
    /// state advanced by the Runner first-execution report. Append-only
    /// Orleans field id.
    /// </summary>
    [property: Id(25)] WorkspaceRepository? WorkspaceRepository = null);

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
    [property: Id(7)] string? WorkDir = null,
    /// <summary>
    /// Accepted attachments the launch path already validated and
    /// bound to <see cref="InputId"/>. Persisted on the input
    /// record so the accepted set is authoritative across restart
    /// and the dispatch payload can carry the descriptors.
    /// Append-only Orleans field id (next free after WorkDir).
    /// </summary>
    [property: Id(8)] IReadOnlyList<AgentSessionInputAttachmentDescriptor>? Attachments = null,
    [property: Id(9)] AgentSessionInputProvenance? Provenance = null,
    /// <summary>
    /// Optional bounded external discussion the caller attaches as
    /// first-launch-only background. Persisted verbatim on the
    /// input record so the audit observation is inspectable and
    /// so a recovery replay observes the same snapshot. The
    /// background is composed into the dispatched agent input at
    /// <c>BuildDispatch</c> time; <see cref="Prompt"/> and the
    /// SessionInput <c>Text</c> stay task-only. Null when no
    /// startup context was supplied — the absence is
    /// observationally identical to before this capability existed.
    /// Append-only Orleans field id (next free after
    /// <see cref="Provenance"/>).
    /// </summary>
    [property: Id(10)] AgentStartupContext? StartupContext = null,
    [property: Id(11)] AgentExecutionDefinition? Definition = null,
    [property: Id(12)] AgentSessionStartup? AgentSessionStartup = null,
    [property: Id(13)] AgentLaunchVisibility LaunchVisibility = AgentLaunchVisibility.Visible,
    /// <summary>
    /// Confirmed Project Repository source for a managed-worktree child.
    /// Set by the spawn coordinator after materialization succeeds so the
    /// child AgentSession owns a confirmed source without a Runner
    /// first-execution check (the worktree is materialized from a confirmed
    /// parent). Absent for inherit spawns and non-spawn launches.
    /// </summary>
    [property: Id(14)] WorkspaceRepositorySnapshot? ConfirmedWorkspaceRepository = null);

[GenerateSerializer]
public sealed record EnsureInitialLaunchResult(
    [property: Id(0)] string SessionId,
    [property: Id(1)] string InputId,
    [property: Id(2)] string TurnId,
    [property: Id(3)] bool AlreadyPersisted);

[GenerateSerializer]
public sealed record EnsureParentLinkCommand(
    [property: Id(0)] SessionParentLink Link,
    [property: Id(1)] string? ExpectedWorkDir,
    [property: Id(2)] string? ExpectedRunnerId,
    [property: Id(3)] string? ExpectedRuntime,
    [property: Id(4)] string? ExpectedRuntimeSessionId);

[GenerateSerializer]
public sealed record EnsureParentLinkResult(
    [property: Id(0)] string SessionId,
    [property: Id(1)] string EdgeId,
    [property: Id(2)] bool AlreadyPersisted);

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
    [property: Id(3)] string Source,
    /// <summary>
    /// Accepted attachments the follow-up path already validated and
    /// bound to <see cref="InputId"/>. Persisted on the input record
    /// so the accepted set is authoritative across restart and the
    /// dispatch payload can carry the descriptors. Append-only
    /// Orleans field id (next free after Source).
    /// </summary>
    [property: Id(4)] IReadOnlyList<AgentSessionInputAttachmentDescriptor>? Attachments = null,
    [property: Id(5)] AgentSessionInputProvenance? Provenance = null);

/// <summary>
/// Command body for <see cref="IAgentSessionGrain.CreateScheduleAsync"/>.
/// <see cref="DueAt"/> must be an offset RFC 3339 instant strictly after
/// the injected clock; the grain stores its UTC instant. An omitted
/// <see cref="IdempotencyKey"/> mints a fresh key (no cross-request
/// dedup); an explicit key makes creation replayable per the contract.
/// </summary>
[GenerateSerializer]
public sealed record CreateSessionScheduleCommand(
    [property: Id(0)] string Text,
    [property: Id(1)] DateTimeOffset DueAt,
    [property: Id(2)] string? IdempotencyKey = null);

[GenerateSerializer]
public sealed record CreateSessionScheduleResult(
    [property: Id(0)] SessionScheduleRecord Schedule,
    [property: Id(1)] bool AlreadyExists);

[GenerateSerializer]
public sealed record CancelSessionScheduleCommand(
    [property: Id(0)] string ScheduleId);

[GenerateSerializer]
public sealed record CancelSessionScheduleResult(
    [property: Id(0)] SessionScheduleRecord Schedule,
    [property: Id(1)] bool AlreadyTerminal);
