using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Api.DirectApi;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Infrastructure.PublicApi;

/// <summary>
/// The canonical feed names the public projection checkpoints track.
/// Each feed has its own watermark shape: the AgentJob ledger advances
/// by ledger revision, the journal feeds by per-source sequence, and
/// the AgentSession ledger by consumed state digest.
/// </summary>
public static class PublicProjectionFeeds
{
    /// <summary>AgentJob ledger rows (<c>AgentJobs</c>), keyed by job key.</summary>
    public const string AgentJobs = "agent_jobs";

    /// <summary>AgentJob CloudEvents journal (<c>AgentJobEvents</c>), keyed by source.</summary>
    public const string AgentJobEvents = "agent_job_events";

    /// <summary>AgentSession ledger rows (<c>AgentSessions</c>), keyed by session id.</summary>
    public const string AgentSessions = "agent_sessions";

    /// <summary>AgentSession CloudEvents journal (<c>AgentSessionEvents</c>), keyed by source.</summary>
    public const string AgentSessionEvents = "agent_session_events";
}

/// <summary>
/// The normalized canonical facts one projection batch consumed for a
/// single public target — a Session (with its Jobs, Inputs, and Turns)
/// or a prepared launch Job that has no live Session projection yet.
/// Facts are copied out of the durable rows inside the projection
/// transaction, so everything derived from this record is "consumed
/// facts": the five-state aggregate is never computed from projection
/// backlog the batch has not yet read.
/// </summary>
internal sealed class PublicProjectionFacts
{
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Public Project identity; null when no public identity exists.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Canonical Agent identity; null when unknown.</summary>
    public string? AgentId { get; init; }

    public AgentSessionActivity? Activity { get; init; }

    public DateTimeOffset? SessionCreatedAt { get; init; }

    /// <summary>An unresolved stop claim is held against the Session.</summary>
    public bool PendingStopActive { get; init; }

    /// <summary>A Session reset reservation is in progress.</summary>
    public bool PendingResetActive { get; init; }

    public IReadOnlyList<JobFacts> Jobs { get; init; } = [];

    public IReadOnlyList<InputFacts> Inputs { get; init; } = [];

    public IReadOnlyList<TurnFacts> Turns { get; init; } = [];

    /// <summary>
    /// The consumed per-source journal rows of the AgentSession event
    /// journal, in ascending per-source order. Used to derive durable
    /// context-reset facts.
    /// </summary>
    public IReadOnlyList<SessionJournalFacts> SessionJournal { get; init; } = [];

    /// <summary>The durable AgentSession ledger facts of one Job.</summary>
    internal sealed record JobFacts(
        string JobKey,
        AgentJobStatus Status,
        string? ProjectId,
        string? AgentId,
        string? SessionId,
        string? InitialInputId,
        string? InitialTurnId,
        DateTimeOffset? SubmittedAt,
        DateTimeOffset? ReadySince,
        DateTimeOffset? RunningSince,
        DateTimeOffset? TerminalAt,
        string? WaitingReason,
        AgentJobTerminalResult? TerminalResult);

    /// <summary>The durable facts of one recorded Session input.</summary>
    internal sealed record InputFacts(
        string InputId,
        AgentSessionInputAcceptance Acceptance,
        DateTimeOffset? RecordedAt,
        string? JobId);

    /// <summary>The durable facts of one recorded Session turn.</summary>
    internal sealed record TurnFacts(
        string TurnId,
        AgentTurnStatus Status,
        IReadOnlyList<string> InputIds,
        string? JobId,
        DateTimeOffset? RecordedAt,
        DateTimeOffset? UpdatedAt,
        AgentTurnResult? Result);

    /// <summary>
    /// One consumed AgentSession journal row relevant to the public
    /// projection (a durable runtime-session binding fact).
    /// </summary>
    internal sealed record SessionJournalFacts(
        long JournalId,
        string Type,
        DateTimeOffset Time);
}

/// <summary>
/// The public observation components for one anchor, before the
/// five-state aggregate is applied. Component values use the public
/// field vocabulary exclusively.
/// </summary>
internal sealed record PublicAnchorComponents(
    string? JobId,
    string? SessionId,
    string? InputId,
    string? TurnId,
    string? JobStatus,
    string? SessionActivity,
    string? Admission,
    string? InputStatus,
    string? TurnStatus,
    string? Outcome,
    string? ReasonCode,
    PublicExecutionOutput? Output,
    PublicExecutionError? Error,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? TerminalAt,
    string? TerminalFact)
{
    /// <summary>
    /// The internal identity of the winning terminal fact
    /// (<see cref="TerminalFact"/>) mapped to the fence storage on the
    /// snapshot row, plus the fenced public outcome.
    /// </summary>
    public bool IsTerminal => TerminalFact is not null;
}

/// <summary>
/// Pure mapping from consumed canonical facts onto the public
/// execution observation vocabulary: the component facts, the
/// five-state aggregate with its fixed precedence, and the normalized
/// source transitions that drive the public Session event journal.
/// The class holds no state and performs no I/O; the projection engine
/// feeds it <see cref="PublicProjectionFacts"/> copied inside the
/// projection transaction.
/// </summary>
internal static class PublicExecutionAggregator
{
    /// <summary>
    /// Computes the public component facts for the Job anchor. A
    /// prepared launch Job (durable Job prepare fact, no Session
    /// acceptance or rejection fact yet) projects
    /// <c>status=accepted</c>/<c>jobStatus=preparing</c> with null live
    /// Session/Input/Turn IDs, anchored on the Job id.
    /// </summary>
    public static PublicAnchorComponents BuildJobAnchor(
        PublicProjectionFacts facts,
        PublicProjectionFacts.JobFacts job)
    {
        var launchInput = ResolveLaunchInput(facts, job);
        var launchTurn = launchInput is null || launchInput.Acceptance == AgentSessionInputAcceptance.Rejected
            ? null
            : ResolveTurnForInput(facts, launchInput.InputId);
        var rejected = launchInput is { Acceptance: AgentSessionInputAcceptance.Rejected };
        var joined = launchInput is not null;

        var jobStatus = joined ? MapJobStatus(job.Status) : PublicExecutionFieldValues.JobPreparing;
        string? inputStatus = launchInput is null
            ? null
            : MapInputStatus(launchInput.Acceptance);
        string? turnStatus = launchTurn is null
            ? null
            : MapTurnStatus(launchTurn.Status, facts.PendingStopActive);

        // The canonical input record exists for a rejected launch input,
        // so its public identity is live; a rejection that created no
        // records contributes no session facts at all and stays
        // preparing until a durable fact lands.
        var liveInputId = joined ? launchInput!.InputId : null;
        var liveTurnId = launchTurn?.TurnId;

        string? sessionId = joined ? facts.SessionId : null;

        var dispatchBlocked = IsDispatchBlocked(job);
        var admission = ResolveAdmission(
            facts,
            jobStatus,
            turnStatus,
            dispatchBlocked,
            sessionExists: joined);

        return new PublicAnchorComponents(
            JobId: job.JobKey,
            SessionId: sessionId,
            InputId: liveInputId,
            TurnId: liveTurnId,
            JobStatus: jobStatus,
            SessionActivity: joined ? MapActivity(facts.Activity) : null,
            Admission: admission,
            InputStatus: inputStatus,
            TurnStatus: turnStatus,
            Outcome: null,
            ReasonCode: null,
            Output: null,
            Error: null,
            AcceptedAt: job.SubmittedAt ?? facts.SessionCreatedAt,
            QueuedAt: job.ReadySince,
            StartedAt: job.RunningSince,
            TerminalAt: null,
            TerminalFact: null)
        {
            // Terminal facts are attached by ApplyTerminal below.
        };
    }

    /// <summary>
    /// Computes the public component facts for a Session Input anchor.
    /// A rejected input is terminal with no live Input/Turn identity.
    /// </summary>
    public static PublicAnchorComponents BuildInputAnchor(
        PublicProjectionFacts facts,
        PublicProjectionFacts.InputFacts input)
    {
        var turn = ResolveTurnForInput(facts, input.InputId);
        var job = input.JobId is not null
            ? FindJob(facts, input.JobId)
            : turn?.JobId is not null ? FindJob(facts, turn.JobId) : null;

        string? turnStatus = input.Acceptance == AgentSessionInputAcceptance.Rejected
            ? null
            : turn is null ? null : MapTurnStatus(turn.Status, facts.PendingStopActive);

        var dispatchBlocked = job is not null && IsDispatchBlocked(job);
        var admission = ResolveAdmission(facts, job is null ? null : MapJobStatus(job.Status), turnStatus, dispatchBlocked, sessionExists: true);

        return new PublicAnchorComponents(
            JobId: job?.JobKey,
            SessionId: facts.SessionId,
            InputId: input.InputId,
            TurnId: input.Acceptance == AgentSessionInputAcceptance.Rejected ? null : turn?.TurnId,
            JobStatus: job is null ? null : MapJobStatus(job.Status),
            SessionActivity: MapActivity(facts.Activity),
            Admission: admission,
            InputStatus: MapInputStatus(input.Acceptance),
            TurnStatus: turnStatus,
            Outcome: null,
            ReasonCode: null,
            Output: null,
            Error: null,
            AcceptedAt: input.RecordedAt,
            QueuedAt: turn?.RecordedAt,
            StartedAt: job?.RunningSince,
            TerminalAt: null,
            TerminalFact: null);
    }

    /// <summary>
    /// Computes the public component facts for a Session Turn anchor.
    /// The anchor stays anchored to its own canonical record: a
    /// terminal Turn stays terminal inside an active Session, and
    /// <c>sessionActivity</c> is reported as context only.
    /// </summary>
    public static PublicAnchorComponents BuildTurnAnchor(
        PublicProjectionFacts facts,
        PublicProjectionFacts.TurnFacts turn)
    {
        var inputId = turn.InputIds.FirstOrDefault();
        var input = inputId is null ? null : FindInput(facts, inputId);
        var job = turn.JobId is not null
            ? FindJob(facts, turn.JobId)
            : null;

        var dispatchBlocked = job is not null && IsDispatchBlocked(job);
        var admission = ResolveAdmission(facts, job is null ? null : MapJobStatus(job.Status), MapTurnStatus(turn.Status, facts.PendingStopActive), dispatchBlocked, sessionExists: true);

        return new PublicAnchorComponents(
            JobId: job?.JobKey,
            SessionId: facts.SessionId,
            InputId: input is { Acceptance: AgentSessionInputAcceptance.Rejected } ? null : inputId,
            TurnId: turn.TurnId,
            JobStatus: job is null ? null : MapJobStatus(job.Status),
            SessionActivity: MapActivity(facts.Activity),
            Admission: admission,
            InputStatus: input is null ? null : MapInputStatus(input.Acceptance),
            TurnStatus: MapTurnStatus(turn.Status, facts.PendingStopActive),
            Outcome: null,
            ReasonCode: null,
            Output: null,
            Error: null,
            AcceptedAt: input?.RecordedAt,
            QueuedAt: turn.RecordedAt,
            StartedAt: job?.RunningSince,
            TerminalAt: null,
            TerminalFact: null);
    }

    /// <summary>
    /// Computes the session-level observation used as the execution
    /// payload of session-scope events: the most recent Turn (or, before
    /// any Turn exists, the most recent Input) provides the live
    /// execution context beside the Session's own activity facts.
    /// </summary>
    public static PublicAnchorComponents BuildSessionAnchor(
        PublicProjectionFacts facts)
    {
        var contextTurn = facts.Turns.LastOrDefault();
        var contextInput = contextTurn is null
            ? facts.Inputs.LastOrDefault()
            : contextTurn.InputIds.Count > 0 ? FindInput(facts, contextTurn.InputIds[0]) : null;
        var job = contextTurn?.JobId is not null
            ? FindJob(facts, contextTurn.JobId)
            : contextInput?.JobId is not null ? FindJob(facts, contextInput.JobId) : null;

        var dispatchBlocked = job is not null && IsDispatchBlocked(job);
        var turnStatus = contextTurn is null
            ? null
            : MapTurnStatus(contextTurn.Status, facts.PendingStopActive);

        return new PublicAnchorComponents(
            JobId: job?.JobKey,
            SessionId: facts.SessionId,
            InputId: contextInput?.InputId,
            TurnId: contextTurn?.TurnId,
            JobStatus: job is null ? null : MapJobStatus(job.Status),
            SessionActivity: MapActivity(facts.Activity),
            Admission: ResolveAdmission(facts, job is null ? null : MapJobStatus(job.Status), turnStatus, dispatchBlocked, sessionExists: true),
            InputStatus: contextInput is null ? null : MapInputStatus(contextInput.Acceptance),
            TurnStatus: turnStatus,
            Outcome: null,
            ReasonCode: null,
            Output: null,
            Error: null,
            AcceptedAt: contextInput?.RecordedAt ?? facts.SessionCreatedAt,
            QueuedAt: contextTurn?.RecordedAt,
            StartedAt: job?.RunningSince,
            TerminalAt: null,
            TerminalFact: null);
    }

    /// <summary>
    /// Applies the terminal-fact mapping onto component facts: the
    /// winning terminal fact (Turn terminal, durable input rejection,
    /// or Job terminal outcome, in that precedence) freezes the public
    /// outcome, output, error, and terminal timestamp and returns the
    /// internal terminal fact identity used by the snapshot fence.
    /// </summary>
    public static PublicAnchorComponents ApplyTerminal(PublicAnchorComponents components, PublicProjectionFacts facts)
    {
        if (components.TurnStatus == PublicExecutionFieldValues.TurnTerminal
            && components.TurnId is { } turnId
            && FindTurn(facts, turnId) is { } turn)
        {
            var outcome = MapTurnOutcome(turn.Status);
            return components with
            {
                Outcome = outcome,
                Output = outcome == PublicExecutionFieldValues.OutcomeCompleted
                    ? ExtractPublicOutput(turn.Result?.Output)
                    : null,
                Error = outcome == PublicExecutionFieldValues.OutcomeFailed ? TurnFailedError() : null,
                TerminalAt = turn.UpdatedAt,
                TerminalFact = TurnTerminalFact(turnId),
            };
        }

        if (components.InputStatus == PublicExecutionFieldValues.InputRejected)
        {
            return components with
            {
                Outcome = PublicExecutionFieldValues.OutcomeRejected,
                Error = RejectedError(),
                TerminalAt = components.AcceptedAt,
                TerminalFact = InputRejectedFact(components.InputId ?? components.JobId ?? components.SessionId ?? "unknown"),
            };
        }

        if (components.JobStatus == PublicExecutionFieldValues.JobTerminal
            && components.JobId is { } jobId
            && FindJob(facts, jobId) is { } job)
        {
            var outcome = MapJobOutcome(job.Status);
            return components with
            {
                Outcome = outcome,
                Output = outcome == PublicExecutionFieldValues.OutcomeCompleted
                    ? ExtractPublicOutput(job.TerminalResult?.Output)
                    : null,
                Error = outcome == PublicExecutionFieldValues.OutcomeFailed ? TurnFailedError() : null,
                TerminalAt = job.TerminalAt,
                TerminalFact = JobTerminalFact(jobId),
            };
        }

        return components;
    }

    /// <summary>
    /// The five-state aggregate with the fixed precedence: terminal
    /// fence, durable rejection, unresolved facts (unknown),
    /// outcome_pending (running), queued-with-blocked-admission
    /// (queued), then running over queued over accepted. Components
    /// stay visible beside the aggregate.
    /// </summary>
    public static string ComputeStatus(PublicAnchorComponents components, bool sessionExists)
    {
        // 1 + 2: a durable terminal fact (Turn terminal fence, durable
        // input rejection, or Job terminal outcome) is terminal.
        if (components.TurnStatus == PublicExecutionFieldValues.TurnTerminal
            || components.JobStatus == PublicExecutionFieldValues.JobTerminal
            || components.InputStatus == PublicExecutionFieldValues.InputRejected)
        {
            return PublicExecutionFieldValues.StatusTerminal;
        }

        // 3: unresolved consumed facts yield unknown — never terminal.
        if (components.TurnStatus == PublicExecutionFieldValues.TurnUnknown
            || components.JobStatus == PublicExecutionFieldValues.JobUnknown
            || components.SessionActivity == PublicExecutionFieldValues.SessionUnknown)
        {
            return PublicExecutionFieldValues.StatusUnknown;
        }

        // 4: outcome_pending is running, never terminal.
        if (components.TurnStatus == PublicExecutionFieldValues.TurnOutcomePending)
        {
            return PublicExecutionFieldValues.StatusRunning;
        }

        // 5: a retryable dispatch block stays queued (admission is
        // blocked separately).
        // 6: running wins over queued, queued wins over accepted.
        if (components.TurnStatus == PublicExecutionFieldValues.TurnRunning
            || components.JobStatus == PublicExecutionFieldValues.JobRunning)
        {
            return PublicExecutionFieldValues.StatusRunning;
        }

        if (components.TurnStatus == PublicExecutionFieldValues.TurnQueued
            || components.JobStatus == PublicExecutionFieldValues.JobQueued)
        {
            return PublicExecutionFieldValues.StatusQueued;
        }

        return PublicExecutionFieldValues.StatusAccepted;
    }

    /// <summary>
    /// The public admission component: blocked whenever an applicable
    /// fact is unknown, a stop outcome is unresolved
    /// (outcome_pending), a reset is in progress, or the launch Job is
    /// queued behind a retryable dispatch block; ready otherwise when
    /// a Session exists; null when no Session exists.
    /// </summary>
    public static string? ResolveAdmission(
        PublicProjectionFacts facts,
        string? jobStatus,
        string? turnStatus,
        bool dispatchBlocked,
        bool sessionExists)
    {
        if (!sessionExists)
        {
            return null;
        }

        var blocked = turnStatus == PublicExecutionFieldValues.TurnUnknown
            || turnStatus == PublicExecutionFieldValues.TurnOutcomePending
            || jobStatus == PublicExecutionFieldValues.JobUnknown
            || facts.Activity == AgentSessionActivity.Unknown
            || facts.PendingStopActive
            || facts.PendingResetActive
            || dispatchBlocked;
        return blocked
            ? PublicExecutionFieldValues.AdmissionBlocked
            : PublicExecutionFieldValues.AdmissionReady;
    }

    /// <summary>
    /// Derives the normalized source transitions a Session's public
    /// journal should contain, from consumed facts only. Transition
    /// identities are stable and internal; the projector stores them
    /// on journal rows for replay deduplication.
    /// </summary>
    public static IReadOnlyList<PublicSourceTransition> DeriveTransitions(
        PublicProjectionFacts facts,
        DateTimeOffset now)
    {
        var transitions = new List<PublicSourceTransition>();

        foreach (var input in facts.Inputs)
        {
            if (input.Acceptance == AgentSessionInputAcceptance.Rejected)
            {
                transitions.Add(new PublicSourceTransition(
                    InputRejectedFact(input.InputId),
                    PublicSessionEventTypes.InputRejected,
                    input.RecordedAt ?? now,
                    PublicAnchorKind.Input,
                    input.InputId));
            }
            else if (input.Acceptance == AgentSessionInputAcceptance.Accepted)
            {
                transitions.Add(new PublicSourceTransition(
                    InputAcceptedFact(input.InputId),
                    PublicSessionEventTypes.InputAccepted,
                    input.RecordedAt ?? now,
                    PublicAnchorKind.Input,
                    input.InputId));
            }
        }

        foreach (var turn in facts.Turns)
        {
            switch (turn.Status)
            {
                case AgentTurnStatus.Queued:
                    transitions.Add(new PublicSourceTransition(
                        TurnQueuedFact(turn.TurnId),
                        PublicSessionEventTypes.TurnQueued,
                        turn.RecordedAt ?? now,
                        PublicAnchorKind.Turn,
                        turn.TurnId));
                    break;
                case AgentTurnStatus.Executing when facts.PendingStopActive:
                    transitions.Add(new PublicSourceTransition(
                        TurnOutcomePendingFact(turn.TurnId),
                        PublicSessionEventTypes.TurnOutcomePending,
                        turn.UpdatedAt ?? turn.RecordedAt ?? now,
                        PublicAnchorKind.Turn,
                        turn.TurnId));
                    break;
                case AgentTurnStatus.Executing:
                    transitions.Add(new PublicSourceTransition(
                        TurnRunningFact(turn.TurnId),
                        PublicSessionEventTypes.TurnRunning,
                        turn.UpdatedAt ?? turn.RecordedAt ?? now,
                        PublicAnchorKind.Turn,
                        turn.TurnId));
                    break;
                case AgentTurnStatus.Completed:
                case AgentTurnStatus.Failed:
                case AgentTurnStatus.Cancelled:
                    transitions.Add(new PublicSourceTransition(
                        TurnTerminalFact(turn.TurnId),
                        PublicSessionEventTypes.TurnTerminal,
                        turn.UpdatedAt ?? turn.RecordedAt ?? now,
                        PublicAnchorKind.Turn,
                        turn.TurnId));
                    break;
                case AgentTurnStatus.Unknown:
                    transitions.Add(new PublicSourceTransition(
                        TurnUnknownFact(turn.TurnId),
                        PublicSessionEventTypes.SessionUnknown,
                        turn.UpdatedAt ?? turn.RecordedAt ?? now,
                        PublicAnchorKind.Turn,
                        turn.TurnId));
                    break;
            }
        }

        // session.unknown for a Session whose own facts are unresolved.
        if (facts.Activity == AgentSessionActivity.Unknown
            && facts.Turns.All(turn => turn.Status != AgentTurnStatus.Unknown))
        {
            transitions.Add(new PublicSourceTransition(
                SessionUnknownFact(),
                PublicSessionEventTypes.SessionUnknown,
                now,
                PublicAnchorKind.Session,
                facts.SessionId));
        }

        // session.context_reset: only from a durable canonical binding
        // replacement fact. The first runtime-session binding is the
        // initial bind, not a reset; every later binding fact replaced
        // the physical runtime session, which is the context boundary.
        var runtimeBoundSeen = 0;
        foreach (var journal in facts.SessionJournal)
        {
            if (journal.Type != RuntimeBoundEventType)
            {
                continue;
            }

            runtimeBoundSeen++;
            if (runtimeBoundSeen > 1)
            {
                transitions.Add(new PublicSourceTransition(
                    ContextResetFact(journal.JournalId),
                    PublicSessionEventTypes.ContextReset,
                    journal.Time,
                    PublicAnchorKind.Session,
                    facts.SessionId));
            }
        }

        return transitions
            .OrderBy(t => t.OccurredAt)
            .ThenBy(t => TransitionRank(t.EventType))
            .ThenBy(t => t.Identity, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>One normalized source transition of a Session's public journal.</summary>
    internal sealed record PublicSourceTransition(
        string Identity,
        string EventType,
        DateTimeOffset OccurredAt,
        PublicAnchorKind AnchorKind,
        string AnchorId);

    internal enum PublicAnchorKind
    {
        Session,
        Input,
        Turn,
    }

    internal const string RuntimeBoundEventType = "com.mohist.agent-session.runtime-bound";

    // --- terminal fact identities (the snapshot fence keys) ---

    public static string TurnTerminalFact(string turnId) => $"turn:{turnId}:terminal";
    public static string InputRejectedFact(string inputId) => $"input:{inputId}:rejected";
    public static string JobTerminalFact(string jobId) => $"job:{jobId}:terminal";

    private static string TurnQueuedFact(string turnId) => $"turn:{turnId}:queued";
    private static string TurnRunningFact(string turnId) => $"turn:{turnId}:running";
    private static string TurnOutcomePendingFact(string turnId) => $"turn:{turnId}:outcome_pending";
    private static string TurnUnknownFact(string turnId) => $"turn:{turnId}:unknown";
    private static string InputAcceptedFact(string inputId) => $"input:{inputId}:accepted";
    private static string SessionUnknownFact() => "session:unknown";
    private static string ContextResetFact(long journalId) => $"session:context-reset:{journalId}";

    // --- component mapping helpers ---

    private static string MapJobStatus(AgentJobStatus status) => status switch
    {
        AgentJobStatus.Pending => PublicExecutionFieldValues.JobQueued,
        AgentJobStatus.Running => PublicExecutionFieldValues.JobRunning,
        AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled
            => PublicExecutionFieldValues.JobTerminal,
        AgentJobStatus.Unknown => PublicExecutionFieldValues.JobUnknown,
        _ => PublicExecutionFieldValues.JobUnknown,
    };

    private static string? MapActivity(AgentSessionActivity? activity) => activity switch
    {
        null => null,
        AgentSessionActivity.Idle => PublicExecutionFieldValues.SessionIdle,
        AgentSessionActivity.Active => PublicExecutionFieldValues.SessionActive,
        AgentSessionActivity.Unknown => PublicExecutionFieldValues.SessionUnknown,
        _ => null,
    };

    private static string? MapInputStatus(AgentSessionInputAcceptance acceptance) => acceptance switch
    {
        AgentSessionInputAcceptance.Accepted => PublicExecutionFieldValues.InputAccepted,
        AgentSessionInputAcceptance.Rejected => PublicExecutionFieldValues.InputRejected,
        AgentSessionInputAcceptance.Pending => PublicExecutionFieldValues.InputUnknown,
        _ => null,
    };

    private static string? MapTurnStatus(AgentTurnStatus status, bool pendingStopActive) => status switch
    {
        AgentTurnStatus.Queued => PublicExecutionFieldValues.TurnQueued,
        AgentTurnStatus.Executing when pendingStopActive => PublicExecutionFieldValues.TurnOutcomePending,
        AgentTurnStatus.Executing => PublicExecutionFieldValues.TurnRunning,
        AgentTurnStatus.Completed or AgentTurnStatus.Failed or AgentTurnStatus.Cancelled
            => PublicExecutionFieldValues.TurnTerminal,
        AgentTurnStatus.Unknown => PublicExecutionFieldValues.TurnUnknown,
        _ => null,
    };

    private static string MapTurnOutcome(AgentTurnStatus status) => status switch
    {
        AgentTurnStatus.Completed => PublicExecutionFieldValues.OutcomeCompleted,
        AgentTurnStatus.Cancelled => PublicExecutionFieldValues.OutcomeCancelled,
        _ => PublicExecutionFieldValues.OutcomeFailed,
    };

    private static string MapJobOutcome(AgentJobStatus status) => status switch
    {
        AgentJobStatus.Completed => PublicExecutionFieldValues.OutcomeCompleted,
        AgentJobStatus.Cancelled => PublicExecutionFieldValues.OutcomeCancelled,
        _ => PublicExecutionFieldValues.OutcomeFailed,
    };

    private static bool IsDispatchBlocked(PublicProjectionFacts.JobFacts job) =>
        job.Status == AgentJobStatus.Pending
        && job.WaitingReason is CapacityFullWaitReason or ConcurrencyLimitWaitReason or NoOnlineRunnerWaitReason;

    private const string CapacityFullWaitReason = "capacity-full";
    private const string ConcurrencyLimitWaitReason = "concurrency-limit";
    private const string NoOnlineRunnerWaitReason = "no-online-runner";

    /// <summary>
    /// Extracts the public final output from the persisted canonical
    /// output string: only a JSON object carrying a non-empty
    /// <c>text</c> string becomes <c>{ "text": ... }</c>; anything else
    /// (missing text, malformed, raw payload) projects to null so no
    /// transcript or raw provider response can leak.
    /// </summary>
    public static PublicExecutionOutput? ExtractPublicOutput(string? canonicalOutput)
    {
        if (string.IsNullOrWhiteSpace(canonicalOutput))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(canonicalOutput);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(text.GetString()))
            {
                return new PublicExecutionOutput { Text = text.GetString()! };
            }
        }
        catch (JsonException)
        {
            // Malformed canonical output is not public output.
        }

        return null;
    }

    /// <summary>The safe public reason code for a component set, or null.</summary>
    public static string? ResolveReasonCode(PublicAnchorComponents components)
    {
        if (components.TurnStatus == PublicExecutionFieldValues.TurnUnknown
            || components.JobStatus == PublicExecutionFieldValues.JobUnknown
            || components.SessionActivity == PublicExecutionFieldValues.SessionUnknown)
        {
            return PublicExecutionFieldValues.Reasons.StopOutcomeUnknown;
        }

        return null;
    }

    public static PublicExecutionError TurnFailedError() => new()
    {
        Code = PublicExecutionFieldValues.OutcomeFailed,
        Message = "The agent execution failed.",
    };

    public static PublicExecutionError RejectedError() => new()
    {
        Code = PublicExecutionFieldValues.OutcomeRejected,
        Message = "The request was not accepted.",
    };

    // --- fact lookups ---

    public static PublicProjectionFacts.InputFacts? ResolveLaunchInput(
        PublicProjectionFacts facts,
        PublicProjectionFacts.JobFacts job)
    {
        foreach (var input in facts.Inputs)
        {
            if (string.Equals(input.JobId, job.JobKey, StringComparison.Ordinal)
                || (job.InitialInputId is not null
                    && string.Equals(input.InputId, job.InitialInputId, StringComparison.Ordinal)))
            {
                return input;
            }
        }

        return null;
    }

    public static PublicProjectionFacts.TurnFacts? ResolveTurnForInput(
        PublicProjectionFacts facts,
        string inputId)
    {
        foreach (var turn in facts.Turns)
        {
            if (turn.InputIds.Contains(inputId, StringComparer.Ordinal))
            {
                return turn;
            }
        }

        return null;
    }

    public static PublicProjectionFacts.JobFacts? FindJob(PublicProjectionFacts facts, string jobId)
    {
        foreach (var job in facts.Jobs)
        {
            if (string.Equals(job.JobKey, jobId, StringComparison.Ordinal))
            {
                return job;
            }
        }

        return null;
    }

    public static PublicProjectionFacts.InputFacts? FindInput(PublicProjectionFacts facts, string inputId)
    {
        foreach (var input in facts.Inputs)
        {
            if (string.Equals(input.InputId, inputId, StringComparison.Ordinal))
            {
                return input;
            }
        }

        return null;
    }

    public static PublicProjectionFacts.TurnFacts? FindTurn(PublicProjectionFacts facts, string turnId)
    {
        foreach (var turn in facts.Turns)
        {
            if (string.Equals(turn.TurnId, turnId, StringComparison.Ordinal))
            {
                return turn;
            }
        }

        return null;
    }

    private static int TransitionRank(string eventType) => eventType switch
    {
        PublicSessionEventTypes.InputAccepted => 0,
        PublicSessionEventTypes.InputRejected => 1,
        PublicSessionEventTypes.TurnQueued => 2,
        PublicSessionEventTypes.TurnRunning => 3,
        PublicSessionEventTypes.TurnOutcomePending => 4,
        PublicSessionEventTypes.ContextReset => 5,
        PublicSessionEventTypes.TurnTerminal => 6,
        PublicSessionEventTypes.SessionUnknown => 7,
        _ => 8,
    };

    /// <summary>
    /// The consumed-state digest used as the AgentSession ledger feed
    /// watermark. AgentSession ledger rows carry no revision column,
    /// so the projection checkpoints the hash of the consumed state
    /// JSON: an identical digest proves the batch already projected
    /// exactly this state.
    /// </summary>
    public static string StateDigest(string stateJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(stateJson));
        return Convert.ToHexString(bytes);
    }
}
