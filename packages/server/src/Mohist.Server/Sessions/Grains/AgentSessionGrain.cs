using System.Text.Json;
using Mohist.Server.Contracts;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Agent.Grains;
using Orleans;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain : Grain, IAgentSessionGrain, IRemindable
{
    private static readonly TimeSpan FollowupLeaseWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PersistTimerDueTime = TimeSpan.FromMilliseconds(200);
    internal const string ScheduleReminderPrefix = "schedule:";
    internal const string ScheduleRecoveryReminderName = "schedule-recovery";
    internal const string StopRecoveryReminderName = "stop-recovery";
    internal static readonly TimeSpan ScheduleRecoveryReminderPeriod = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan StopRecoveryReminderPeriod = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan StopOperationDeadline = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan OneShotReminderPeriod = TimeSpan.FromMinutes(1);
    private const string OpenCodeRuntime = "opencode";
    private const string PiRuntime = "pi";

    private readonly IAgentSessionStore _stateStore;
    private readonly IAgentSessionTranscriptStore _transcriptStore;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ITranscriptEventPublisher _transcriptPublisher;
    private readonly IAgentSessionPersistenceObserver _persistenceObserver;
    private readonly TimeProvider _timeProvider;
    private readonly IAgentSessionConnectionRegistry _connections;
    private readonly IGrainFactory _grains;
    private readonly IEventStore _eventStore;
    private readonly IBackgroundTaskLauncher _backgroundTasks;
    private readonly IFollowupDispatchScheduler? _followupDispatchScheduler;
    private readonly ISessionWorkPort? _sessionWorkPort;
    private readonly ISessionStopDelivery? _sessionStopDelivery;
    private readonly ILogger<AgentSessionGrain> _log;
    private readonly TranscriptAccumulator _transcript = new();
    private AgentSession? _session;
    private bool _sessionReloadRequired;
    private AgentSessionTranscriptSummary? _cachedSummary;
    private long _realtimeSequence;
    private IDisposable? _persistTimer;
    private bool _stateDirty;
    private readonly List<AgentSessionEvent> _pendingDomainEvents = new();
    private string? _lastHealthStatus;
    private double? _lastHealthPercent;

    public AgentSessionGrain(
        IAgentSessionStore stateStore,
        IAgentSessionTranscriptStore transcriptStore,
        IDbContextFactory<MohistDbContext> dbFactory,
        ITranscriptEventPublisher transcriptPublisher,
        IAgentSessionPersistenceObserver persistenceObserver,
        TimeProvider timeProvider,
        IAgentSessionConnectionRegistry connections,
        IGrainFactory grains,
        ILogger<AgentSessionGrain> log,
        IEventStore eventStore,
        IBackgroundTaskLauncher backgroundTasks,
        IFollowupDispatchScheduler? followupDispatchScheduler = null,
        ISessionWorkPort? sessionWorkPort = null,
        ISessionStopDelivery? sessionStopDelivery = null)
    {
        _stateStore = stateStore;
        _transcriptStore = transcriptStore;
        _dbFactory = dbFactory;
        _transcriptPublisher = transcriptPublisher;
        _persistenceObserver = persistenceObserver;
        _timeProvider = timeProvider;
        _connections = connections;
        _grains = grains;
        _eventStore = eventStore;
        _backgroundTasks = backgroundTasks;
        _followupDispatchScheduler = followupDispatchScheduler;
        _sessionWorkPort = sessionWorkPort;
        _sessionStopDelivery = sessionStopDelivery;
        _log = log;
    }

    private string SessionId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _sessionReloadRequired = false;
        _session = await _stateStore.LoadAsync(SessionId);
        if (_session is not null)
        {
            _session.PersistedActivitySummary = (_session.PersistedActivitySummary ?? AgentSessionActivitySummaryState.Empty).Normalize();
            if (ReclaimUnconfirmedFollowupDispatches(_session))
                await _stateStore.SaveAsync(SessionId, _session);
        }
        if (_session?.Status.PendingTranscriptEvidence?.Count > 0)
            EnsurePersistenceTimer();
        await EnsureScheduleRemindersAsync();
        await EnsureStopRecoveryReminderAsync();
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName.StartsWith(ScheduleReminderPrefix, StringComparison.Ordinal))
        {
            // One-shot delivery reminder: unregister explicitly so the
            // reminder never re-fires; activation loss / registration
            // failure is covered by the recovery reminder tick and by
            // re-registration on activation.
            try
            {
                var reminder = await this.GetReminder(reminderName);
                if (reminder is not null)
                    await this.UnregisterReminder(reminder);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
            var scheduleId = reminderName[ScheduleReminderPrefix.Length..];
            var schedule = _session?.Status.Schedules?.FirstOrDefault(candidate =>
                string.Equals(candidate.ScheduleId, scheduleId, StringComparison.Ordinal));
            if (schedule is not null)
                await DeliverScheduledInputAsync(schedule);
            return;
        }
        if (string.Equals(reminderName, ScheduleRecoveryReminderName, StringComparison.Ordinal))
            await RunScheduledInputRecoveryAsync();
        else if (string.Equals(reminderName, StopRecoveryReminderName, StringComparison.Ordinal))
            await RunStopRecoveryAsync();
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _persistTimer?.Dispose();
        _persistTimer = null;

        // A quarantined activation was deactivated because an event-aware save
        // failed; its in-memory session and pending events are dirty (the store
        // rolled back). Do not attempt another flush from that dirty state.
        if (_sessionReloadRequired)
            return;

        await FlushAsync(ct);
    }

    public async Task<AgentSessionInfo> OpenAsync(OpenAgentSessionCommand command)
    {
        RejectIfReloadRequired();
        if (_session is null)
        {
            _session = CreateSession(command);
        }
        else
        {
            _ = _session.MergeMetadata(command.Metadata);
            // When a session was minted up front (e.g. by the generic
            // agent-session launch endpoint) the launch endpoint
            // opened the session with an empty RunnerId; the runner's
            // subsequent open call carries the authoritative RunnerId,
            // and we stamp it onto the runtime exactly once. An
            // already-bound RunnerId is left untouched so workflow
            // sessions remain sticky across reopens (the existing
            // semantics the runner-side retry flow relies on).
            if ((string.IsNullOrWhiteSpace(_session.Runtime.RunnerId)
                && !string.IsNullOrWhiteSpace(command.RunnerId))
                || (string.IsNullOrWhiteSpace(_session.Runtime.WorkDir)
                    && !string.IsNullOrWhiteSpace(command.WorkDir)))
            {
                _session.Runtime = _session.Runtime with
                {
                    RunnerId = string.IsNullOrWhiteSpace(_session.Runtime.RunnerId)
                        ? command.RunnerId
                        : _session.Runtime.RunnerId,
                    WorkDir = string.IsNullOrWhiteSpace(_session.Runtime.WorkDir)
                        ? command.WorkDir
                        : _session.Runtime.WorkDir,
                };
            }
        }

        await _stateStore.SaveAsync(SessionId, _session);
        _connections.RegisterSession(_session.Runtime.RunnerId, SessionId);
        return await ToInfoAsync(_session);
    }

    private AgentSession CreateSession(OpenAgentSessionCommand command)
    {
        RequireProjectOwnership(command.Metadata);
        var session = AgentSession.Create(
            SessionId,
            command.RunnerId ?? string.Empty,
            command.WorkDir,
            command.Metadata,
            Now(),
            runtime: command.AgentRuntime);
        session.LaunchVisibility = command.LaunchVisibility;
        session.Settings = new AgentSessionSettings(command.Model, command.Definition, command.AgentSessionStartup);
        return session;
    }

    private static void RequireProjectOwnership(AgentSessionMetadata? metadata)
    {
        var labels = metadata?.Labels;
        if (labels is not null
            && labels.TryGetValue(AgentSessionQueryMetadataKeys.ProjectId, out var projectId)
            && !string.IsNullOrWhiteSpace(projectId))
            return;

        throw new InvalidOperationException("Agent session cannot open without the required project-id label.");
    }

    public async Task<AgentSessionInfo> AttachPhysicalSessionAsync(AttachPhysicalSessionCommand command)
    {
        var session = await GetRequiredAsync();

        var now = Now();
        var wasBound = !string.IsNullOrWhiteSpace(session.Status.AgentRuntimeSessionId);
        var events = session.AttachPhysicalSession(
            command.AgentSessionId,
            command.Model,
            command.WorkDir,
            command.ChangeDir,
            command.ProcessPid,
            now,
            command.Runtime,
            command.ExpectedRuntime,
            command.ExpectedAgentSessionId,
            command.ExpectedRunnerId);
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            _connections.RegisterSession(session.Runtime.RunnerId, SessionId);
            return await ToInfoAsync(session);
        }

        var transcriptEntries = wasBound && events.Any(e => e.Value is AgentSessionRuntimeBound)
            ? BuildContextResetTranscriptEntries(session, "runtime-change", now)
            : [];
        await PersistRecoveryAsync(session, events, transcriptEntries);
        _connections.RegisterSession(session.Runtime.RunnerId, SessionId);
        return await ToInfoAsync(session);
    }

    public async Task<AgentSessionInfo> RecoverMissingRuntimeSessionAsync(RecoverMissingRuntimeSessionCommand command)
    {
        var session = await GetRequiredAsync();
        await ExpireAcceptedFollowupsAsync(session);
        EnsureSessionIdleForRecovery(session);
        var now = Now();
        var events = session.RebindRuntimeSession(
            new AgentRuntimeBinding(command.ExpectedRunnerId, command.ExpectedRuntime, command.ExpectedRuntimeSessionId),
            new AgentRuntimeBinding(command.ExpectedRunnerId, command.ExpectedRuntime, command.ReplacementRuntimeSessionId),
            "missing-recovery",
            now,
            session.BindingEpoch);
        await PersistRecoveryAsync(session, events, BuildContextResetTranscriptEntries(session, "missing-recovery", now));
        return await ToInfoAsync(session);
    }

    public async Task<AgentSessionInfo> ReconcileMissingBindingAsync(ReconcileMissingBindingCommand command)
    {
        var session = await GetRequiredAsync();
        var now = Now();
        var events = session.ReconcileMissingBinding(
            new AgentRuntimeBinding(command.ExpectedRunnerId, command.ExpectedRuntime, command.ExpectedRuntimeSessionId),
            new AgentRuntimeBinding(command.ExpectedRunnerId, command.ExpectedRuntime, command.ReplacementRuntimeSessionId),
            now);
        await PersistRecoveryAsync(session, events, BuildContextResetTranscriptEntries(session, "missing-recovery", now));
        return await ToInfoAsync(session);
    }

    public async Task<AgentSessionRecoveryResult> CompactAsync(CompactAgentSessionCommand command)
    {
        var session = await GetRequiredAsync();
        await ExpireAcceptedFollowupsAsync(session);
        EnsureRuntimeSessionPresent(session);
        EnsureSessionIdleForRecovery(session);

        var now = Now();
        var usage = AgentSessionJsonHelper.Usage(session);
        var usedBefore = usage.ContextWindowUsed;
        var size = usage.ContextWindowSize;

        var summary = string.IsNullOrWhiteSpace(command.Summary)
            ? await AgentSessionSummaryBuilder.BuildAsync(_dbFactory, session.Id, command.MaxSummaryChars)
            : command.Summary!;

        var events = session.RecordCompaction(
            usedBefore,
            usedBefore,
            size,
            "summary",
            summary,
            now);
        var transcriptEntries = BuildCompactionTranscriptEntries(
            session,
            usedBefore,
            usedBefore,
            size,
            summary,
            now);

        await PersistRecoveryAsync(session, events, transcriptEntries);

        return BuildRecoveryResult(session, usedBefore, size, "compact", wasCompacted: true);
    }

    public async Task<AgentSessionRecoveryResult> ResetAsync(ResetAgentSessionCommand command)
    {
        var session = await GetRequiredAsync();
        await ExpireAcceptedFollowupsAsync(session);
        EnsureSessionIdleForRecovery(session);
        EnsureBindingChangeAllowed(session, command.ExpectedBindingEpoch);
        var now = Now();
        var usage = AgentSessionJsonHelper.Usage(session);
        var usedBefore = usage.ContextWindowUsed;
        var size = usage.ContextWindowSize;

        var events = session.RebindRuntimeSession(
            new AgentRuntimeBinding(session.Runtime.RunnerId, session.Runtime.Runtime, command.ExpectedRuntimeSessionId),
            new AgentRuntimeBinding(session.Runtime.RunnerId, command.ReplacementRuntime, command.ReplacementRuntimeSessionId),
            "reset",
            now,
            command.ExpectedBindingEpoch ?? session.BindingEpoch);

        await PersistRecoveryAsync(session, events, BuildContextResetTranscriptEntries(session, "reset", now));

        return BuildRecoveryResult(session, usedBefore, size, "reset", wasCompacted: false);
    }

    public async Task<SessionCommandRequest> PrepareSessionCommandAsync(SessionCommandKind command, string? idempotencyKey = null)
    {
        if (command is not (SessionCommandKind.Compact or SessionCommandKind.Reset))
            throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported session command");

        return await BeginSessionCommandAsync(command, idempotencyKey);
    }

    public Task<SessionCommandRequest> BeginResetAsync(string? idempotencyKey = null) =>
        BeginSessionCommandAsync(SessionCommandKind.Reset, idempotencyKey);

    public async Task<AgentSessionRecoveryResult?> GetCompletedRecoveryAsync(SessionCommandKind command, string? idempotencyKey = null)
    {
        var session = await GetRequiredAsync();
        var reservation = session.Status.PendingReset;
        return reservation is not null
            && string.Equals(reservation.Command, CommandName(command), StringComparison.Ordinal)
            && MatchesRecoveryIdempotencyKey(reservation, RecoveryIdempotencyKey(idempotencyKey))
            && reservation.Outcome is not null
            ? ToRecoveryResult(reservation.Outcome)
            : null;
    }

    private async Task<SessionCommandRequest> BeginSessionCommandAsync(SessionCommandKind command, string? idempotencyKey)
    {
        var session = await GetRequiredAsync();
        await ExpireAcceptedFollowupsAsync(session);
        EnsureSessionIdleForRecovery(session);
        if (command == SessionCommandKind.Reset)
            EnsureBindingChangeAllowed(session, null);
        var key = RecoveryIdempotencyKey(idempotencyKey);
        var commandName = CommandName(command);

        if (session.Status.PendingReset is { } pending)
        {
            var sameCommand = string.Equals(pending.Command, commandName, StringComparison.Ordinal);
            if (pending.Outcome is null)
            {
                if (!sameCommand)
                    throw new RecoveryOperationInProgressException(session.Id, pending.Command);

                if (!MatchesRecoveryIdempotencyKey(pending, key))
                {
                    pending = pending with
                    {
                        AdditionalIdempotencyKeys = (pending.AdditionalIdempotencyKeys ?? [])
                            .Append(key)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray()
                    };
                    session.Status = session.Status with { PendingReset = pending };
                    await CommitAsync(session, []);
                }

                return BuildSessionCommandRequest(session, command, pending);
            }

            if (sameCommand && MatchesRecoveryIdempotencyKey(pending, key))
                return BuildSessionCommandRequest(session, command, pending);

            session.Status = session.Status with { PendingReset = null };
            await CommitAsync(session, []);
        }

        if (command == SessionCommandKind.Compact)
            EnsureRuntimeSessionPresent(session);

        if (string.IsNullOrWhiteSpace(session.Runtime.RunnerId))
            throw new RuntimeSessionMissingException(session.Id, session.Status.AgentRuntimeSessionId, session.Runtime.Runtime);

        var runtime = command == SessionCommandKind.Reset && !IsRuntimeRegistered(session.Runtime.Runtime ?? string.Empty)
            ? session.Runtime.Runtime!
            : session.Runtime.Runtime!;
        if (command == SessionCommandKind.Reset && !IsRuntimeRegistered(runtime))
            runtime = OpenCodeRuntime;
        var reservation = new AgentSessionResetReservation(
            Guid.NewGuid().ToString("N"),
            session.Status.AgentRuntimeSessionId,
            runtime,
            Now(),
            commandName,
            IdempotencyKey: key,
            ExpectedBindingEpoch: session.BindingEpoch);
        session.Status = session.Status with { PendingReset = reservation };
        await CommitAsync(session, []);
        return BuildSessionCommandRequest(session, command, reservation);
    }

    public async Task<AgentSessionRecoveryResult> CompleteCompactAsync(CompleteCompactAgentSessionCommand command)
    {
        var session = await GetRequiredAsync();
        await ExpireAcceptedFollowupsAsync(session);
        var reservation = RequireReservation(session, command.OperationId, SessionCommandKind.Compact);
        if (reservation.Outcome is not null)
            return ToRecoveryResult(reservation.Outcome);
        EnsureRuntimeSessionPresent(session);
        EnsureSessionIdleForRecovery(session);

        var now = Now();
        var usage = AgentSessionJsonHelper.Usage(session);
        var usedBefore = usage.ContextWindowUsed;
        var size = usage.ContextWindowSize;
        var summary = string.IsNullOrWhiteSpace(command.Summary)
            ? await AgentSessionSummaryBuilder.BuildAsync(_dbFactory, session.Id, command.MaxSummaryChars)
            : command.Summary!;
        var events = session.RecordCompaction(usedBefore, usedBefore, size, "summary", summary, now);
        var transcriptEntries = BuildCompactionTranscriptEntries(session, usedBefore, usedBefore, size, summary, now);
        var result = BuildRecoveryResult(session, usedBefore, size, "compact", wasCompacted: true);
        session.Status = session.Status with { PendingReset = reservation with { Outcome = ToRecoveryOutcome(result) } };
        await PersistRecoveryAsync(session, events, transcriptEntries, reservation.OperationId);
        return result;
    }

    public async Task<AgentSessionRecoveryResult> CompleteResetAsync(CompleteResetAgentSessionCommand command)
    {
        var session = await GetRequiredAsync();
        await ExpireAcceptedFollowupsAsync(session);
        var reservation = RequireReservation(session, command.OperationId, SessionCommandKind.Reset);
        if (reservation.Outcome is not null)
            return ToRecoveryResult(reservation.Outcome);
        if (!string.Equals(reservation.Runtime, command.ReplacementRuntime, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Reset replacement runtime for AgentSession {session.Id} does not match its reservation.");

        EnsureSessionIdleForRecovery(session);
        EnsureBindingChangeAllowed(session, reservation.ExpectedBindingEpoch);
        var now = Now();
        var usage = AgentSessionJsonHelper.Usage(session);
        var usedBefore = usage.ContextWindowUsed;
        var size = usage.ContextWindowSize;
        var events = session.RebindRuntimeSession(
            new AgentRuntimeBinding(session.Runtime.RunnerId, session.Runtime.Runtime, reservation.ExpectedRuntimeSessionId),
            new AgentRuntimeBinding(session.Runtime.RunnerId, command.ReplacementRuntime, command.ReplacementRuntimeSessionId),
            "reset",
            now,
            reservation.ExpectedBindingEpoch);
        var result = BuildRecoveryResult(session, usedBefore, size, "reset", wasCompacted: false);
        session.Status = session.Status with { PendingReset = reservation with { Outcome = ToRecoveryOutcome(result) } };
        await PersistRecoveryAsync(session, events, BuildContextResetTranscriptEntries(session, "reset", now), reservation.OperationId);
        return result;
    }

    public async Task AbandonResetAsync(string operationId)
    {
        var session = await GetRequiredAsync();
        if (!string.Equals(session.Status.PendingReset?.OperationId, operationId, StringComparison.Ordinal)
            || session.Status.PendingReset?.Outcome is not null)
            return;

        session.Status = session.Status with { PendingReset = null };
        await CommitAsync(session, []);
    }

    public async Task<AgentSessionFollowupReservation> BeginFollowupAsync()
    {
        var session = await GetRequiredAsync();
        EnsureRuntimeSessionPresent(session);
        if (session.Status.PendingReset is { } recovery)
        {
            if (recovery.Outcome is null)
                throw new RecoveryOperationInProgressException(session.Id, recovery.Command);
            session.Status = session.Status with { PendingReset = null };
        }

        var pending = GetPendingFollowups(session);
        if (pending.Any(lease => !lease.Accepted))
            throw new InvalidOperationException("A follow-up operation is already in progress.");
        if (session.Status.PendingStop is { IsActive: true } stop)
            throw new StopOperationInProgressException(session.Id, stop.TurnId);
        if (session.Status.Activity == AgentSessionActivity.Unknown)
            throw new SessionActivityUnknownException(session.Id);

        var startsIdleTurn = session.Status.Activity == AgentSessionActivity.Idle;
        var operationId = Guid.NewGuid().ToString("N");
        var projectId = session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId);
        var agentId = session.Metadata?.Label(GenericAgentSessionMetadata.AgentId);
        var token = !startsIdleTurn
            || string.IsNullOrWhiteSpace(projectId)
            || string.IsNullOrWhiteSpace(agentId)
            ? null
            : FollowupConcurrencyToken(session.Id, operationId);
        var dispatchId = token is null ? null : $"followup:{session.Id}:{operationId}";
        var lease = new AgentSessionFollowupLease(
            operationId,
            session.Status.AgentRuntimeSessionId!,
            StartedAt: Now(),
            ConcurrencyToken: token,
            ConcurrencyAgentId: agentId,
            ConcurrencyDispatchId: dispatchId,
            ConcurrencyGateStatus: token is null ? null : "dispatch-pending");

        SetPendingFollowups(session, pending.Append(lease).ToArray());
        await CommitAsync(session, []);

        var current = GetPendingFollowups(session).FirstOrDefault(candidate =>
            string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal));
        if (current is not null && token is not null)
            await EnsureFollowupGateAsync(session, current);

        current = GetPendingFollowups(session).FirstOrDefault(candidate =>
            string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal)) ?? lease;
        return new AgentSessionFollowupReservation(
            lease.OperationId,
            StartsIdleTurn: startsIdleTurn,
            ConcurrencyPermitHeld: current.ConcurrencyPermitId is not null);
    }

    public async Task ConfirmFollowupAsync(string operationId)
    {
        var session = await GetRequiredAsync();
        var pending = GetPendingFollowups(session);
        var index = pending.ToList().FindIndex(lease => string.Equals(lease.OperationId, operationId, StringComparison.Ordinal));
        if (index < 0 || pending[index].Accepted)
            return;

        var next = pending.ToArray();
        next[index] = next[index] with { Accepted = true, AcceptedAt = Now() };
        SetPendingFollowups(session, next);
        await CommitAsync(session, []);
    }

    public async Task AbandonFollowupAsync(string operationId)
    {
        var session = await GetRequiredAsync();
        var pending = GetPendingFollowups(session);
        var lease = pending.FirstOrDefault(candidate => string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal));
        if (lease is null || lease.Accepted)
            return;

        SetPendingFollowups(session, pending.Where(candidate => !string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal)).ToArray());
        await CommitAsync(session, []);
        await ReleaseFollowupConcurrencyPermitAsync(
            session,
            lease.ConcurrencyToken,
            lease.ConcurrencyAgentId,
            lease.ConcurrencyPermitId,
            lease.ConcurrencyGeneration,
            lease.ConcurrencyWaiterId);
    }

    public async Task<AgentSessionFollowupAcceptResult> AcceptFollowupAsync(AcceptFollowupCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Source))
            throw new ArgumentException("Source is required.", nameof(command));

        var hasText = !string.IsNullOrEmpty(command.Text);
        var hasAttachments = command.Attachments is { Count: > 0 };
        if (!hasText && !hasAttachments)
        {
            throw new ArgumentException(
                "Follow-up input requires non-empty text or at least one accepted attachment.",
                nameof(command));
        }

        var session = await GetRequiredAsync();
        EnsureRuntimeSessionPresent(session);
        if (session.Status.PendingReset is { } recovery)
        {
            if (recovery.Outcome is null)
                throw new RecoveryOperationInProgressException(session.Id, recovery.Command);
            session.Status = session.Status with { PendingReset = null };
        }

        var pending = GetPendingFollowups(session);
        if (pending.Any(lease => !lease.Accepted))
            throw new FollowupOperationInProgressException(session.Id);
        if (session.Status.PendingStop is { IsActive: true } stop)
            throw new StopOperationInProgressException(session.Id, stop.TurnId);
        if (session.Status.Activity == AgentSessionActivity.Unknown)
            throw new SessionActivityUnknownException(session.Id);

        var key = string.IsNullOrWhiteSpace(command.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : command.IdempotencyKey;
        var existing = session.FindFollowupInputByIdempotencyKey(key);
        if (existing is not null)
        {
            var accepted = existing.Input;
            var expectedText = hasText ? command.Text : string.Empty;
            var existingText = accepted.Text ?? string.Empty;
            if (!string.Equals(existingText, expectedText, StringComparison.Ordinal)
                || !string.Equals(accepted.Source, command.Source, StringComparison.Ordinal)
                || !AttachmentSetEquivalent(accepted.Attachments, command.Attachments)
                || !Equals(accepted.Provenance, command.Provenance))
            {
                throw new InvalidOperationException(
                    $"AgentSession {session.Id} already has input '{accepted.Id}' with different content/source for idempotency key.");
            }

            var existingTurn = existing.Turn
                ?? throw new InvalidOperationException(
                    $"AgentSession {session.Id} accepted input '{accepted.Id}' has no assigned turn.");
            await CommitAsync(session, Array.Empty<AgentSessionEvent>());
            return new AgentSessionFollowupAcceptResult(
                InputId: accepted.Id,
                TurnId: existingTurn.Id,
                OperationId: existing.OperationId ?? string.Empty,
                AlreadyAccepted: true,
                ShouldRedeliver: existingTurn.Status == AgentTurnStatus.Queued,
                InputAcceptance: accepted.Acceptance,
                TurnStatus: existingTurn.Status,
                Attachments: accepted.Attachments);
        }

        const int maxQueuedTurns = 16;
        if (session.CountQueuedFollowupInputs() >= maxQueuedTurns)
            throw new AgentSessionFollowupCapacityExceededException(session.Id, maxQueuedTurns);

        var inputId = string.IsNullOrWhiteSpace(command.PreMintedInputId)
            ? Guid.NewGuid().ToString("N")
            : command.PreMintedInputId!;
        var turnId = string.IsNullOrWhiteSpace(command.PreMintedTurnId)
            ? Guid.NewGuid().ToString("N")
            : command.PreMintedTurnId!;

        var result = session.AcceptFollowup(
            inputId: inputId,
            turnId: turnId,
            operationId: Guid.NewGuid().ToString("N"),
            text: command.Text ?? string.Empty,
            source: command.Source,
            idempotencyKey: key,
            now: Now(),
            attachments: command.Attachments,
            provenance: command.Provenance);
        StampFollowupConcurrencyToken(session, result.OperationId);
        await CommitAsync(session, Array.Empty<AgentSessionEvent>());
        var acceptedLease = GetPendingFollowups(session).FirstOrDefault(candidate =>
            string.Equals(candidate.OperationId, result.OperationId, StringComparison.Ordinal));
        if (acceptedLease is not null && session.Status.Activity == AgentSessionActivity.Idle)
            await EnsureFollowupGateAsync(session, acceptedLease);
        return result with { AttachmentResults = command.AttachmentResults };
    }

    private static bool AttachmentSetEquivalent(
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? persisted,
        IReadOnlyList<AgentSessionInputAttachmentDescriptor>? supplied)
    {
        var persistedCount = persisted?.Count ?? 0;
        var suppliedCount = supplied?.Count ?? 0;
        if (persistedCount != suppliedCount) return false;
        if (persistedCount == 0) return true;
        for (var index = 0; index < persistedCount; index++)
        {
            var a = persisted![index];
            var b = supplied![index];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.OriginalFileName, b.OriginalFileName, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.ContentType, b.ContentType, StringComparison.Ordinal)) return false;
            if (a.Size != b.Size) return false;
        }
        return true;
    }

    public async Task<CreateSessionScheduleResult> CreateScheduleAsync(CreateSessionScheduleCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = await GetRequiredAsync();
        if (string.IsNullOrWhiteSpace(command.Text))
            throw new ArgumentException("Schedule text is required.", nameof(command));

        var dueAt = command.DueAt.UtcDateTime;
        var now = Now();
        if (dueAt <= now)
            throw new ScheduleDueInPastException(session.Id, dueAt);

        var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : command.IdempotencyKey!;
        var existing = session.FindScheduleByIdempotencyKey(idempotencyKey);
        if (existing is not null)
        {
            var normalizedText = command.Text.Trim();
            if (!string.Equals(existing.Text.Trim(), normalizedText, StringComparison.Ordinal)
                || existing.DueAt != dueAt)
            {
                throw new ScheduleIdempotencyConflictException(session.Id, idempotencyKey);
            }
            return new CreateSessionScheduleResult(existing, AlreadyExists: true);
        }

        var scheduleId = Guid.NewGuid().ToString("N");
        var schedule = session.CreateSchedule(scheduleId, command.Text, dueAt, idempotencyKey, now);
        await CommitAsync(session, []);
        await EnsureScheduleRemindersAsync();
        return new CreateSessionScheduleResult(schedule, AlreadyExists: false);
    }

    public async Task<IReadOnlyList<SessionScheduleRecord>> ListSchedulesAsync()
    {
        var session = await GetRequiredAsync();
        return session.SortedSchedules();
    }

    public async Task<CancelSessionScheduleResult> CancelScheduleAsync(CancelSessionScheduleCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.ScheduleId))
            throw new ArgumentException("Schedule id is required.", nameof(command));
        var session = await GetRequiredAsync();
        var before = session.FindSchedule(command.ScheduleId)
            ?? throw new ScheduleNotFoundException(session.Id, command.ScheduleId);
        var alreadyTerminal = before.IsTerminal;
        var schedule = session.CancelSchedule(command.ScheduleId, Now());
        if (!alreadyTerminal)
        {
            await CommitAsync(session, []);
            await EnsureScheduleRemindersAsync();
        }
        return new CancelSessionScheduleResult(schedule, AlreadyTerminal: alreadyTerminal);
    }

    public async Task RunScheduledInputRecoveryAsync()
    {
        var session = await GetRequiredAsync();
        var now = Now();
        foreach (var schedule in session.SortedSchedules())
        {
            if (schedule.IsTerminal)
                continue;
            if (schedule.Status == SessionScheduleStatus.Scheduled && schedule.DueAt > now)
                continue;
            await DeliverScheduledInputAsync(schedule);
        }
        await EnsureScheduleRemindersAsync();
    }

    private async Task DeliverScheduledInputAsync(SessionScheduleRecord schedule)
    {
        var session = await GetRequiredAsync();
        var current = session.FindSchedule(schedule.ScheduleId);
        if (current is null || current.IsTerminal)
            return;
        var now = Now();
        if (current.Status == SessionScheduleStatus.Scheduled)
        {
            if (current.DueAt > now)
                return;
            current = session.BeginScheduleDelivery(current.ScheduleId, now);
            await CommitAsync(session, []);
        }
        if (current.Status != SessionScheduleStatus.PendingDelivery)
            return;

        AgentSessionFollowupAcceptResult accept;
        try
        {
            accept = await AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: current.Text,
                Source: "session-schedule",
                IdempotencyKey: $"{ScheduleReminderPrefix}{current.ScheduleId}"));
        }
        catch (Exception ex) when (ex is RuntimeSessionMissingException
            or SessionActivityUnknownException
            or StopOperationInProgressException
            or AgentSessionFollowupCapacityExceededException
            or FollowupOperationInProgressException
            or FollowupConcurrencyLimitException
            or InvalidOperationException)
        {
            // Blocked delivery: stay pending-delivery and retry on the
            // next recovery tick. Runtime-session-missing is NOT treated
            // as deterministic confirmation of a gone runtime session —
            // only the Runner can prove that, and it replaces the binding
            // via its own recovery endpoint; until then we never invent a
            // binding or drop the schedule.
            return;
        }

        session = await GetRequiredAsync();
        var delivered = session.MarkScheduleDelivered(current.ScheduleId, accept.InputId);
        if (delivered.Status == SessionScheduleStatus.Delivered)
        {
            await CommitAsync(session, []);
            await EnsureScheduleRemindersAsync();
            _followupDispatchScheduler?.Schedule(
                session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty,
                session.Id);
        }
    }

    private async Task EnsureScheduleRemindersAsync()
    {
        var schedules = _session?.Status.Schedules ?? [];
        var nonTerminal = schedules.Where(candidate => !candidate.IsTerminal).ToList();
        var now = Now();
        foreach (var schedule in nonTerminal)
        {
            var due = schedule.DueAt - now;
            await this.RegisterOrUpdateReminder(
                $"{ScheduleReminderPrefix}{schedule.ScheduleId}",
                due <= TimeSpan.Zero ? TimeSpan.Zero : due,
                OneShotReminderPeriod);
        }
        if (nonTerminal.Count > 0)
        {
            await this.RegisterOrUpdateReminder(
                ScheduleRecoveryReminderName,
                ScheduleRecoveryReminderPeriod,
                ScheduleRecoveryReminderPeriod);
        }
        else
        {
            try
            {
                var reminder = await this.GetReminder(ScheduleRecoveryReminderName);
                if (reminder is not null)
                    await this.UnregisterReminder(reminder);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }
    }

    private async Task EnsureStopRecoveryReminderAsync()
    {
        if (_session?.Status.PendingStop is { IsActive: true })
        {
            await this.RegisterOrUpdateReminder(
                StopRecoveryReminderName,
                StopRecoveryReminderPeriod,
                StopRecoveryReminderPeriod);
            return;
        }

        try
        {
            var reminder = await this.GetReminder(StopRecoveryReminderName);
            if (reminder is not null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    private SessionStopDeliveryRequest? BuildStopDeliveryRequest(
        AgentTurnRecord turn,
        AgentSessionStopClaim claim)
    {
        if (_session is null)
            return null;

        var metadata = _session.Metadata;
        var projectId = metadata.Label(AgentSessionQueryMetadataKeys.ProjectId);
        var sourceKind = metadata.Label(AgentSessionQueryMetadataKeys.SourceKind);
        if (string.IsNullOrWhiteSpace(projectId)
            || string.IsNullOrWhiteSpace(sourceKind)
            || string.IsNullOrWhiteSpace(_session.Runtime.RunnerId)
            || string.IsNullOrWhiteSpace(_session.Runtime.Runtime)
            || string.IsNullOrWhiteSpace(_session.Status.AgentRuntimeSessionId)
            || string.IsNullOrWhiteSpace(_session.Runtime.WorkDir))
            return null;

        return new SessionStopDeliveryRequest(
            projectId,
            _session.Id,
            turn.Id,
            claim.OperationId,
            _session.Runtime.RunnerId,
            sourceKind,
            metadata.Label(AgentSessionQueryMetadataKeys.WorkflowRunId),
            metadata.Label(AgentSessionQueryMetadataKeys.SessionName),
            _session.Runtime.Runtime,
            _session.Status.AgentRuntimeSessionId,
            _session.Runtime.WorkDir);
    }

    public Task RunStopRecoveryAsync() => RedeliverPendingTurnStopAsync();

    private async Task RedeliverPendingTurnStopAsync()
    {
        var session = await GetRequiredAsync();
        var claim = session.Status.PendingStop;
        if (claim is not { IsActive: true })
        {
            await EnsureStopRecoveryReminderAsync();
            return;
        }

        if (claim.DeadlineAt is { } deadline && deadline <= _timeProvider.GetUtcNow())
        {
            await ApplyStopDeliveryAsync(
                claim.TurnId,
                claim.OperationId,
                AgentSessionStopDisposition.Blocked,
                "stop-recovery-deadline-exhausted");
            return;
        }

        var turn = (session.Status.Turns ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, claim.TurnId, StringComparison.Ordinal));
        if (turn is null || _sessionStopDelivery is null)
        {
            await EnsureStopRecoveryReminderAsync();
            return;
        }

        var request = BuildStopDeliveryRequest(turn, claim);
        if (request is null)
        {
            await EnsureStopRecoveryReminderAsync();
            return;
        }

        if (!IsRecordedStopTargetActive(session, turn))
        {
            var observation = RecoveryObservation(session, turn);
            await ObserveWorkflowExecutionAsync(
                turn,
                observation.Kind,
                observation.ReasonCode,
                stopOperationId: claim.OperationId);
            await EnsureStopRecoveryReminderAsync();
            return;
        }

        if (!claim.DispatchStarted)
        {
            session.MarkTurnStopDispatched(claim.TurnId, claim.OperationId);
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            claim = session.Status.PendingStop ?? claim;
        }

        var result = SessionStopDeliveryArbitration.Interpret(
            await _sessionStopDelivery.DispatchAsync(request));
        await ApplyStopDeliveryAsync(turn.Id, claim.OperationId, result.Disposition);
    }

    private static bool IsRecordedStopTargetActive(AgentSession session, AgentTurnRecord turn)
    {
        if (turn.Status != AgentTurnStatus.Executing || session.Status.Activity != AgentSessionActivity.Active)
            return false;
        if (turn.WorkflowExecution is not { } binding)
            return true;
        return string.Equals(binding.AgentSessionId, session.Id, StringComparison.Ordinal)
            && string.Equals(binding.RunnerId, session.Runtime.RunnerId, StringComparison.Ordinal)
            && string.Equals(binding.Runtime, session.Runtime.Runtime, StringComparison.Ordinal)
            && string.Equals(binding.RuntimeSessionId, session.Status.AgentRuntimeSessionId, StringComparison.Ordinal);
    }

    private static RecoveryStopObservation RecoveryObservation(AgentSession session, AgentTurnRecord turn)
    {
        if (turn.WorkflowExecution is { } binding
            && (!string.Equals(binding.AgentSessionId, session.Id, StringComparison.Ordinal)
                || !string.Equals(binding.RunnerId, session.Runtime.RunnerId, StringComparison.Ordinal)
                || !string.Equals(binding.Runtime, session.Runtime.Runtime, StringComparison.Ordinal)
                || !string.Equals(binding.RuntimeSessionId, session.Status.AgentRuntimeSessionId, StringComparison.Ordinal)))
        {
            return new(SessionWorkflowObservationKind.TargetMissing, "stop-target-replaced");
        }
        if (turn.Status is AgentTurnStatus.Completed or AgentTurnStatus.Failed or AgentTurnStatus.Cancelled)
            return new(ObservationKind(turn.Status), "stop-target-terminal");
        if (turn.Status == AgentTurnStatus.Unknown)
            return new(SessionWorkflowObservationKind.Unknown, "stop-target-unknown");
        return session.Status.Activity switch
        {
            AgentSessionActivity.Idle => new(SessionWorkflowObservationKind.Idle, "stop-target-idle"),
            AgentSessionActivity.Unknown => new(SessionWorkflowObservationKind.Unknown, "stop-target-indeterminate"),
            _ => new(SessionWorkflowObservationKind.Unknown, "stop-target-indeterminate"),
        };
    }

    private readonly record struct RecoveryStopObservation(
        SessionWorkflowObservationKind Kind,
        string ReasonCode);

    private Task ObserveWorkflowExecutionAsync(
        AgentTurnRecord? turn,
        SessionWorkflowObservationKind kind,
        string reasonCode,
        string? message = null,
        string? stopOperationId = null)
    {
        if (_sessionWorkPort is null || turn?.WorkflowExecution is null)
            return Task.CompletedTask;
        return _sessionWorkPort.ObserveAgentExecutionAsync(
            turn.WorkflowExecution,
            kind,
            reasonCode,
            message,
            stopOperationId);
    }

    private static SessionWorkflowObservationKind ObservationKind(AgentTurnStatus status) => status switch
    {
        AgentTurnStatus.Completed => SessionWorkflowObservationKind.Completed,
        AgentTurnStatus.Failed => SessionWorkflowObservationKind.Failed,
        AgentTurnStatus.Cancelled => SessionWorkflowObservationKind.Cancelled,
        AgentTurnStatus.Unknown => SessionWorkflowObservationKind.Unknown,
        _ => SessionWorkflowObservationKind.Unknown,
    };


    public async Task<AgentSessionFollowupDispatch?> BeginNextFollowupDispatchAsync()
    {
        var session = await GetRequiredAsync();
        var turns = session.Status.Turns ?? [];
        if (turns.Any(turn => turn.Status == AgentTurnStatus.Executing)) return null;
        var leases = GetPendingFollowups(session).ToList();
        var turn = turns.FirstOrDefault(turn => string.IsNullOrEmpty(turn.JobId) && turn.Status == AgentTurnStatus.Queued);
        if (turn is null) return null;
        var index = leases.FindIndex(lease => string.Equals(lease.TurnId, turn.Id, StringComparison.Ordinal));
        if (index < 0 || leases[index].Dispatching) return null;

        var lease = leases[index];
        if (!await AcquireFollowupDispatchPermitAsync(session, lease))
            return null;

        leases = GetPendingFollowups(session).ToList();
        index = leases.FindIndex(candidate =>
            string.Equals(candidate.OperationId, lease.OperationId, StringComparison.Ordinal));
        if (index < 0)
            return null;
        lease = GetPendingFollowups(session).FirstOrDefault(candidate =>
            string.Equals(candidate.OperationId, lease.OperationId, StringComparison.Ordinal)) ?? lease;

        var inputs = (session.Status.Inputs ?? []).ToDictionary(input => input.Id, StringComparer.Ordinal);
        var texts = turn.InputIds.Select(id => inputs[id].Text).ToArray();
        var attachments = CollectAttachmentsForDispatch(inputs, turn.InputIds);
        leases[index] = lease with
        {
            Dispatching = true,
            PayloadSealed = true,
            ConcurrencyGateStatus = lease.ConcurrencyPermitId is null ? lease.ConcurrencyGateStatus : "dispatch-pending",
        };
        SetPendingFollowups(session, leases);
        await CommitAsync(session, []);
        if (leases[index].ConcurrencyPermitId is not null
            && leases[index].ConcurrencyToken is not null
            && leases[index].ConcurrencyAgentId is not null
            && leases[index].ConcurrencyDispatchId is not null)
        {
            var dispatchProjectId = session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId);
            var dispatchAgentId = leases[index].ConcurrencyAgentId;
            var dispatchToken = leases[index].ConcurrencyToken;
            var dispatchPermitId = leases[index].ConcurrencyPermitId;
            var dispatchId = leases[index].ConcurrencyDispatchId;
            if (string.IsNullOrWhiteSpace(dispatchProjectId)
                || string.IsNullOrWhiteSpace(dispatchAgentId)
                || string.IsNullOrWhiteSpace(dispatchToken)
                || string.IsNullOrWhiteSpace(dispatchPermitId)
                || string.IsNullOrWhiteSpace(dispatchId))
                return null;
            await _grains
                .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(dispatchProjectId, dispatchAgentId))
                .MarkDispatchedAsync(
                    dispatchProjectId,
                    dispatchAgentId,
                    dispatchToken,
                    dispatchPermitId,
                    dispatchId);
            leases[index] = leases[index] with { ConcurrencyGateStatus = "dispatched" };
            SetPendingFollowups(session, leases);
            await CommitAsync(session, []);
        }
        var inputId = turn.InputIds.Count == 1 ? turn.InputIds[0] : null;
        var provenance = inputId is not null && inputs.TryGetValue(inputId, out var input)
            ? input.Provenance
            : null;
        return new AgentSessionFollowupDispatch(
            turn.Id,
            leases[index].OperationId,
            texts,
            attachments,
            inputId,
            provenance,
            leases[index].ConcurrencyDispatchId ?? $"followup:{session.Id}:{leases[index].OperationId}");
    }

    private async Task<bool> AcquireFollowupDispatchPermitAsync(
        AgentSession session,
        AgentSessionFollowupLease lease)
    {
        var projectId = session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId);
        var agentId = session.Metadata?.Label(GenericAgentSessionMetadata.AgentId);
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(agentId))
            return true;

        var token = lease.ConcurrencyToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            token = FollowupConcurrencyToken(session.Id, lease.OperationId);
            var leases = GetPendingFollowups(session).ToList();
            var index = leases.FindIndex(candidate =>
                string.Equals(candidate.OperationId, lease.OperationId, StringComparison.Ordinal));
            if (index < 0)
                return false;
            leases[index] = leases[index] with
            {
                ConcurrencyToken = token,
                ConcurrencyAgentId = agentId,
                ConcurrencyDispatchId = $"followup:{session.Id}:{lease.OperationId}",
                ConcurrencyGateStatus = "dispatch-pending",
            };
            SetPendingFollowups(session, leases);
            await CommitAsync(session, []);
            lease = leases[index];
        }

        if (lease.ConcurrencyPermitId is not null)
            return true;

        var result = await _grains
            .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId))
            .AcquireAsync(
                projectId,
                agentId,
                token,
                session.Id,
                AgentConcurrencyPermitOwnerKind.Followup,
                lease.ConcurrencyDispatchId ?? $"followup:{session.Id}:{lease.OperationId}");
        var leasesAfterAcquire = GetPendingFollowups(session).ToList();
        var acquiredIndex = leasesAfterAcquire.FindIndex(candidate =>
            string.Equals(candidate.OperationId, lease.OperationId, StringComparison.Ordinal));
        if (acquiredIndex < 0)
            return false;

        if (result == AgentConcurrencyAcquireResult.Waiting)
        {
            var waiter = (await _grains
                .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId))
                .GetSnapshotAsync())
                .Waiters
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Token, token, StringComparison.Ordinal)
                    && string.Equals(candidate.OwnerId, session.Id, StringComparison.Ordinal));
            leasesAfterAcquire[acquiredIndex] = leasesAfterAcquire[acquiredIndex] with
            {
                ConcurrencyGateStatus = "queued",
                WaitingReason = "capacity-full",
                ConcurrencyWaiterId = waiter?.WaiterId,
                ConcurrencyGeneration = waiter?.Generation ?? leasesAfterAcquire[acquiredIndex].ConcurrencyGeneration,
            };
            SetPendingFollowups(session, leasesAfterAcquire);
            await CommitAsync(session, []);
            return false;
        }

        var permit = await _grains
            .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId))
            .GetPermitAsync(token);
        if (permit is null)
            return true;

        leasesAfterAcquire[acquiredIndex] = leasesAfterAcquire[acquiredIndex] with
        {
            ConcurrencyPermitId = permit.PermitId,
            ConcurrencyDispatchId = permit.DispatchId,
            ConcurrencyGeneration = permit.Generation,
            ConcurrencyWaiterId = null,
            ConcurrencyGateStatus = "dispatch-pending",
            WaitingReason = null,
        };
        SetPendingFollowups(session, leasesAfterAcquire);
        await CommitAsync(session, []);
        await _grains
            .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId))
            .ConfirmDispatchPendingAsync(
                projectId,
                agentId,
                token,
                permit.PermitId!,
                permit.DispatchId!);
        return true;
    }

    private async Task EnsureFollowupGateAsync(
        AgentSession session,
        AgentSessionFollowupLease lease)
    {
        if (string.IsNullOrWhiteSpace(lease.ConcurrencyToken)
            || string.IsNullOrWhiteSpace(lease.ConcurrencyAgentId)
            || lease.ConcurrencyPermitId is not null)
            return;

        await AcquireFollowupDispatchPermitAsync(session, lease);
    }

    private void StampFollowupConcurrencyToken(AgentSession session, string? operationId)
    {
        var projectId = session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId);
        var agentId = session.Metadata?.Label(GenericAgentSessionMetadata.AgentId);
        if (string.IsNullOrWhiteSpace(projectId)
            || string.IsNullOrWhiteSpace(agentId)
            || string.IsNullOrWhiteSpace(operationId))
            return;

        var leases = GetPendingFollowups(session).ToList();
        var index = leases.FindIndex(candidate =>
            string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal));
        if (index < 0 || !string.IsNullOrWhiteSpace(leases[index].ConcurrencyToken))
            return;

        leases[index] = leases[index] with
        {
            ConcurrencyToken = FollowupConcurrencyToken(session.Id, operationId),
            ConcurrencyAgentId = agentId,
            ConcurrencyDispatchId = $"followup:{session.Id}:{operationId}",
            ConcurrencyGateStatus = "dispatch-pending",
        };
        SetPendingFollowups(session, leases);
    }

    private static string FollowupConcurrencyToken(string sessionId, string operationId) =>
        $"followup:{sessionId}:{operationId}";

    public async Task ConcurrencyPermitGrantedAsync(
        string? token = null,
        string? permitId = null,
        string? dispatchId = null)
    {
        var session = await GetRequiredAsync();
        var lease = GetPendingFollowups(session).FirstOrDefault(candidate =>
            (token is null || string.Equals(candidate.ConcurrencyToken, token, StringComparison.Ordinal))
            && (dispatchId is null || string.Equals(candidate.ConcurrencyDispatchId, dispatchId, StringComparison.Ordinal))
            && (permitId is null
                || candidate.ConcurrencyPermitId is null
                || string.Equals(candidate.ConcurrencyPermitId, permitId, StringComparison.Ordinal)));
        if (lease is null)
            return;

        if (permitId is not null && lease.ConcurrencyPermitId is null)
        {
            var leases = GetPendingFollowups(session).ToList();
            var index = leases.FindIndex(candidate =>
                string.Equals(candidate.OperationId, lease.OperationId, StringComparison.Ordinal));
            if (index >= 0)
            {
                leases[index] = leases[index] with
                {
                    ConcurrencyPermitId = permitId,
                    ConcurrencyDispatchId = dispatchId ?? leases[index].ConcurrencyDispatchId,
                    ConcurrencyGateStatus = "dispatch-pending",
                    WaitingReason = null,
                };
                SetPendingFollowups(session, leases);
                await CommitAsync(session, []);
            }
        }
        _followupDispatchScheduler?.Schedule(
            session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty,
            session.Id);
    }

    private static IReadOnlyList<AgentSessionInputAttachmentDescriptor>? CollectAttachmentsForDispatch(
        IReadOnlyDictionary<string, AgentSessionInputRecord> inputs,
        IReadOnlyList<string> inputIds)
    {
        var collected = new List<AgentSessionInputAttachmentDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inputId in inputIds)
        {
            if (!inputs.TryGetValue(inputId, out var input)) continue;
            if (input.Attachments is null) continue;
            foreach (var descriptor in input.Attachments)
            {
                if (descriptor is null) continue;
                if (!seen.Add(descriptor.Id)) continue;
                collected.Add(descriptor);
            }
        }
        return collected.Count == 0 ? null : collected;
    }

    public async Task ReleaseFollowupDispatchAsync(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return;
        var session = await GetRequiredAsync();
        var leases = GetPendingFollowups(session).ToList();
        var index = leases.FindIndex(lease => string.Equals(lease.OperationId, operationId, StringComparison.Ordinal));
        if (index < 0 || !leases[index].Dispatching) return;
        var lease = leases[index];
        leases[index] = leases[index] with { Dispatching = false };
        SetPendingFollowups(session, leases);
        await CommitAsync(session, []);
        _followupDispatchScheduler?.Schedule(
            session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty,
            session.Id);
    }

    public async Task MarkFollowupTurnExecutingAsync(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            return;
        var session = await GetRequiredAsync();
        var events = session.MarkFollowupTurnExecuting(operationId, Now());
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            return;
        }
        await CommitAsync(session, events);
    }

    public async Task MarkFollowupTurnTerminalAsync(
        string operationId,
        AgentTurnStatus status,
        AgentTurnResult? result)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            return;
        var session = await GetRequiredAsync();
        var lease = GetPendingFollowups(session).FirstOrDefault(candidate =>
            string.Equals(candidate.OperationId, operationId, StringComparison.Ordinal));
        var events = session.MarkFollowupTurnTerminal(operationId, status, result, Now());
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            if (lease is not null)
                await ReleaseFollowupConcurrencyPermitAsync(
                    session,
                    lease.ConcurrencyToken,
                    lease.ConcurrencyAgentId,
                    lease.ConcurrencyPermitId,
                    lease.ConcurrencyGeneration,
                    lease.ConcurrencyWaiterId);
            _followupDispatchScheduler?.Schedule(
                session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty,
                session.Id);
            return;
        }
        await CommitAsync(session, events);
        if (lease is not null)
            await ReleaseFollowupConcurrencyPermitAsync(
                session,
                lease.ConcurrencyToken,
                lease.ConcurrencyAgentId,
                lease.ConcurrencyPermitId,
                lease.ConcurrencyGeneration,
                lease.ConcurrencyWaiterId);
        _followupDispatchScheduler?.Schedule(
            session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty,
            session.Id);
    }

    private static SessionCommandRequest BuildSessionCommandRequest(
        AgentSession session,
        SessionCommandKind command,
        AgentSessionResetReservation? reservation = null) =>
        new(
            SessionId: session.Id,
            Runtime: reservation?.Runtime ?? session.Runtime.Runtime!,
            RuntimeSessionId: session.Status.AgentRuntimeSessionId,
            RunnerId: session.Runtime.RunnerId,
            WorkDir: session.Runtime.WorkDir,
            Command: command,
            ExpectedRuntimeSessionId: command == SessionCommandKind.Reset ? reservation?.ExpectedRuntimeSessionId : null,
            OperationId: reservation?.OperationId
                ?? throw new InvalidOperationException("Session command requires a persisted operation id."),
            ProjectId: session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId));

    private async Task ReleaseFollowupConcurrencyPermitAsync(
        AgentSession session,
        string? token,
        string? agentId,
        string? permitId = null,
        long generation = 0,
        string? waiterId = null)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(agentId))
            return;
        var projectId = session.Metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId);
        if (string.IsNullOrWhiteSpace(projectId))
            return;

        await _grains
            .GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId))
            .ReleaseAsync(
                projectId,
                agentId,
                token,
                permitId,
                generation == 0 ? null : generation,
                waiterId);
    }

    private static IReadOnlyList<AgentSessionFollowupLease> GetPendingFollowups(AgentSession session)
    {
        if (session.Status.PendingFollowups is { Count: > 0 } pending)
            return pending;
        return session.Status.PendingFollowup is null ? [] : [session.Status.PendingFollowup];
    }

    private static void SetPendingFollowups(AgentSession session, IReadOnlyList<AgentSessionFollowupLease> leases)
    {
        session.Status = session.Status with
        {
            PendingFollowup = null,
            PendingFollowups = leases,
        };
    }

    private static AgentSessionRecoveryOutcome ToRecoveryOutcome(AgentSessionRecoveryResult result) => new(
        result.Id,
        result.Status,
        result.ContextWindowSize,
        result.ContextWindowUsed,
        result.ContextUsagePercent,
        result.ContextWindowUsedBefore,
        result.Operation,
        result.WasCompacted);

    private static AgentSessionRecoveryResult ToRecoveryResult(AgentSessionRecoveryOutcome outcome) => new(
        outcome.Id,
        outcome.Status,
        outcome.ContextWindowSize,
        outcome.ContextWindowUsed,
        outcome.ContextUsagePercent,
        outcome.ContextWindowUsedBefore,
        outcome.Operation,
        outcome.WasCompacted);

    private static string RecoveryIdempotencyKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value;

    private static bool MatchesRecoveryIdempotencyKey(AgentSessionResetReservation reservation, string key) =>
        string.Equals(reservation.IdempotencyKey, key, StringComparison.Ordinal)
        || reservation.AdditionalIdempotencyKeys?.Contains(key, StringComparer.Ordinal) == true;

    private async Task ExpireAcceptedFollowupsAsync(AgentSession session)
    {
        var pending = GetPendingFollowups(session);
        var now = Now();
        var remaining = pending.Where(lease =>
        {
            var startedAt = lease.Accepted ? lease.AcceptedAt : lease.StartedAt;
            return startedAt is { } timestamp
                && now - timestamp <= FollowupLeaseWindow;
        }).ToArray();
        if (remaining.Length == pending.Count) return;
        var expired = pending.Where(lease => !remaining.Contains(lease)).ToArray();
        SetPendingFollowups(session, remaining);
        await CommitAsync(session, []);
        foreach (var lease in expired)
            await ReleaseFollowupConcurrencyPermitAsync(
                session,
                lease.ConcurrencyToken,
                lease.ConcurrencyAgentId,
                lease.ConcurrencyPermitId,
                lease.ConcurrencyGeneration,
                lease.ConcurrencyWaiterId);
    }

    private static AgentSessionResetReservation RequireReservation(
        AgentSession session,
        string operationId,
        SessionCommandKind command)
    {
        var reservation = session.Status.PendingReset;
        if (reservation is null || !string.Equals(reservation.OperationId, operationId, StringComparison.Ordinal))
            throw new StaleRuntimeSessionBindingException(session.Id, operationId, reservation?.OperationId);
        if (!string.Equals(reservation.Command, CommandName(command), StringComparison.Ordinal))
            throw new RecoveryOperationInProgressException(session.Id, reservation.Command);
        return reservation;
    }

    private static string CommandName(SessionCommandKind command) => command switch
    {
        SessionCommandKind.Compact => "compact",
        SessionCommandKind.Reset => "reset",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported session command"),
    };

    private static void EnsureRuntimeSessionPresent(AgentSession session)
    {
        if (!session.IsRuntimeSessionMissing(IsRuntimeRegistered)) return;
        throw new RuntimeSessionMissingException(session.Id, session.Status.AgentRuntimeSessionId, session.Runtime.Runtime);
    }

    private static bool IsRuntimeRegistered(string runtime) =>
        string.Equals(runtime, OpenCodeRuntime, StringComparison.OrdinalIgnoreCase)
        || string.Equals(runtime, PiRuntime, StringComparison.OrdinalIgnoreCase);

    private static void EnsureBindingChangeAllowed(AgentSession session, long? expectedEpoch)
    {
        if (expectedEpoch is not null && expectedEpoch.Value != session.BindingEpoch)
            throw new InvalidOperationException("binding_epoch_changed");
        if (session.BindingUseReceipts?.Any(item => item.State == SessionTreeBindingUseState.Held) == true)
            throw new InvalidOperationException("binding_attach_in_progress");
    }

    private void EnsureSessionIdleForRecovery(AgentSession session)
    {
        var pending = GetPendingFollowups(session);
        var hasNonTerminalTurn = (session.Status.Turns ?? [])
            .Any(turn => turn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing);
        if (pending.Count > 0
            || hasNonTerminalTurn
            || session.Status.PendingStop is { IsActive: true }
            || session.Status.Activity != AgentSessionActivity.Idle)
            throw new InvalidOperationException(
                $"AgentSession {session.Id} is currently active; Compact and Reset require an idle session. "
                + $"Activity={session.Status.Activity}, PendingFollowups={pending.Count}, NonTerminalTurns={hasNonTerminalTurn}.");
    }

    private IReadOnlyList<RuntimeEventEnvelope> BuildContextResetTranscriptEntries(
        AgentSession session,
        string reason,
        DateTime now) =>
    [
        new()
        {
            Id = -(_realtimeSequence + 1),
            SessionId = session.Id,
            AgentSessionId = session.Status.AgentRuntimeSessionId,
            Sequence = ++_realtimeSequence,
            Type = RuntimeEventTypes.SessionContextReset,
            PayloadJson = JSON.Serialize(new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["observedAt"] = now.ToString("o"),
            }),
            CreatedAt = now,
        }
    ];

    private IReadOnlyList<RuntimeEventEnvelope> BuildCompactionTranscriptEntries(
        AgentSession session,
        long? usedBefore,
        long? usedAfter,
        long? size,
        string? summary,
        DateTime now)
    {
        return
        [
            new()
            {
                Id = -(_realtimeSequence + 1),
                SessionId = session.Id,
                AgentSessionId = session.Status.AgentRuntimeSessionId,
                Sequence = ++_realtimeSequence,
                Type = "compaction",
                PayloadJson = BuildCompactionPayload("summary", usedBefore, usedAfter, size, summary, now),
                CreatedAt = now,
            },
            new()
            {
                Id = -(_realtimeSequence + 1),
                SessionId = session.Id,
                AgentSessionId = session.Status.AgentRuntimeSessionId,
                Sequence = ++_realtimeSequence,
                Type = "compaction_event",
                PayloadJson = BuildCompactionEventPayload("summary", usedBefore, usedAfter, size, summary, now),
                CreatedAt = now,
            }
        ];
    }

    private static string BuildCompactionPayload(
        string strategy,
        long? usedBefore,
        long? usedAfter,
        long? size,
        string? summary,
        DateTime now)
    {
        var payload = new Dictionary<string, object?>
        {
            ["strategy"] = strategy,
            ["contextWindowUsedBefore"] = usedBefore,
            ["contextWindowUsedAfter"] = usedAfter,
            ["contextWindowSize"] = size,
            ["recordedAt"] = now.ToString("o"),
        };
        if (!string.IsNullOrWhiteSpace(summary))
            payload["summary"] = summary;
        return JSON.Serialize(payload);
    }

    private static string BuildCompactionEventPayload(
        string strategy,
        long? usedBefore,
        long? usedAfter,
        long? size,
        string? summary,
        DateTime now)
    {
        var payload = new Dictionary<string, object?>
        {
            ["strategy"] = strategy,
            ["contextWindowUsedBefore"] = usedBefore,
            ["contextWindowUsedAfter"] = usedAfter,
            ["contextWindowSize"] = size,
            ["recordedAt"] = now.ToString("o"),
        };
        if (!string.IsNullOrWhiteSpace(summary))
            payload["summary"] = summary;
        return JSON.Serialize(payload);
    }

    private async Task PersistRecoveryAsync(
        AgentSession session,
        IReadOnlyList<AgentSessionEvent> events,
        IReadOnlyList<RuntimeEventEnvelope> transcriptEntries,
        string? operationId = null)
    {
        if (transcriptEntries.Count > 0)
        {
            var prefix = operationId ?? Guid.NewGuid().ToString("N");
            var pendingEvidence = session.Status.PendingTranscriptEvidence?.ToList() ?? [];
            pendingEvidence.AddRange(transcriptEntries.Select((entry, index) => new AgentSessionTranscriptEvidence(
                $"recovery:{prefix}:{index}",
                entry.AgentSessionId,
                entry.Type,
                entry.PayloadJson,
                entry.CreatedAt,
                "recovery")));
            session.Status = session.Status with { PendingTranscriptEvidence = pendingEvidence };
        }

        try
        {
            await _stateStore.SaveAsync(SessionId, session, events);
        }
        catch
        {
            // The recovery transition has
            // already mutated the live session. The store rolled back, so the
            // committed state and AgentSessionEvents rows are unchanged.
            // Quarantine the activation so the mutated in-memory session is
            // not salvaged on a later command; the next activation reloads
            // from storage. See CommitAsync for the same defense.
            _sessionReloadRequired = true;
            DeactivateOnIdle();
            throw;
        }
        // The recovery state, domain event, terminal result, and transcript
        // evidence are now durable together. A later retry can return the
        // stored result without sending a second runtime command, while the
        // evidence remains available after a restart until its own store
        // accepts it.
        _session = session;
        _cachedSummary = null;
        if (!await FlushPendingTranscriptEvidenceAsync(session, CancellationToken.None))
            EnsurePersistenceTimer();

        await FanOutRealtimeAsync(session, transcriptEntries, events);
    }

    private async Task<bool> FlushPendingTranscriptEvidenceAsync(AgentSession session, CancellationToken ct)
    {
        var evidence = session.Status.PendingTranscriptEvidence;
        if (evidence is null || evidence.Count == 0)
            return true;

        foreach (var item in evidence.ToArray())
        {
            var flush = new AgentSessionTranscriptFlush(
                StartNewTurn: false,
                Turn: new AgentSessionTranscriptTurnUpsert(
                    session.Id,
                    Sequence: 0,
                    PromptText: string.Empty,
                    PromptKind: item.PromptKind,
                    StartedAt: item.CreatedAt,
                    UpdatedAt: item.CreatedAt,
                    RuntimeSessionId: item.RuntimeSessionId),
                Parts:
                [
                    new AgentSessionTranscriptPartDelta(
                        ToTranscriptPartType(item.Type),
                        item.Id,
                        item.Id,
                        TextDelta: null,
                        PayloadJson: item.PayloadJson,
                        FirstSeenAt: item.CreatedAt,
                        LastSeenAt: item.CreatedAt,
                        RawEventCount: 1,
                        IsIdempotent: true),
                ]);
            try
            {
                await _transcriptStore.SaveAsync(flush, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to save durable transcript evidence for {SessionId}; evidence={EvidenceId}",
                    SessionId, item.Id);
                return false;
            }

            session.Status = session.Status with
            {
                PendingTranscriptEvidence = session.Status.PendingTranscriptEvidence!
                    .Where(candidate => !string.Equals(candidate.Id, item.Id, StringComparison.Ordinal))
                    .ToArray(),
            };
            await CommitAsync(session, []);
            _session = session;
        }

        return true;
    }

    private static string ToTranscriptPartType(string eventType) => eventType switch
    {
        RuntimeEventTypes.SessionLiveness => TranscriptPartTypes.Status,
        RuntimeEventTypes.SessionActivity => TranscriptPartTypes.SessionActivity,
        RuntimeEventTypes.SessionContextReset => TranscriptPartTypes.SessionContextReset,
        RuntimeEventTypes.TurnFailed => TranscriptPartTypes.Status,
        RuntimeEventTypes.Compaction => TranscriptPartTypes.Compaction,
        RuntimeEventTypes.ProviderRetry => TranscriptPartTypes.ProviderRetry,
        _ => eventType,
    };

    private AgentSessionRecoveryResult BuildRecoveryResult(
        AgentSession session,
        long? usedBefore,
        long? size,
        string operation,
        bool wasCompacted)
    {
        var usage = AgentSessionJsonHelper.Usage(session);
        return new AgentSessionRecoveryResult(
            session.Id,
            AgentSessionJsonHelper.ActivityName(session),
            usage.ContextWindowSize ?? size,
            usage.ContextWindowUsed ?? usedBefore,
            AgentSessionJsonHelper.ContextUsagePercent(usage.ContextWindowUsed ?? usedBefore, usage.ContextWindowSize ?? size),
            usedBefore,
            operation,
            wasCompacted);
    }

    public async Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendRuntimeEventsAsync(
        AppendAgentSessionRuntimeEventsCommand command)
    {
        var session = await GetRequiredAsync();
        if (command.WorkflowExecution is not null && command.SessionTurnId is not null)
            throw new InvalidOperationException("Runtime events cannot carry both Workflow execution and Session turn identity.");
        if (command.SessionTurnId is not null)
            ValidateSessionRuntimeEventTurnIds(session, command.RuntimeEvents, command.SessionTurnId);
        var hasWorkflowTurnForRuntime = (session.Status.Turns ?? []).Any(turn =>
            turn.WorkflowExecution is { } binding
            && string.Equals(binding.RuntimeSessionId, command.RuntimeSessionId, StringComparison.Ordinal));
        if (hasWorkflowTurnForRuntime && command.SessionTurnId is null)
        {
            if (command.WorkflowExecution is null)
                throw new InvalidOperationException("Workflow runtime events require the acknowledged Agent turn binding.");
            ValidateWorkflowRuntimeEventBinding(session, command.WorkflowExecution);
        }
        else if (command.WorkflowExecution is not null)
        {
            ValidateWorkflowRuntimeEventBinding(session, command.WorkflowExecution);
        }
        if (command.WorkflowExecution is { } workflowBinding)
            ValidateWorkflowRuntimeEventTurnIds(command.RuntimeEvents, workflowBinding);

        var events = await AppendEventsAsync(command.RuntimeEvents, command.RuntimeSessionId, requireCurrentRuntimeBinding: true);
        if (command.WorkflowExecution is { } binding)
        {
            var observations = BuildWorkflowRuntimeObservations(command.RuntimeEvents);
            if (observations.Count == 0)
                return events;
            if (!await FlushAsync(CancellationToken.None))
                throw new InvalidOperationException($"Agent session {SessionId} could not persist Workflow runtime observation.");
            if (_sessionWorkPort is not null)
            {
                foreach (var observation in observations)
                {
                    await _sessionWorkPort.ObserveAgentExecutionAsync(
                        binding,
                        observation.Kind,
                        observation.ReasonCode,
                        observation.Message,
                        observation.StopOperationId);
                }
            }
        }
        return events;
    }

    private static void ValidateWorkflowRuntimeEventTurnIds(
        IReadOnlyList<AgentSessionRuntimeEventInput> events,
        SessionWorkflowExecutionBinding binding)
    {
        foreach (var runtimeEvent in events)
        {
            var payload = SafeDeserialize(runtimeEvent.PayloadJson);
            if (!string.Equals(
                    AgentSessionJsonHelper.GetStringProp(payload, "turnId"),
                    binding.AgentTurnId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Workflow runtime event does not match the acknowledged Agent turn binding.");
            }
        }
    }

    private static void ValidateSessionRuntimeEventTurnIds(
        AgentSession session,
        IReadOnlyList<AgentSessionRuntimeEventInput> events,
        string turnId)
    {
        var turn = (session.Status.Turns ?? []).SingleOrDefault(candidate =>
            string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
        if (turn is null || turn.WorkflowExecution is not null)
            throw new InvalidOperationException("Session runtime events do not match a recorded Session follow-up turn.");

        foreach (var runtimeEvent in events)
        {
            var payload = SafeDeserialize(runtimeEvent.PayloadJson);
            if (!string.Equals(
                    AgentSessionJsonHelper.GetStringProp(payload, "turnId"),
                    turnId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Session runtime event does not match the recorded Session follow-up turn.");
            }
        }
    }

    private static IReadOnlyList<WorkflowRuntimeObservation> BuildWorkflowRuntimeObservations(
        IReadOnlyList<AgentSessionRuntimeEventInput> events)
    {
        foreach (var runtimeEvent in events)
        {
            if (runtimeEvent.Type != RuntimeEventTypes.TurnFailed)
                continue;
            var failedPayload = SafeDeserialize(runtimeEvent.PayloadJson);
            var unknown = string.Equals(
                AgentSessionJsonHelper.GetStringProp(failedPayload, "failureCategory"),
                "unknown",
                StringComparison.OrdinalIgnoreCase);
            return
            [
                new WorkflowRuntimeObservation(
                    unknown ? SessionWorkflowObservationKind.Unknown : SessionWorkflowObservationKind.Failed,
                    unknown ? "turn-unknown" : "turn-failed",
                    AgentSessionJsonHelper.GetStringProp(failedPayload, "failureReason")
                        ?? AgentSessionJsonHelper.GetStringProp(failedPayload, "message"),
                    AgentSessionJsonHelper.GetStringProp(failedPayload, "stopOperationId"))
            ];
        }

        foreach (var runtimeEvent in events)
        {
            if (runtimeEvent.Type != RuntimeEventTypes.SessionActivity)
                continue;

            var activityPayload = SafeDeserialize(runtimeEvent.PayloadJson);
            var activity = ParseActivity(activityPayload);
            var reason = AgentSessionJsonHelper.GetStringProp(activityPayload, "reason");
            var kind = activity switch
            {
                AgentSessionActivity.Idle => SessionWorkflowObservationKind.Idle,
                AgentSessionActivity.Unknown when string.Equals(reason, "runner-disconnected", StringComparison.Ordinal)
                    => SessionWorkflowObservationKind.Disconnected,
                AgentSessionActivity.Unknown => SessionWorkflowObservationKind.Unknown,
                _ => (SessionWorkflowObservationKind?)null,
            };
            if (kind is null)
                continue;
            return
            [
                new WorkflowRuntimeObservation(
                    kind.Value,
                    reason ?? (kind == SessionWorkflowObservationKind.Idle ? "session-idle" : "session-unknown"),
                    AgentSessionJsonHelper.GetStringProp(activityPayload, "failureReason")
                        ?? AgentSessionJsonHelper.GetStringProp(activityPayload, "message"),
                    AgentSessionJsonHelper.GetStringProp(activityPayload, "stopOperationId"))
            ];
        }
        return [];
    }

    private readonly record struct WorkflowRuntimeObservation(
        SessionWorkflowObservationKind Kind,
        string ReasonCode,
        string? Message,
        string? StopOperationId);

    public async Task<WorkflowAgentSessionInputReceipt> AcceptWorkflowInputAsync(
        AcceptWorkflowAgentSessionInputCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.InputDeliveryId)
            || string.IsNullOrWhiteSpace(command.Prompt)
            || string.IsNullOrWhiteSpace(command.WorkflowRunId)
            || string.IsNullOrWhiteSpace(command.TaskRunId)
            || string.IsNullOrWhiteSpace(command.WorkId)
            || string.IsNullOrWhiteSpace(command.RunnerId)
            || string.IsNullOrWhiteSpace(command.Runtime)
            || string.IsNullOrWhiteSpace(command.RuntimeSessionId))
        {
            throw new ArgumentException("Workflow input requires a complete execution identity.", nameof(command));
        }

        var session = await GetRequiredAsync();
        ValidateWorkflowInputTarget(session, command);

        var existingInput = (session.Status.Inputs ?? []).SingleOrDefault(input =>
            string.Equals(input.Id, command.InputDeliveryId, StringComparison.Ordinal));
        var existingTurn = (session.Status.Turns ?? []).SingleOrDefault(turn =>
            turn.InputIds.Contains(command.InputDeliveryId, StringComparer.Ordinal));

        SessionWorkflowExecutionBinding binding;
        if (existingInput is not null || existingTurn is not null)
        {
            if (existingInput is null || existingTurn?.WorkflowExecution is null
                || !string.Equals(existingInput.Text, command.Prompt, StringComparison.Ordinal)
                || !string.Equals(existingInput.Source, "workflow", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Workflow input delivery '{command.InputDeliveryId}' conflicts with persisted AgentSession state.");
            }
            binding = existingTurn.WorkflowExecution;
            var expected = binding with { AgentTurnId = existingTurn.Id };
            if (!Equals(binding, expected)
                || !WorkflowInputBindingMatches(binding, command, SessionId))
            {
                throw new InvalidOperationException(
                    $"Workflow input delivery '{command.InputDeliveryId}' conflicts with the frozen execution binding.");
            }
        }
        else
        {
            var turnId = $"turn_{Guid.NewGuid():N}";
            binding = new SessionWorkflowExecutionBinding(
                command.InputDeliveryId,
                command.WorkflowRunId,
                command.TaskRunId,
                command.WorkId,
                command.RunnerId,
                SessionId,
                turnId,
                command.Runtime,
                command.RuntimeSessionId);

            _ = session.RecordFollowupTurn(
                command.InputDeliveryId,
                turnId,
                command.Prompt,
                "workflow",
                Now());
            var turns = (session.Status.Turns ?? []).ToList();
            var turnIndex = turns.FindIndex(turn => string.Equals(turn.Id, turnId, StringComparison.Ordinal));
            if (turnIndex < 0)
                throw new InvalidOperationException("Workflow input did not create an Agent turn.");
            turns[turnIndex] = turns[turnIndex] with { WorkflowExecution = binding };
            session.Status = session.Status with { Turns = turns };
            _session = session;

            // The binding is the recovery source of truth. Persist it before
            // transcript projection or the cross-grain Workflow call.
            await _stateStore.SaveAsync(SessionId, session);

            var entries = await AppendEventsAsync(
                [new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionInput, command.PayloadJson)],
                command.RuntimeSessionId,
                requireCurrentRuntimeBinding: true);
            if (entries.Count != 1)
                throw new InvalidOperationException("Workflow input was not accepted by the AgentSession transcript.");
            _ = await FlushAsync(CancellationToken.None);
        }

        if (_sessionWorkPort is null)
            throw new InvalidOperationException("Workflow input binding port is unavailable.");

        var accepted = await _sessionWorkPort.BindAgentExecutionAsync(binding);
        return new WorkflowAgentSessionInputReceipt(
            command.InputDeliveryId,
            binding.AgentTurnId,
            accepted,
            binding.AgentSessionId);
    }

    public Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendSystemEventsAsync(AppendAgentSessionSystemEventsCommand command)
    {
        if (command.RuntimeEvents.Any(e => !string.Equals(e.Type, RuntimeEventTypes.SessionActivity, StringComparison.Ordinal)))
            throw new InvalidOperationException("System AgentSession events are limited to session.activity.");
        return AppendEventsAsync(command.RuntimeEvents, null, requireCurrentRuntimeBinding: false);
    }

    private static bool WorkflowInputBindingMatches(
        SessionWorkflowExecutionBinding binding,
        AcceptWorkflowAgentSessionInputCommand command,
        string sessionId) =>
        string.Equals(binding.InputDeliveryId, command.InputDeliveryId, StringComparison.Ordinal)
        && string.Equals(binding.WorkflowRunId, command.WorkflowRunId, StringComparison.Ordinal)
        && string.Equals(binding.TaskRunId, command.TaskRunId, StringComparison.Ordinal)
        && string.Equals(binding.WorkId, command.WorkId, StringComparison.Ordinal)
        && string.Equals(binding.RunnerId, command.RunnerId, StringComparison.Ordinal)
        && string.Equals(binding.AgentSessionId, sessionId, StringComparison.Ordinal)
        && string.Equals(binding.Runtime, command.Runtime, StringComparison.Ordinal)
        && string.Equals(binding.RuntimeSessionId, command.RuntimeSessionId, StringComparison.Ordinal);

    private static void ValidateWorkflowRuntimeEventBinding(
        AgentSession session,
        SessionWorkflowExecutionBinding binding)
    {
        var turn = (session.Status.Turns ?? []).SingleOrDefault(turn =>
            string.Equals(turn.Id, binding.AgentTurnId, StringComparison.Ordinal));
        if (turn?.WorkflowExecution is null
            || !Equals(turn.WorkflowExecution, binding))
        {
            throw new InvalidOperationException("Workflow runtime events do not match a frozen Agent turn binding.");
        }
    }

    private static void ValidateWorkflowInputTarget(
        AgentSession session,
        AcceptWorkflowAgentSessionInputCommand command)
    {
        if (!string.Equals(
                session.Metadata.Label(AgentSessionQueryMetadataKeys.SourceKind),
                "workflow",
                StringComparison.Ordinal)
            || !string.Equals(
                session.Metadata.Label(AgentSessionQueryMetadataKeys.WorkflowRunId),
                command.WorkflowRunId,
                StringComparison.Ordinal)
            || !string.Equals(session.Runtime.RunnerId, command.RunnerId, StringComparison.Ordinal)
            || !string.Equals(session.Runtime.Runtime, command.Runtime, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(session.Status.AgentRuntimeSessionId, command.RuntimeSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workflow input does not match the current AgentSession runtime binding.");
        }
    }

    public async Task<AppendTerminalCloseResult> AppendTerminalCloseAsync(AppendTerminalCloseCommand command)
    {
        var sourcePayload = AgentSessionJsonHelper.ParsePayloadOrEmpty(command.PayloadJson);
        var payload = JSON.Serialize(new Dictionary<string, object?>
        {
            ["activity"] = "idle",
            ["observedAt"] = command.RecordedAt.ToString("o"),
            ["recordedAt"] = command.RecordedAt.ToString("o"),
            ["operationId"] = command.DeliveryId,
            ["deliveryId"] = command.DeliveryId,
            ["status"] = command.Status,
            ["exitCode"] = command.ExitCode,
            ["failureReason"] = command.FailureReason,
            ["failureCategory"] = command.FailureCategory,
            ["agentJobId"] = AgentSessionJsonHelper.GetStringProp(sourcePayload, "agentJobId"),
        });
        var entries = await AppendEventsAsync(
            [new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionActivity, payload)],
            command.RuntimeSessionId,
            requireCurrentRuntimeBinding: !string.IsNullOrWhiteSpace(command.RuntimeSessionId));
        if (entries.Count == 0)
            return new AppendTerminalCloseResult(SessionId, command.DeliveryId, true);
        if (!await FlushAsync(CancellationToken.None))
            throw new InvalidOperationException($"Agent session {SessionId} could not persist terminal delivery {command.DeliveryId}");
        return new AppendTerminalCloseResult(SessionId, command.DeliveryId, false);
    }

    private async Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendEventsAsync(
        IReadOnlyList<AgentSessionRuntimeEventInput> runtimeEvents,
        string? runtimeSessionId,
        bool requireCurrentRuntimeBinding)
    {
        if (runtimeEvents.Count == 0) return [];

        var supportedEvents = runtimeEvents
            .Where(runtimeEvent => TranscriptAccumulator.EventTypes.Contains(runtimeEvent.Type))
            .ToList();
        foreach (var group in runtimeEvents
            .Where(runtimeEvent => !TranscriptAccumulator.EventTypes.Contains(runtimeEvent.Type))
            .GroupBy(runtimeEvent => runtimeEvent.Type, StringComparer.Ordinal))
        {
            _log.LogWarning(
                "AgentSessionGrain discarded unsupported transcript events for {SessionId}; type {EventType}, count {DiscardedEventCount}",
                SessionId,
                group.Key,
                group.Count());
        }
        if (supportedEvents.Count == 0) return [];
        runtimeEvents = supportedEvents;

        var session = await GetRequiredAsync();
        var turnStatusBefore = SnapshotNonLaunchTurnStatuses(session);
        if (requireCurrentRuntimeBinding
            && (string.IsNullOrWhiteSpace(runtimeSessionId)
                || !string.Equals(runtimeSessionId, session.Status.AgentRuntimeSessionId, StringComparison.Ordinal)))
        {
            _log.LogWarning(
                "AgentSessionGrain discarded runtime events because the reported runtime session binding was not current for {SessionId}; expected {ExpectedRuntimeSessionId}, reported {ReportedRuntimeSessionId}, count {DiscardedEventCount}",
                SessionId,
                session.Status.AgentRuntimeSessionId,
                string.IsNullOrWhiteSpace(runtimeSessionId) ? null : runtimeSessionId,
                runtimeEvents.Count);
            return [];
        }

        if (runtimeEvents.Any(e => e.Type == RuntimeEventTypes.SessionInput)
            && session.Status.Activity == AgentSessionActivity.Unknown)
            return [];

        // `session.input` is the canonical cross-source turn delimiter.
        // Before the new input reaches `TranscriptAccumulator` (which
        // would replace the active prompt), flush any prior pending
        // transcript data so rapid reused Workflow turns cannot merge
        // or overwrite an input that is still waiting for deferred
        // persistence. A failed flush rejects the new input and
        // retains the uncommitted prior accumulator state for the
        // existing later persistence attempt.
        var hasSessionInput = runtimeEvents.Any(e =>
            string.Equals(e.Type, RuntimeEventTypes.SessionInput, StringComparison.Ordinal));
        if (hasSessionInput && _transcript.HasPending)
        {
            if (!await FlushPendingTranscriptAsync(session, CancellationToken.None))
            {
                _log.LogWarning(
                    "AgentSessionGrain rejected session.input for {SessionId} because prior transcript data is pending and could not be persisted; retry once persistence succeeds",
                    SessionId);
                return [];
            }
            _session = session;
        }

        var now = Now();
        var events = new List<AgentSessionEvent>();
        if (runtimeEvents.Any(ShouldRecordActivity)
            && (session.Status.CurrentTurnEndedAt is null || runtimeEvents.Any(ResumesTurn)))
            events.AddRange(session.RecordActivity(now));
        _stateDirty = true;

        var previousHealth = _lastHealthStatus;
        var previousUsagePercent = _lastHealthPercent;
        var entries = new List<RuntimeEventEnvelope>();
        var supplementaryEntries = new List<RuntimeEventEnvelope>();
        var pendingConcurrencyReleases = new List<(
            string Token,
            string AgentId,
            string? PermitId,
            long Generation,
            string? WaiterId)>();
        foreach (var e in runtimeEvents)
        {
            var domainEvents = ApplyRuntimeEventToDomain(session, e, now);
            events.AddRange(domainEvents);
            SettleStopClaimFromRuntimeEvent(session, e);

            if (e.Type == RuntimeEventTypes.SessionInput)
            {
                var inputPayload = SafeDeserialize(e.PayloadJson);
                var operationId = AgentSessionJsonHelper.GetStringProp(inputPayload, "operationId");
                if (!string.IsNullOrWhiteSpace(operationId))
                    events.AddRange(session.MarkFollowupTurnExecuting(operationId, now));
            }

            if (e.Type == RuntimeEventTypes.SessionActivity)
            {
                var activityPayload = SafeDeserialize(e.PayloadJson);
                var activity = ParseActivity(activityPayload);
                var operationId = AgentSessionJsonHelper.GetStringProp(activityPayload, "operationId");
                if (!string.IsNullOrWhiteSpace(operationId)
                    && (activity == AgentSessionActivity.Idle || activity == AgentSessionActivity.Unknown))
                {
                    var pending = GetPendingFollowups(session);
                    var clearing = pending.FirstOrDefault(lease =>
                        string.Equals(lease.OperationId, operationId, StringComparison.Ordinal));
                    if (clearing?.ConcurrencyToken is not null && clearing.ConcurrencyAgentId is not null)
                        pendingConcurrencyReleases.Add((
                            clearing.ConcurrencyToken,
                            clearing.ConcurrencyAgentId,
                            clearing.ConcurrencyPermitId,
                            clearing.ConcurrencyGeneration,
                            clearing.ConcurrencyWaiterId));

                    if (!string.IsNullOrWhiteSpace(clearing?.TurnId))
                    {
                        events.AddRange(session.MarkFollowupTurnTerminal(
                            operationId,
                            ResolveFollowupTurnTerminalStatus(activityPayload),
                            ResolveFollowupTurnResult(activityPayload),
                            now));
                    }
                    else
                    {
                        SetPendingFollowups(session, pending.Where(lease =>
                            !string.Equals(lease.OperationId, operationId, StringComparison.Ordinal)).ToArray());
                    }
                }
            }

            var payloadJson = string.IsNullOrWhiteSpace(e.PayloadJson) ? "{}" : e.PayloadJson;
            var classifiedPayload = ClassifyTurnFailedPayload(session, e.Type, payloadJson, now);
            if (classifiedPayload is not null)
            {
                payloadJson = classifiedPayload;
                events.AddRange(EmitContextExhaustionDomain(session, payloadJson, now));
            }

            entries.Add(new RuntimeEventEnvelope
            {
                Id = -(_realtimeSequence + 1),
                SessionId = session.Id,
                AgentSessionId = session.Status.AgentRuntimeSessionId,
                Sequence = ++_realtimeSequence,
                Type = e.Type,
                PayloadJson = payloadJson,
                CreatedAt = now,
            });

            if (string.Equals(e.Type, "usage.updated", StringComparison.Ordinal))
            {
                var built = TryBuildContextHealthUpdate(session, now, previousHealth, previousUsagePercent);
                if (built.HasValue)
                {
                    var health = built.Value;
                    supplementaryEntries.Add(health.Envelope);
                    events.AddRange(health.DomainEvents);
                    previousHealth = health.NewStatus;
                    previousUsagePercent = health.NewPercent;
                }
            }
        }

        _lastHealthStatus = previousHealth;
        _lastHealthPercent = previousUsagePercent;

        var allEntries = entries.Concat(supplementaryEntries).ToList();

        var normalizedParts = _transcript.Accept(session, allEntries, now);
        session.PersistedActivitySummary = AgentSessionActivitySummaryReducer.Reduce(
            session.PersistedActivitySummary,
            normalizedParts);

        _pendingDomainEvents.AddRange(events);
        _session = session;
        _cachedSummary = null;

        EnsurePersistenceTimer();

        await FanOutRealtimeAsync(
            session,
            allEntries,
            events);

        // A terminal activity event fences the lease in the session store
        // before its generation is released in the agent gate.
        if (pendingConcurrencyReleases.Count > 0)
            await CommitAsync(session, []);

        foreach (var release in pendingConcurrencyReleases)
            await ReleaseFollowupConcurrencyPermitAsync(
                session,
                release.Token,
                release.AgentId,
                release.PermitId,
                release.Generation,
                release.WaiterId);

        await TryEmitFollowupTerminalDeliveriesAsync(session, turnStatusBefore);
        await EnsureStopRecoveryReminderAsync();

        return entries.Select(e => ToEventInfo(e)).ToList();
    }

    private static bool ShouldRecordActivity(AgentSessionRuntimeEventInput runtimeEvent)
    {
        if (!string.Equals(runtimeEvent.Type, RuntimeEventTypes.SessionInput, StringComparison.Ordinal))
            return true;

        try
        {
            var payload = JSON.DeserializeElement(runtimeEvent.PayloadJson);
            return !string.Equals(AgentSessionJsonHelper.GetStringProp(payload, "source"), "followup", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(AgentSessionJsonHelper.GetStringProp(payload, "operationId"));
        }
        catch
        {
            return true;
        }
    }

    private static bool ResumesTurn(AgentSessionRuntimeEventInput runtimeEvent) =>
        runtimeEvent.Type is RuntimeEventTypes.SessionInput
            or RuntimeEventTypes.MessageDelta
            or RuntimeEventTypes.ReasoningDelta
            or RuntimeEventTypes.ToolCallStarted
            or RuntimeEventTypes.ToolCallUpdated
            or RuntimeEventTypes.ToolCallCompleted;

    private string? ClassifyTurnFailedPayload(AgentSession session, string type, string payloadJson, DateTime now)
    {
        if (!string.Equals(type, RuntimeEventTypes.TurnFailed, StringComparison.Ordinal))
            return null;

        JsonElement payload;
        try
        {
            payload = JSON.DeserializeElement(payloadJson);
        }
        catch
        {
            return null;
        }
        if (payload.ValueKind != JsonValueKind.Object) return null;

        var usage = AgentSessionJsonHelper.Usage(session);
        var elapsed = session.Status.LastDataAt is { } last
            && session.Status.BoundAt is { } bound
            && last > bound
            ? last - bound
            : (TimeSpan?)null;

        var producedArtifacts = AgentSessionJsonHelper.GetBoolProp(payload, "producedArtifacts")
            ?? AgentSessionJsonHelper.GetBoolProp(payload, "producedExpectedOutput")
            ?? false;

        var result = ContextExhaustionClassifier.ClassifyTurnFailure(
            "failed",
            usage.ContextWindowUsed,
            usage.ContextWindowSize,
            elapsed,
            producedArtifacts);
        if (result.Category is null)
        {
            // Try the rapid-completion heuristic for successful closes that
            // look suspiciously fast and did not produce expected output.
            result = ContextExhaustionClassifier.ClassifyRapidCompletion(
                "failed",
                AgentSessionJsonHelper.ContextUsagePercent(usage.ContextWindowUsed, usage.ContextWindowSize),
                elapsed,
                producedArtifacts);
            if (result.Category is null) return null;
        }

        return ContextExhaustionClassifier.ApplyToPayload(payloadJson, result) ?? payloadJson;
    }

    private IReadOnlyList<AgentSessionEvent> EmitContextExhaustionDomain(AgentSession session, string payloadJson, DateTime now)
    {
        JsonElement payload;
        try
        {
            payload = JSON.DeserializeElement(payloadJson);
        }
        catch
        {
            return [];
        }
        if (payload.ValueKind != JsonValueKind.Object) return [];
        var failureCategory = AgentSessionJsonHelper.GetStringProp(payload, "failureCategory");
        if (!string.Equals(failureCategory, ContextExhaustionClassifier.ContextExhaustionCategory, StringComparison.Ordinal)
            && !string.Equals(failureCategory, ContextExhaustionClassifier.SuspectedContextExhaustionCategory, StringComparison.Ordinal))
            return [];
        var percent = AgentSessionJsonHelper.GetDoubleProp(payload, "contextUsagePercent");
        var usage = AgentSessionJsonHelper.Usage(session);
        return session.RecordContextExhaustion(
            failureCategory,
            percent,
            usage.ContextWindowUsed,
            usage.ContextWindowSize,
            now);
    }

    private readonly record struct ContextHealthDecision(
        RuntimeEventEnvelope Envelope,
        IReadOnlyList<AgentSessionEvent> DomainEvents,
        string? NewStatus,
        double? NewPercent);

    private ContextHealthDecision? TryBuildContextHealthUpdate(
        AgentSession session,
        DateTime now,
        string? previousStatus,
        double? previousPercent)
    {
        var usage = AgentSessionJsonHelper.Usage(session);
        var percent = AgentSessionJsonHelper.ContextUsagePercent(usage.ContextWindowUsed, usage.ContextWindowSize);
        if (percent is null) return null;

        var newStatus = ContextHealthClassifier.Classify(percent);
        if (newStatus is null) return null;

        if (!ContextHealthClassifier.ShouldEmitUpdate(previousStatus, previousPercent, percent))
            return null;

        var payload = JSON.Serialize(new Dictionary<string, object?>
        {
            ["healthStatus"] = newStatus,
            ["contextWindowSize"] = usage.ContextWindowSize,
            ["contextWindowUsed"] = usage.ContextWindowUsed,
            ["contextUsagePercent"] = percent,
            ["recordedAt"] = now.ToString("o"),
        });

        var envelope = new RuntimeEventEnvelope
        {
            Id = -(_realtimeSequence + 1),
            SessionId = session.Id,
            AgentSessionId = session.Status.AgentRuntimeSessionId,
            Sequence = ++_realtimeSequence,
            Type = "context_health_update",
            PayloadJson = payload,
            CreatedAt = now,
        };

        var domainEvents = session.RecordContextHealthUpdate(
            newStatus,
            percent,
            usage.ContextWindowUsed,
            usage.ContextWindowSize,
            now);

        return new ContextHealthDecision(envelope, domainEvents, newStatus, percent);
    }

    private async Task FanOutRealtimeAsync(
        AgentSession session,
        IReadOnlyList<RuntimeEventEnvelope> entries,
        IReadOnlyList<AgentSessionEvent> domainEvents)
    {
        if (entries.Count == 0) return;

        foreach (var row in entries)
        {
            if (!TranscriptAccumulator.EventTypes.Contains(row.Type))
                continue;

            JsonElement payload;
            try
            {
                payload = JSON.DeserializeElement(row.PayloadJson);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "AgentSessionGrain failed to deserialise transcript payload for {Type} on {SessionId}",
                    row.Type, session.Id);
                continue;
            }

            var envelope = new TranscriptEnvelope(
                Id: row.Id.ToString(),
                SessionId: row.SessionId,
                RuntimeSessionId: row.AgentSessionId,
                Runtime: session.Runtime.Runtime,
                Sequence: row.Sequence,
                Type: row.Type,
                Payload: payload,
                CreatedAt: row.CreatedAt.ToString("o"));

            try
            {
                await _transcriptPublisher.PublishAsync(envelope, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "AgentSessionGrain transcript publish failed for {Type} on {SessionId}",
                    row.Type, session.Id);
            }
        }
    }

    private void EnsurePersistenceTimer()
    {
        _persistTimer ??= this.RegisterGrainTimer(
            _ => PersistCallback(),
            PersistTimerDueTime,
            PersistTimerDueTime);
    }

    private async Task PersistCallback()
    {
        if (_session is null || (!_stateDirty
            && !_transcript.HasPending
            && !(_session.Status.PendingTranscriptEvidence?.Count > 0)))
        {
            DisposePersistTimer();
            return;
        }

        var cycleId = _persistenceObserver.StartCycle(SessionId);
        try
        {
            var success = await FlushAsync(CancellationToken.None);
            _persistenceObserver.Report(new AgentSessionPersistenceResult(
                SessionId,
                cycleId,
                success
                    ? AgentSessionPersistenceOutcome.Succeeded
                    : AgentSessionPersistenceOutcome.TranscriptFailed));
            if (success)
                DisposePersistTimer();
        }
        catch
        {
            _persistenceObserver.Report(new AgentSessionPersistenceResult(
                SessionId,
                cycleId,
                AgentSessionPersistenceOutcome.StateFailed));
            throw;
        }
    }

    /// <summary>
    /// Flush only the pending transcript data (and any pending recovery
    /// evidence) without committing state or domain events. Used as the
    /// deterministic fence before accepting a new <c>session.input</c>
    /// so a rapid reused Workflow turn cannot overwrite an input that
    /// is still waiting for deferred persistence. On failure the prior
    /// pending accumulator state is retained for the existing later
    /// persistence attempt — callers can react by rejecting the new
    /// input and waiting for the next timer tick or a forced flush.
    /// </summary>
    private async Task<bool> FlushPendingTranscriptAsync(AgentSession session, CancellationToken ct)
    {
        if (!await FlushPendingTranscriptEvidenceAsync(session, ct))
            return false;

        if (!_transcript.HasPending)
            return true;

        var now = Now();
        var transcript = _transcript.BuildFlush(session, now);
        if (transcript is null)
            return true;

        try
        {
            await _transcriptStore.SaveAsync(transcript, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "AgentSessionGrain failed to flush pending transcript before accepting session.input for {SessionId}; parts={PartCount}",
                SessionId, transcript.Parts.Count);
            return false;
        }
        _transcript.CommitFlush();
        return true;
    }

    private async Task<bool> FlushAsync(CancellationToken ct)
    {
        var session = _session;
        if (session is null) return true;
        var hasPendingPersistence = _stateDirty
            || _transcript.HasPending
            || session.Status.PendingTranscriptEvidence?.Count > 0;
        if (!hasPendingPersistence)
            return true;

        // A prior event-aware save on this activation failed and quarantined
        // it; do not attempt another flush from the same dirty state.
        if (_sessionReloadRequired)
            throw new InvalidOperationException($"Agent session {SessionId} must reload after a failed event-aware save");

        if (!await FlushPendingTranscriptEvidenceAsync(session, ct))
            return false;

        var now = Now();
        var transcript = _transcript.BuildFlush(session, now);

        if (_stateDirty)
        {
            var pendingEvents = _pendingDomainEvents.Count == 0
                ? Array.Empty<AgentSessionEvent>()
                : _pendingDomainEvents.ToArray();
            try
            {
                await _stateStore.SaveAsync(SessionId, session, pendingEvents, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to save state for {SessionId}",
                    SessionId);
                // The state/event transaction rolled back, but the live
                // session and _pendingDomainEvents already absorbed the
                // runtime activity. Quarantine the activation so a later
                // command cannot persist the dirty state without the matching
                // AgentSessionEvents rows. See CommitAsync for the same defense.
                _sessionReloadRequired = true;
                DeactivateOnIdle();
                throw;
            }
            // The state/event transaction committed atomically. The domain
            // events are now durable rows; clear them so a subsequent
            // transcript-only retry cannot re-append them. Splitting the two
            // retry states means a transcript failure no longer duplicates
            // already-committed lifecycle events on the next flush.
            _pendingDomainEvents.Clear();
            _stateDirty = false;
        }

        if (transcript is null)
            return true;

        try
        {
            await _transcriptStore.SaveAsync(transcript, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "AgentSessionGrain failed to save transcript for {SessionId}; parts={PartCount}",
                SessionId, transcript.Parts.Count);
            // State/events already committed; only the transcript needs
            // retry. _transcript keeps the un-committed flush, so the next
            // PersistCallback re-attempts just the transcript.
            return false;
        }
        _transcript.CommitFlush();
        return true;
    }

    private void DisposePersistTimer()
    {
        _persistTimer?.Dispose();
        _persistTimer = null;
    }

    public async Task EnsureRuntimeSessionPresentAsync()
    {
        var session = await GetRequiredAsync();
        EnsureRuntimeSessionPresent(session);
    }

    public async Task RunnerDisconnectedAsync()
    {
        var session = await GetRequiredAsync();
        if (session.Status.Activity != AgentSessionActivity.Active) return;
        var turn = (session.Status.Turns ?? []).LastOrDefault(candidate =>
            candidate.Status == AgentTurnStatus.Executing
            && candidate.WorkflowExecution is { } binding
            && string.Equals(binding.AgentSessionId, session.Id, StringComparison.Ordinal)
            && string.Equals(binding.RunnerId, session.Runtime.RunnerId, StringComparison.Ordinal)
            && string.Equals(binding.Runtime, session.Runtime.Runtime, StringComparison.Ordinal)
            && string.Equals(
                binding.RuntimeSessionId,
                session.Status.AgentRuntimeSessionId,
                StringComparison.Ordinal));
        var now = Now();
        session.SetActivity(AgentSessionActivity.Unknown, now);
        var payload = JSON.Serialize(new { activity = "unknown", observedAt = now.ToString("o"), reason = "runner-disconnected" });
        await AppendEventsAsync([new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionActivity, payload)], session.Status.AgentRuntimeSessionId, true);
        if (turn?.WorkflowExecution is not null)
        {
            if (!await FlushAsync(CancellationToken.None))
                throw new InvalidOperationException($"Agent session {SessionId} could not persist a runner-disconnected observation.");
            await ObserveWorkflowExecutionAsync(
                turn,
                SessionWorkflowObservationKind.Disconnected,
                "runner-disconnected");
        }
    }

    private async Task<AgentSession> GetRequiredAsync()
    {
        if (_sessionReloadRequired)
            throw new InvalidOperationException($"Agent session {SessionId} must reload after a failed event-aware save");
        if (_session is not null) return _session;

        _session = await _stateStore.LoadAsync(SessionId);
        if (_session is null)
            throw new InvalidOperationException($"Agent session {SessionId} does not exist.");
        _session.PersistedActivitySummary = (_session.PersistedActivitySummary ?? AgentSessionActivitySummaryState.Empty).Normalize();
        return _session;
    }

    private async Task CommitAsync(AgentSession session, IReadOnlyList<AgentSessionEvent> events)
    {
        try
        {
            await _stateStore.SaveAsync(SessionId, session, events);
        }
        catch
        {
            // The store rolled back its transaction, but the transitions have
            // already mutated the live session object (Runtime/Status/Usage).
            // A retry on this activation could bind/record the mutation again
            // with zero events and persist it through the no-events path,
            // losing the AgentSessionEvents row. Quarantine the activation so
            // GetRequiredAsync() rejects further work until it reloads.
            _sessionReloadRequired = true;
            DeactivateOnIdle();
            throw;
        }
        _session = session;
    }

    private async Task<AgentSessionInfo> ToInfoAsync(AgentSession s)
    {
        var eventSummary = await LoadEventSummaryAsync(s.Id);
        var usage = AgentSessionJsonHelper.Usage(s);
        return new AgentSessionInfo(
            s.Id,
            s.Runtime.RunnerId,
            s.Status.AgentRuntimeSessionId,
            AgentSessionJsonHelper.ActivityName(s),
            s.Settings.Model,
            s.Runtime.WorkDir,
            s.Status.CreatedAt.ToString("o"),
            s.Status.BoundAt?.ToString("o"),
            s.Status.LastDataAt?.ToString("o"),
            eventSummary.ResolvedModel,
            usage.InputTokens,
            usage.OutputTokens,
            usage.TotalTokens,
            usage.CachedReadTokens,
            usage.ThoughtTokens,
            usage.CostAmount,
            usage.CostCurrency,
            usage.ContextWindowUsed,
            usage.ContextWindowSize,
            eventSummary.FailureCategory,
            eventSummary.ToolCallCount,
            eventSummary.ToolErrorCount,
            s.Runtime.Runtime,
            usage.CachedWriteTokens,
            s.BindingEpoch,
            s.ActivitySummary.LastTerminalStatus ?? eventSummary.LastTerminalStatus,
            AgentWorkInterruptionProjection.Latest(s.Status.InterruptionHistory),
            s.Status.InterruptionHistory,
            eventSummary.AppliedReasoningEffort);
    }

    private async Task<AgentSessionTranscriptSummary> LoadEventSummaryAsync(string sessionId)
    {
        if (_cachedSummary is not null)
            return _cachedSummary;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var turns = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .ToListAsync();
        if (turns.Count == 0)
            return _cachedSummary = AgentSessionTranscriptSummary.Empty;

        var turnSequenceByTurnId = turns.ToDictionary(t => t.Id, t => t.Sequence);
        var turnIds = turns.Select(t => t.Id).ToList();
        var currentRuntimeSessionId = _session?.Status.AgentRuntimeSessionId;

        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(e => turnIds.Contains(e.TurnId))
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToListAsync();

        var events = parts
            .Where(part => string.IsNullOrWhiteSpace(currentRuntimeSessionId)
                || turns.FirstOrDefault(t => t.Id == part.TurnId) is { } t
                    && string.Equals(t.RuntimeSessionId, currentRuntimeSessionId, StringComparison.Ordinal))
            .Select(part => new TranscriptSummaryEvent(
                TurnSequence: turnSequenceByTurnId.GetValueOrDefault(part.TurnId, 0),
                Sequence: part.Sequence,
                PartId: part.Id.ToString(),
                Type: part.Type,
                PayloadJson: part.PayloadJson));

        return _cachedSummary = TranscriptEventSummaryProjector.Summarize(events);
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;

    private static AgentSessionRuntimeEventInfo ToEventInfo(RuntimeEventEnvelope e) => new(
        e.Id.ToString(),
        e.SessionId,
        e.AgentSessionId,
        e.Sequence,
        e.Type,
        e.PayloadJson,
        e.CreatedAt.ToString("o"));

    private static IReadOnlyList<AgentSessionEvent> ApplyRuntimeEventToDomain(
        AgentSession session,
        AgentSessionRuntimeEventInput runtimeEvent,
        DateTime now)
    {
        var payload = JSON.DeserializeElement(runtimeEvent.PayloadJson);
        var operationId = AgentSessionJsonHelper.GetStringProp(payload, "operationId");
        return runtimeEvent.Type switch
        {
            RuntimeEventTypes.SessionInput when HasPendingFollowupOperation(session, operationId) => session.SetActivity(
                session.Status.Activity == AgentSessionActivity.Unknown
                    ? AgentSessionActivity.Unknown
                    : AgentSessionActivity.Active,
                now),
            RuntimeEventTypes.SessionActivity when HasPendingFollowupOperation(session, operationId) => session.SetActivity(
                ParseActivity(payload),
                now),
            RuntimeEventTypes.SessionInput => DriveNonLaunchTurnLifecycle(session, runtimeEvent, now),
            RuntimeEventTypes.SessionActivity => DriveTerminalActivityLifecycle(session, runtimeEvent, payload, now),
            RuntimeEventTypes.SessionLiveness => session.RecordActivity(now),
            RuntimeEventTypes.UsageUpdated => session.ApplyUsage(
                AgentSessionJsonHelper.GetLongProp(payload, "inputTokens"),
                AgentSessionJsonHelper.GetLongProp(payload, "outputTokens"),
                AgentSessionJsonHelper.GetLongProp(payload, "totalTokens"),
                AgentSessionJsonHelper.GetLongProp(payload, "cachedReadTokens"),
                AgentSessionJsonHelper.GetLongProp(payload, "thoughtTokens"),
                AgentSessionJsonHelper.GetCostAmount(payload),
                AgentSessionJsonHelper.GetCostCurrency(payload),
                AgentSessionJsonHelper.GetContextWindowUsed(payload),
                AgentSessionJsonHelper.GetContextWindowSize(payload),
                now,
                AgentSessionJsonHelper.GetLongProp(payload, "cachedWriteTokens")),
            RuntimeEventTypes.ModelResolved => session.ResolveModel(
                AgentSessionJsonHelper.GetStringProp(payload, "resolvedModel"),
                now),
            _ => []
        };
    }

    private static JsonElement SafeDeserialize(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return default;
        try
        {
            return JSON.DeserializeElement(payloadJson);
        }
        catch
        {
            return default;
        }
    }

    private static AgentTurnStatus ResolveFollowupTurnTerminalStatus(JsonElement payload)
    {
        var status = AgentSessionJsonHelper.GetStringProp(payload, "status")?.ToLowerInvariant();
        var failureCategory = AgentSessionJsonHelper.GetStringProp(payload, "failureCategory");
        return status switch
        {
            "failed" => AgentTurnStatus.Failed,
            "cancelled" => AgentTurnStatus.Cancelled,
            "unknown" => AgentTurnStatus.Unknown,
            _ => string.Equals(failureCategory, "unknown", StringComparison.OrdinalIgnoreCase)
                ? AgentTurnStatus.Unknown
                : AgentTurnStatus.Completed,
        };
    }

    private static AgentTurnResult? ResolveFollowupTurnResult(JsonElement payload)
    {
        var message = AgentSessionJsonHelper.GetStringProp(payload, "message");
        var output = AgentSessionJsonHelper.GetStringProp(payload, "output");
        var failureReason = AgentSessionJsonHelper.GetStringProp(payload, "failureReason");
        var failureCategory = AgentSessionJsonHelper.GetStringProp(payload, "failureCategory");
        return message is null && output is null && failureReason is null && failureCategory is null
            ? null
            : new AgentTurnResult(message, output, failureReason, failureCategory);
    }

    private static void SettleStopClaimFromRuntimeEvent(
        AgentSession session,
        AgentSessionRuntimeEventInput runtimeEvent)
    {
        if (runtimeEvent.Type != RuntimeEventTypes.SessionActivity)
            return;

        var payload = JSON.DeserializeElement(runtimeEvent.PayloadJson);
        var terminal = MapTerminalActivityToTurnStatus(
            ParseActivity(payload),
            AgentSessionJsonHelper.GetStringProp(payload, "status"),
            !string.IsNullOrWhiteSpace(AgentSessionJsonHelper.GetStringProp(payload, "stopOperationId")));
        if (terminal is null
            || !TryResolveTurnId(runtimeEvent.PayloadJson, out var turnId))
            return;

        var pending = session.Status.PendingStop;
        if (pending is { IsActive: true }
            && string.Equals(pending.TurnId, turnId, StringComparison.Ordinal))
        {
            session.CompleteTurnStop(turnId, pending.OperationId);
        }
    }

    private static IReadOnlyList<AgentSessionEvent> DriveNonLaunchTurnLifecycle(
        AgentSession session,
        AgentSessionRuntimeEventInput runtimeEvent,
        DateTime now)
    {
        if (TryResolveTurnId(runtimeEvent.PayloadJson, out var payloadTurnId))
        {
            var turn = session.Status.Turns is { Count: > 0 } turns
                ? turns.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, payloadTurnId, StringComparison.Ordinal))
                : null;
            var current = FindCurrentNonLaunchTurn(session);
            if (turn is null
                || !string.IsNullOrWhiteSpace(turn.JobId)
                || current is null
                || !string.Equals(current.Id, turn.Id, StringComparison.Ordinal))
            {
                return [];
            }
            return session.MarkTurnExecuting(turn.Id, now);
        }
        var events = new List<AgentSessionEvent>(session.SetActivity(
            session.Status.Activity == AgentSessionActivity.Unknown
                ? AgentSessionActivity.Unknown
                : AgentSessionActivity.Active,
            now));
        events.AddRange(MarkCurrentNonLaunchTurnExecuting(session, now));
        return events;
    }

    private static IReadOnlyList<AgentSessionEvent> DriveTerminalActivityLifecycle(
        AgentSession session,
        AgentSessionRuntimeEventInput runtimeEvent,
        JsonElement payload,
        DateTime now)
    {
        var status = AgentSessionJsonHelper.GetStringProp(payload, "status");
        var activity = ParseActivity(payload);
        if (activity == AgentSessionActivity.Active)
            return session.SetActivity(activity, now);
        var terminal = MapTerminalActivityToTurnStatus(
            activity,
            status,
            !string.IsNullOrWhiteSpace(AgentSessionJsonHelper.GetStringProp(payload, "stopOperationId")));
        if (terminal is null)
            return session.SetActivity(activity, now);
        if (TryResolveTurnId(runtimeEvent.PayloadJson, out var payloadTurnId))
        {
            var turn = session.Status.Turns is { Count: > 0 } turns
                ? turns.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, payloadTurnId, StringComparison.Ordinal))
                : null;
            if (turn is null
                || !string.IsNullOrWhiteSpace(turn.JobId)
                || !IsCurrentNonLaunchTurn(session, turn))
            {
                return [];
            }
            return session.MarkTurnTerminal(turn.Id, terminal.Value, null, now);
        }
        var events = new List<AgentSessionEvent>(session.SetActivity(activity, now));
        events.AddRange(MarkCurrentNonLaunchTurnTerminal(session, terminal.Value, now));
        return events;
    }

    private static AgentTurnStatus? MapTerminalActivityToTurnStatus(
        AgentSessionActivity activity,
        string? status,
        bool stopConfirmed)
    {
        if (activity == AgentSessionActivity.Active)
            return null;
        if (activity == AgentSessionActivity.Unknown)
            return AgentTurnStatus.Unknown;
        if (stopConfirmed)
            return AgentTurnStatus.Cancelled;
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            return AgentTurnStatus.Failed;
        return AgentTurnStatus.Completed;
    }

    private static IReadOnlyList<AgentSessionEvent> MarkCurrentNonLaunchTurnExecuting(
        AgentSession session,
        DateTime now)
    {
        var current = FindCurrentNonLaunchTurn(session);
        return current is null ? [] : session.MarkTurnExecuting(current.Id, now);
    }

    private static IReadOnlyList<AgentSessionEvent> MarkCurrentNonLaunchTurnTerminal(
        AgentSession session,
        AgentTurnStatus terminal,
        DateTime now)
    {
        var current = FindCurrentNonLaunchTurn(session);
        return current is null ? [] : session.MarkTurnTerminal(current.Id, terminal, null, now);
    }

    private static AgentTurnRecord? FindCurrentNonLaunchTurn(AgentSession session)
    {
        var turns = session.Status.Turns ?? [];
        for (var index = turns.Count - 1; index >= 0; index--)
        {
            var turn = turns[index];
            if (!string.IsNullOrWhiteSpace(turn.JobId))
                continue;
            if (turn.Status is AgentTurnStatus.Completed
                or AgentTurnStatus.Failed
                or AgentTurnStatus.Cancelled
                or AgentTurnStatus.Unknown)
                continue;
            return turn;
        }
        return null;
    }

    private static bool IsCurrentNonLaunchTurn(AgentSession session, AgentTurnRecord turn)
    {
        if (turn.Status is AgentTurnStatus.Completed
            or AgentTurnStatus.Failed
            or AgentTurnStatus.Cancelled)
        {
            return false;
        }

        var turns = session.Status.Turns ?? [];
        var latestNonLaunch = turns.LastOrDefault(candidate => string.IsNullOrWhiteSpace(candidate.JobId));
        return latestNonLaunch is not null
            && string.Equals(latestNonLaunch.Id, turn.Id, StringComparison.Ordinal);
    }

    private static bool TryResolveTurnId(string payloadJson, out string turnId)
    {
        try
        {
            var payload = JSON.DeserializeElement(payloadJson);
            var id = AgentSessionJsonHelper.GetStringProp(payload, "turnId");
            if (!string.IsNullOrWhiteSpace(id))
            {
                turnId = id;
                return true;
            }
        }
        catch
        {
        }
        turnId = string.Empty;
        return false;
    }

    private static AgentSessionActivity ParseActivity(JsonElement payload) =>
        AgentSessionJsonHelper.GetStringProp(payload, "activity")?.ToLowerInvariant() switch
        {
            "active" => AgentSessionActivity.Active,
            "unknown" => AgentSessionActivity.Unknown,
            _ => AgentSessionActivity.Idle,
        };

    private static bool HasPendingFollowupOperation(AgentSession session, string? operationId) =>
        !string.IsNullOrWhiteSpace(operationId)
        && GetPendingFollowups(session).Any(lease =>
            string.Equals(lease.OperationId, operationId, StringComparison.Ordinal));

    public async Task<EnsureInitialLaunchResult> EnsureInitialLaunchAsync(EnsureInitialLaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.InputId))
            throw new ArgumentException("Input id is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.TurnId))
            throw new ArgumentException("Turn id is required.", nameof(command));
        if (string.IsNullOrEmpty(command.Prompt)
            && (command.Attachments is null || command.Attachments.Count == 0))
            throw new ArgumentException(
                "Prompt is required unless at least one attachment is accepted.",
                nameof(command));
        if (string.IsNullOrWhiteSpace(command.JobId))
            throw new ArgumentException("Job id is required.", nameof(command));

        bool alreadyPersisted = false;
        if (_session is null)
        {
            RejectIfReloadRequired();
            _session = CreateSession(new OpenAgentSessionCommand(
                RunnerId: string.Empty,
                AgentRuntime: command.Runtime ?? AgentConfigSchema.OpenCodeRuntime,
                WorkDir: command.WorkDir,
                Metadata: command.Metadata,
                Definition: command.Definition,
                AgentSessionStartup: command.AgentSessionStartup,
                LaunchVisibility: command.LaunchVisibility));
        }
        else
        {
            CheckInputsAndTurns(command, out alreadyPersisted);
        }

        if (!alreadyPersisted)
        {
            _ = _session.EnsureInitialLaunch(
                inputId: command.InputId,
                turnId: command.TurnId,
                prompt: command.Prompt,
                source: command.Source,
                jobId: command.JobId,
                now: Now(),
                attachments: command.Attachments,
                provenance: command.Provenance,
                startupContext: command.StartupContext);
        }

        await _stateStore.SaveAsync(SessionId, _session);
        _connections.RegisterSession(_session.Runtime.RunnerId, SessionId);

        return new EnsureInitialLaunchResult(
            SessionId: SessionId,
            InputId: command.InputId,
            TurnId: command.TurnId,
            AlreadyPersisted: alreadyPersisted);
    }

    public async Task<EnsureParentLinkResult> EnsureParentLinkAsync(EnsureParentLinkCommand command)
    {
        throw new InvalidOperationException("Parent link attachment requires the fence receipt protocol.");
    }

    public async Task<ApplyParentLinkAttachResult> ApplyParentLinkAttachAsync(
        ApplyParentLinkAttachCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.AttachedRevision <= 0
            || string.IsNullOrWhiteSpace(command.CommandId)
            || string.IsNullOrWhiteSpace(command.EdgeId)
            || string.IsNullOrWhiteSpace(command.ParentSessionId)
            || string.IsNullOrWhiteSpace(command.ParentAgentId)
            || string.IsNullOrWhiteSpace(command.ChildLaunchJobId)
            || string.IsNullOrWhiteSpace(command.ProjectId)
            || command.ExpectedBindingEpoch <= 0
            || string.IsNullOrWhiteSpace(command.BindingUseReceiptId)
            || command.ExpectedLinkState != SessionTreeExpectedLinkState.Absent)
        {
            return new ApplyParentLinkAttachResult(
                SessionTreeAttachMutationState.Rejected,
                RejectionReason: "parent_link_attach_identity_invalid");
        }

        var session = await GetRequiredAsync();
        if (!string.Equals(
                session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId),
                command.ProjectId,
                StringComparison.Ordinal))
        {
            return new ApplyParentLinkAttachResult(
                SessionTreeAttachMutationState.ReconciliationRequired,
                RejectionReason: "parent_link_project_mismatch");
        }

        if (session.ParentLink is { } existing)
        {
            var replayMatches = existing.EdgeId == command.EdgeId
                && existing.ParentSessionId == command.ParentSessionId
                && existing.ParentAgentId == command.ParentAgentId
                && existing.ChildLaunchJobId == command.ChildLaunchJobId
                && existing.State == SessionParentLinkState.Attached
                && existing.AttachedRevision == command.AttachedRevision
                && existing.AttachCommandId == command.CommandId
                && existing.ParentWorkDir == command.ExpectedWorkDir
                && existing.ParentRunnerId == command.ExpectedRunnerId
                && existing.ParentRuntime == command.ExpectedRuntime
                && existing.ParentRuntimeSessionId == command.ExpectedRuntimeSessionId
                && existing.BindingEpoch == command.ExpectedBindingEpoch
                && existing.BindingUseReceiptId == command.BindingUseReceiptId
                && existing.ExpectedLinkState == command.ExpectedLinkState;
            return replayMatches
                ? new ApplyParentLinkAttachResult(
                    SessionTreeAttachMutationState.Attached,
                    existing,
                    AttachReceipt(command))
                : new ApplyParentLinkAttachResult(
                    SessionTreeAttachMutationState.ReconciliationRequired,
                    RejectionReason: "parent_link_identity_mismatch");
        }

        session.ParentLink = new SessionParentLink(
            command.EdgeId,
            command.ParentSessionId,
            command.ParentAgentId,
            command.ChildLaunchJobId,
            _timeProvider.GetUtcNow(),
            command.AttachedRevision,
            SessionParentLinkState.Attached,
            AttachCommandId: command.CommandId,
            ParentWorkDir: command.ExpectedWorkDir,
            ParentRunnerId: command.ExpectedRunnerId,
            ParentRuntime: command.ExpectedRuntime,
            ParentRuntimeSessionId: command.ExpectedRuntimeSessionId,
            BindingEpoch: command.ExpectedBindingEpoch,
            BindingUseReceiptId: command.BindingUseReceiptId,
            ExpectedLinkState: command.ExpectedLinkState);
        await CommitAsync(session, []);
        return new ApplyParentLinkAttachResult(
            SessionTreeAttachMutationState.Attached,
            session.ParentLink,
            AttachReceipt(command));
    }

    public async Task<AcquireChildAttachBindingResult> AcquireChildAttachBindingAsync(
        AcquireChildAttachBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = await GetRequiredAsync();
        var projectId = session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId);
        if (!string.Equals(command.ParentSessionId, SessionId, StringComparison.Ordinal)
            || !string.Equals(command.ProjectId, projectId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(command.ParentAgentId)
            || command.ExpectedBindingEpoch <= 0)
        {
            return new AcquireChildAttachBindingResult(
                SessionTreeBindingAcquireState.BindingChanged,
                RejectionReason: "parent_binding_changed");
        }

        var receipts = session.BindingUseReceipts?.ToList() ?? [];
        var existing = receipts.FirstOrDefault(item =>
            item.CommandId == command.CommandId && item.EdgeId == command.EdgeId);
        if (existing is not null)
        {
            return BindingUseMatches(existing, command)
                && existing.State == SessionTreeBindingUseState.Held
                ? new AcquireChildAttachBindingResult(SessionTreeBindingAcquireState.AlreadyAcquired, existing)
                : new AcquireChildAttachBindingResult(
                    SessionTreeBindingAcquireState.ReconciliationRequired,
                    RejectionReason: "parent_binding_use_mismatch");
        }

        if (!BindingMatches(session, command))
        {
            return new AcquireChildAttachBindingResult(
                SessionTreeBindingAcquireState.BindingChanged,
                RejectionReason: "parent_binding_changed");
        }

        var receipt = new SessionTreeBindingUseReceipt(
            Guid.NewGuid().ToString("N"),
            command.ProjectId,
            command.CommandId,
            command.EdgeId,
            SessionId,
            command.ExpectedWorkDir,
            command.ExpectedRunnerId,
            command.ExpectedRuntime,
            command.ExpectedRuntimeSessionId,
            command.ExpectedBindingEpoch,
            ParentAgentId: command.ParentAgentId);
        session.BindingUseReceipts = receipts.Append(receipt).ToArray();
        await CommitAsync(session, []);
        return new AcquireChildAttachBindingResult(SessionTreeBindingAcquireState.Acquired, receipt);
    }

    public async Task<ReleaseChildAttachBindingResult> ReleaseChildAttachBindingAsync(
        ReleaseChildAttachBindingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = await GetRequiredAsync();
        var receipts = session.BindingUseReceipts?.ToList() ?? [];
        var index = receipts.FindIndex(item => item.ReceiptId == command.Receipt.ReceiptId);
        if (index < 0)
            return new ReleaseChildAttachBindingResult(
                SessionTreeBindingReleaseState.ReconciliationRequired,
                "parent_binding_use_missing");

        var current = receipts[index];
        if (!BindingUseMatches(current, command.Receipt))
            return new ReleaseChildAttachBindingResult(
                SessionTreeBindingReleaseState.ReconciliationRequired,
                "parent_binding_use_mismatch");
        if (current.State == SessionTreeBindingUseState.Released)
        {
            return current.ReleaseOutcome == command.Outcome
                ? new ReleaseChildAttachBindingResult(SessionTreeBindingReleaseState.AlreadyReleased)
                : new ReleaseChildAttachBindingResult(
                    SessionTreeBindingReleaseState.ReconciliationRequired,
                    "parent_binding_release_mismatch");
        }

        receipts[index] = current with
        {
            State = SessionTreeBindingUseState.Released,
            ReleaseOutcome = command.Outcome,
        };
        session.BindingUseReceipts = receipts;
        await CommitAsync(session, []);
        return new ReleaseChildAttachBindingResult(SessionTreeBindingReleaseState.Released);
    }

    private SessionTreeAttachReceipt AttachReceipt(ApplyParentLinkAttachCommand command) =>
        new(
            command.CommandId,
            command.EdgeId,
            command.ParentSessionId,
            SessionId,
            command.ChildLaunchJobId,
            command.AttachedRevision,
            command.ProjectId,
            SessionTreeMutationKind.Attach,
            command.ExpectedWorkDir,
            command.ExpectedRunnerId,
            command.ExpectedRuntime,
            command.ExpectedRuntimeSessionId,
            command.ExpectedBindingEpoch,
            command.BindingUseReceiptId,
            command.ExpectedLinkState,
            command.ParentAgentId);

    private bool BindingMatches(AgentSession session, AcquireChildAttachBindingCommand command) =>
        session.BindingEpoch == command.ExpectedBindingEpoch
        && session.Runtime.WorkDir == command.ExpectedWorkDir
        && session.Runtime.RunnerId == command.ExpectedRunnerId
        && string.Equals(session.Runtime.Runtime, command.ExpectedRuntime, StringComparison.Ordinal)
        && session.Status.AgentRuntimeSessionId == command.ExpectedRuntimeSessionId;

    private static bool BindingUseMatches(
        SessionTreeBindingUseReceipt receipt,
        AcquireChildAttachBindingCommand command) =>
        receipt.ProjectId == command.ProjectId
        && receipt.CommandId == command.CommandId
        && receipt.EdgeId == command.EdgeId
        && receipt.ParentSessionId == command.ParentSessionId
        && receipt.ParentWorkDir == command.ExpectedWorkDir
        && receipt.RunnerId == command.ExpectedRunnerId
        && receipt.Runtime == command.ExpectedRuntime
        && receipt.RuntimeSessionId == command.ExpectedRuntimeSessionId
        && receipt.BindingEpoch == command.ExpectedBindingEpoch
        && receipt.ParentAgentId == command.ParentAgentId;

    private static bool BindingUseMatches(
        SessionTreeBindingUseReceipt current,
        SessionTreeBindingUseReceipt expected) =>
        current.ReceiptId == expected.ReceiptId
        && current.ProjectId == expected.ProjectId
        && current.CommandId == expected.CommandId
        && current.EdgeId == expected.EdgeId
        && current.ParentSessionId == expected.ParentSessionId
        && current.ParentWorkDir == expected.ParentWorkDir
        && current.RunnerId == expected.RunnerId
        && current.Runtime == expected.Runtime
        && current.RuntimeSessionId == expected.RuntimeSessionId
        && current.BindingEpoch == expected.BindingEpoch
        && current.ParentAgentId == expected.ParentAgentId;

    public async Task<ClaimSubagentTerminalReportResult> ClaimSubagentTerminalReportAsync(
        ClaimSubagentTerminalReportCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = await GetRequiredAsync();
        var link = session.ParentLink;
        if (link is null || !MatchesParentLink(link, command.EdgeId, command.ChildLaunchJobId))
        {
            return new ClaimSubagentTerminalReportResult(
                SubagentTerminalReportClaimDisposition.Rejected,
                RejectionReason: "parent_link_identity_mismatch");
        }

        if (link.TerminalReport == TerminalReportState.Delivered)
        {
            return new ClaimSubagentTerminalReportResult(
                SubagentTerminalReportClaimDisposition.Delivered,
                link.TerminalReportDeliveredInputId);
        }
        if (link.TerminalReport == TerminalReportState.Pending)
            return new ClaimSubagentTerminalReportResult(SubagentTerminalReportClaimDisposition.Pending);
        if (link.TerminalReport == TerminalReportState.Suppressed)
            return new ClaimSubagentTerminalReportResult(SubagentTerminalReportClaimDisposition.Suppressed);
        if (link.State == SessionParentLinkState.Detached)
        {
            session.ParentLink = link with { TerminalReport = TerminalReportState.Suppressed };
            await CommitAsync(session, []);
            return new ClaimSubagentTerminalReportResult(SubagentTerminalReportClaimDisposition.Suppressed);
        }

        session.ParentLink = link with { TerminalReport = TerminalReportState.Pending };
        await CommitAsync(session, []);
        return new ClaimSubagentTerminalReportResult(SubagentTerminalReportClaimDisposition.ClaimedPending);
    }

    public async Task<RecordSubagentTerminalReportDeliveredResult> RecordSubagentTerminalReportDeliveredAsync(
        RecordSubagentTerminalReportDeliveredCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.ParentInputId))
            throw new ArgumentException("ParentInputId is required.", nameof(command));

        var session = await GetRequiredAsync();
        var link = session.ParentLink;
        if (link is null || !MatchesParentLink(link, command.EdgeId, command.ChildLaunchJobId))
        {
            return new RecordSubagentTerminalReportDeliveredResult(
                SubagentTerminalReportDeliveryDisposition.Rejected,
                RejectionReason: "parent_link_identity_mismatch");
        }
        if (link.TerminalReport == TerminalReportState.Delivered)
        {
            return new RecordSubagentTerminalReportDeliveredResult(
                link.TerminalReportDeliveredInputId == command.ParentInputId
                    ? SubagentTerminalReportDeliveryDisposition.AlreadyDelivered
                    : SubagentTerminalReportDeliveryDisposition.InputIdConflict,
                link.TerminalReportDeliveredInputId,
                link.TerminalReportDeliveredInputId == command.ParentInputId
                    ? null
                    : "parent_input_id_conflict");
        }
        if (link.TerminalReport != TerminalReportState.Pending)
        {
            return new RecordSubagentTerminalReportDeliveredResult(
                SubagentTerminalReportDeliveryDisposition.Rejected,
                RejectionReason: "terminal_report_not_pending");
        }

        session.ParentLink = link with
        {
            TerminalReport = TerminalReportState.Delivered,
            TerminalReportDeliveredInputId = command.ParentInputId,
        };
        await CommitAsync(session, []);
        return new RecordSubagentTerminalReportDeliveredResult(
            SubagentTerminalReportDeliveryDisposition.Delivered,
            command.ParentInputId);
    }

    public async Task<ApplyParentLinkDetachResult> ApplyParentLinkDetachAsync(
        ApplyParentLinkDetachCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.DetachedRevision <= 0
            || string.IsNullOrWhiteSpace(command.CommandId)
            || string.IsNullOrWhiteSpace(command.ChildSessionId)
            || command.ExpectedAttachedRevision is null
            || command.ExpectedAttachedRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(command), "DetachedRevision must be positive.");

        var session = await GetRequiredAsync();
        var link = session.ParentLink;
        if (link is null
            || command.ChildSessionId != SessionId
            || !MatchesParentLink(link, command.EdgeId, command.ParentSessionId, command.ChildLaunchJobId))
        {
            return new ApplyParentLinkDetachResult(
                SessionTreeDetachMutationState.Rejected,
                RejectionReason: "parent_link_identity_mismatch");
        }
        if (link.AttachedRevision != command.ExpectedAttachedRevision)
        {
            return new ApplyParentLinkDetachResult(
                SessionTreeDetachMutationState.ReconciliationRequired,
                link,
                "reconciliation_required");
        }
        if (link.State == SessionParentLinkState.Detached)
        {
            return link.DetachCommandId == command.CommandId
                && link.DetachedRevision == command.DetachedRevision
                && link.DetachExpectedAttachedRevision == command.ExpectedAttachedRevision
                ? new ApplyParentLinkDetachResult(
                    SessionTreeDetachMutationState.Detached,
                    link,
                    Receipt: new SessionTreeDetachReceipt(
                        command.CommandId!,
                        command.EdgeId,
                        command.ParentSessionId,
                        command.ChildSessionId!,
                        command.DetachedRevision,
                        command.ChildLaunchJobId,
                        command.ExpectedAttachedRevision.Value))
                : new ApplyParentLinkDetachResult(
                    SessionTreeDetachMutationState.ReconciliationRequired,
                    link,
                    "reconciliation_required");
        }

        session.ParentLink = link with
        {
            State = SessionParentLinkState.Detached,
            DetachedAt = Now(),
            DetachedRevision = command.DetachedRevision,
            DetachCommandId = command.CommandId,
            DetachExpectedAttachedRevision = command.ExpectedAttachedRevision,
            TerminalReport = link.TerminalReport == TerminalReportState.None
                ? TerminalReportState.Suppressed
                : link.TerminalReport,
        };
        await CommitAsync(session, []);
        return new ApplyParentLinkDetachResult(
            SessionTreeDetachMutationState.Detached,
            session.ParentLink,
            Receipt: new SessionTreeDetachReceipt(
                command.CommandId!,
                command.EdgeId,
                command.ParentSessionId,
                command.ChildSessionId!,
                command.DetachedRevision,
                command.ChildLaunchJobId,
                command.ExpectedAttachedRevision.Value));
    }

    private static bool MatchesParentLink(
        SessionParentLink link,
        string edgeId,
        string childLaunchJobId) =>
        MatchesParentLink(link, edgeId, link.ParentSessionId, childLaunchJobId);

    private static bool MatchesParentLink(
        SessionParentLink link,
        string edgeId,
        string parentSessionId,
        string childLaunchJobId) =>
        link.EdgeId == edgeId
        && link.ParentSessionId == parentSessionId
        && link.ChildLaunchJobId == childLaunchJobId;

    public async Task PromoteProvisionalLaunchAsync()
    {
        var session = await GetRequiredAsync();
        if (session.LaunchVisibility == AgentLaunchVisibility.Rejected)
            throw new InvalidOperationException("Rejected AgentSession launch cannot be promoted.");
        if (session.LaunchVisibility == AgentLaunchVisibility.Visible)
            return;
        session.LaunchVisibility = AgentLaunchVisibility.Visible;
        await _stateStore.SaveAsync(SessionId, session);
        _session = session;
    }

    public async Task AbortProvisionalLaunchAsync(string jobId, string turnId, string reason)
    {
        var info = await GetAsync();
        if (info is null)
            return;
        var session = await GetRequiredAsync();
        if (session.LaunchVisibility == AgentLaunchVisibility.Rejected)
            return;
        if (!string.IsNullOrWhiteSpace(turnId))
            await StopQueuedTurnAsync(turnId);
        session = await GetRequiredAsync();
        session.LaunchVisibility = AgentLaunchVisibility.Rejected;
        session.ParentLink = null;
        session.Status = session.Status with { Activity = AgentSessionActivity.Idle };
        await _stateStore.SaveAsync(SessionId, session);
        _session = session;
    }

    private void CheckInputsAndTurns(EnsureInitialLaunchCommand command, out bool alreadyPersisted)
    {
        alreadyPersisted = false;
        var inputs = _session!.Status.Inputs ?? [];
        var turns = _session.Status.Turns ?? [];
        var inputMatch = inputs.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, command.InputId, StringComparison.Ordinal));
        var turnMatch = turns.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, command.TurnId, StringComparison.Ordinal));

        if (inputMatch is null && turnMatch is null)
            return;

        alreadyPersisted = true;
        if (inputMatch is not null)
        {
            if (!string.Equals(inputMatch.Text, command.Prompt, StringComparison.Ordinal)
                || !string.Equals(inputMatch.Source, command.Source, StringComparison.Ordinal)
                || !string.Equals(inputMatch.JobId, command.JobId, StringComparison.Ordinal)
                || !AttachmentSetEquivalent(inputMatch.Attachments, command.Attachments)
                || !Equals(inputMatch.Provenance, command.Provenance)
                || !Equals(inputMatch.StartupContext, command.StartupContext))
            {
                throw new InvalidOperationException(
                    $"AgentSession {SessionId} already has input '{command.InputId}' with different content/source/job/attachments.");
            }
        }

        if (turnMatch is not null)
        {
            if (!string.Equals(turnMatch.JobId, command.JobId, StringComparison.Ordinal)
                || !turnMatch.InputIds.Contains(command.InputId, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"AgentSession {SessionId} already has turn '{command.TurnId}' with different job/input linkage.");
            }
        }
    }

    public async Task MarkInitialTurnExecutingAsync(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return;
        var session = await GetRequiredAsync();
        var events = session.MarkInitialTurnExecuting(jobId, Now());
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            return;
        }
        await CommitAsync(session, events);
    }

    private static bool ReclaimUnconfirmedFollowupDispatches(AgentSession session)
    {
        var leases = GetPendingFollowups(session).ToList();
        var queuedTurnIds = (session.Status.Turns ?? [])
            .Where(turn => turn.Status == AgentTurnStatus.Queued)
            .Select(turn => turn.Id)
            .ToHashSet(StringComparer.Ordinal);
        var changed = false;
        for (var index = 0; index < leases.Count; index++)
        {
            if (!leases[index].Dispatching || !queuedTurnIds.Contains(leases[index].TurnId ?? string.Empty))
                continue;
            leases[index] = leases[index] with { Dispatching = false };
            changed = true;
        }
        if (changed)
            SetPendingFollowups(session, leases);
        return changed;
    }

    public async Task MarkInitialTurnTerminalAsync(string jobId, AgentTurnStatus status, AgentTurnResult? result)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return;
        var session = await GetRequiredAsync();
        var turn = (session.Status.Turns ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.JobId, jobId, StringComparison.Ordinal));
        var events = session.MarkInitialTurnTerminal(jobId, status, result, Now());
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            _followupDispatchScheduler?.Schedule(
                session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty,
                session.Id);
            await ObserveWorkflowExecutionAsync(
                turn,
                ObservationKind(status),
                status == AgentTurnStatus.Completed ? "runtime-completed" : "runtime-failure",
                result?.FailureReason ?? result?.Message);
            return;
        }
        await CommitAsync(session, events);
        _followupDispatchScheduler?.Schedule(
            session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty,
            session.Id);
        await ObserveWorkflowExecutionAsync(
            turn,
            ObservationKind(status),
            status == AgentTurnStatus.Completed ? "runtime-completed" : "runtime-failure",
            result?.FailureReason ?? result?.Message);
    }

    public async Task RecordFollowupTurnAsync(RecordFollowupTurnCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.InputId))
            throw new ArgumentException("Input id is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.TurnId))
            throw new ArgumentException("Turn id is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.Prompt)
            && (command.Attachments is null || command.Attachments.Count == 0))
        {
            throw new ArgumentException(
                "Prompt is required unless at least one attachment is accepted.",
                nameof(command));
        }
        if (string.IsNullOrWhiteSpace(command.Source))
            throw new ArgumentException("Source is required.", nameof(command));

        var session = await GetRequiredAsync();
        var events = session.RecordFollowupTurn(
            inputId: command.InputId,
            turnId: command.TurnId,
            prompt: command.Prompt,
            source: command.Source,
            now: Now(),
            attachments: command.Attachments,
            provenance: command.Provenance);
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            _stateDirty = true;
            EnsurePersistenceTimer();
            return;
        }
        await CommitAsync(session, events);
    }

    public async Task AbandonFollowupTurnAsync(string inputId, string turnId)
    {
        if (string.IsNullOrWhiteSpace(inputId) || string.IsNullOrWhiteSpace(turnId))
            return;
        var session = await GetRequiredAsync();
        var events = session.AbandonFollowupTurn(inputId, turnId, Now());
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            return;
        }
        await CommitAsync(session, events);
    }

    public async Task MarkTurnExecutingAsync(string turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId))
            return;
        var session = await GetRequiredAsync();
        var events = session.MarkTurnExecuting(turnId, Now());
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            return;
        }
        await CommitAsync(session, events);
    }

    public async Task MarkTurnTerminalAsync(string turnId, AgentTurnStatus status, AgentTurnResult? result)
    {
        if (string.IsNullOrWhiteSpace(turnId))
            return;
        var session = await GetRequiredAsync();
        var turn = (session.Status.Turns ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
        var events = session.MarkTurnTerminal(turnId, status, result, Now());
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            await ObserveWorkflowExecutionAsync(
                turn,
                ObservationKind(status),
                status == AgentTurnStatus.Completed ? "runtime-completed" : "runtime-failure",
                result?.FailureReason ?? result?.Message);
            return;
        }
        await CommitAsync(session, events);
        await ObserveWorkflowExecutionAsync(
            turn,
            ObservationKind(status),
            status == AgentTurnStatus.Completed ? "runtime-completed" : "runtime-failure",
            result?.FailureReason ?? result?.Message);
    }

    public async Task<AgentTurnStopResult> StopQueuedTurnAsync(string turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId))
            return new AgentTurnStopResult(null, false);
        var session = await GetRequiredAsync();
        var turn = (session.Status.Turns ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
        var lease = GetPendingFollowups(session).FirstOrDefault(candidate =>
            string.Equals(candidate.TurnId, turnId, StringComparison.Ordinal));
        var result = session.StopQueuedTurn(turnId, Now());
        await _stateStore.SaveAsync(SessionId, session);
        _session = session;
        if (result.Cancelled && lease is not null)
        {
            await ReleaseFollowupConcurrencyPermitAsync(
                session,
                lease.ConcurrencyToken,
                lease.ConcurrencyAgentId,
                lease.ConcurrencyPermitId,
                lease.ConcurrencyGeneration,
                lease.ConcurrencyWaiterId);
            _followupDispatchScheduler?.Schedule(
                session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty,
                session.Id);
        }
        if (result.Cancelled || result.Control?.Status == AgentTurnStatus.Cancelled)
            await ObserveWorkflowExecutionAsync(
                turn,
                SessionWorkflowObservationKind.Cancelled,
                "session-stop",
                stopOperationId: session.Status.PendingStop?.TurnId == turnId
                    ? session.Status.PendingStop.OperationId
                    : null);
        return result;
    }

    public async Task StopTurnAsync(string turnId)
    {
        _ = await StopQueuedTurnAsync(turnId);
    }

    public async Task<AgentTurnStopClaimResult> ClaimTurnStopAsync(string turnId, string? operationId = null)
    {
        var session = await GetRequiredAsync();
        var result = session.ClaimTurnStop(
            turnId,
            operationId,
            _timeProvider.GetUtcNow(),
            StopOperationDeadline);
        await _stateStore.SaveAsync(SessionId, session);
        _session = session;
        await EnsureStopRecoveryReminderAsync();
        return result;
    }

    public async Task MarkTurnStopDispatchedAsync(string turnId, string operationId)
    {
        if (string.IsNullOrWhiteSpace(turnId) || string.IsNullOrWhiteSpace(operationId))
            return;
        var session = await GetRequiredAsync();
        session.MarkTurnStopDispatched(turnId, operationId);
        await _stateStore.SaveAsync(SessionId, session);
        _session = session;
    }

    public async Task CompleteTurnStopAsync(string turnId, string operationId)
    {
        if (string.IsNullOrWhiteSpace(turnId) || string.IsNullOrWhiteSpace(operationId))
            return;
        var session = await GetRequiredAsync();
        var pending = session.Status.PendingStop;
        var turn = (session.Status.Turns ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
        if (pending is null
            || !string.Equals(pending.TurnId, turnId, StringComparison.Ordinal)
            || !string.Equals(pending.OperationId, operationId, StringComparison.Ordinal))
        {
            if (turn?.Status == AgentTurnStatus.Cancelled)
                await ObserveWorkflowExecutionAsync(
                    turn,
                    SessionWorkflowObservationKind.Cancelled,
                    "session-stop",
                    stopOperationId: operationId);
            return;
        }

        var events = session.MarkTurnTerminal(turnId, AgentTurnStatus.Cancelled, null, Now());
        session.CompleteTurnStop(turnId, operationId);
        if (events.Count > 0)
            await CommitAsync(session, events);
        else
            await _stateStore.SaveAsync(SessionId, session);
        _session = session;
        await EnsureStopRecoveryReminderAsync();
        await ObserveWorkflowExecutionAsync(
            turn,
            SessionWorkflowObservationKind.Stopped,
            "session-stop",
            stopOperationId: operationId);
    }

    public async Task ReleaseTurnStopAsync(string turnId, string operationId)
    {
        if (string.IsNullOrWhiteSpace(turnId) || string.IsNullOrWhiteSpace(operationId))
            return;
        var session = await GetRequiredAsync();
        session.SettleTurnStop(turnId, operationId, AgentSessionStopDisposition.NotCancellable);
        await _stateStore.SaveAsync(SessionId, session);
        _session = session;
        await EnsureStopRecoveryReminderAsync();
    }

    public async Task ApplyStopDeliveryAsync(
        string turnId,
        string operationId,
        AgentSessionStopDisposition disposition,
        string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(turnId) || string.IsNullOrWhiteSpace(operationId))
            return;

        var session = await GetRequiredAsync();
        var pending = session.Status.PendingStop;
        if (pending is null
            || !pending.IsActive
            || !string.Equals(pending.TurnId, turnId, StringComparison.Ordinal)
            || !string.Equals(pending.OperationId, operationId, StringComparison.Ordinal))
            return;

        var turn = (session.Status.Turns ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
        var control = session.ResolveTurnControl(turnId);
        switch (disposition)
        {
            case AgentSessionStopDisposition.Unavailable:
                await ObserveWorkflowExecutionAsync(
                    turn,
                    SessionWorkflowObservationKind.StopUnconfirmed,
                    reason ?? "stop-delivery-unavailable",
                    stopOperationId: operationId);
                await EnsureStopRecoveryReminderAsync();
                return;

            case AgentSessionStopDisposition.StopRequested:
                session.SettleTurnStop(turnId, operationId, disposition, reason);
                await _stateStore.SaveAsync(SessionId, session);
                _session = session;
                await ObserveWorkflowExecutionAsync(
                    turn,
                    SessionWorkflowObservationKind.StopUnconfirmed,
                    reason ?? "stop-unconfirmed",
                    stopOperationId: operationId);
                await EnsureStopRecoveryReminderAsync();
                return;

            case AgentSessionStopDisposition.Idle:
                session.SettleTurnStop(turnId, operationId, disposition, reason);
                await _stateStore.SaveAsync(SessionId, session);
                _session = session;
                await EnsureStopRecoveryReminderAsync();
                await ObserveWorkflowExecutionAsync(
                    turn,
                    SessionWorkflowObservationKind.Idle,
                    reason ?? "stop-target-idle",
                    stopOperationId: operationId);
                return;

            case AgentSessionStopDisposition.Ended:
            case AgentSessionStopDisposition.NotCancellable:
                session.SettleTurnStop(turnId, operationId, disposition, reason);
                await _stateStore.SaveAsync(SessionId, session);
                _session = session;
                await EnsureStopRecoveryReminderAsync();
                await ObserveWorkflowExecutionAsync(
                    turn,
                    SessionWorkflowObservationKind.TargetMissing,
                    reason ?? "stop-target-missing",
                    stopOperationId: operationId);
                return;

            case AgentSessionStopDisposition.Unknown:
            case AgentSessionStopDisposition.Blocked:
                if (control?.IsLaunchTurn == true && control.JobId is not null)
                {
                    await _grains.GetGrain<IAgentJobGrain>(control.JobId).MarkUnknownAsync(
                        reason ?? "stop-unconfirmed");
                }
                else if (control is not null)
                {
                    var events = session.MarkTurnTerminal(turnId, AgentTurnStatus.Unknown, null, Now());
                    if (events.Count > 0)
                        await CommitAsync(session, events);
                }

                session.SettleTurnStop(turnId, operationId, disposition, reason);
                await _stateStore.SaveAsync(SessionId, session);
                _session = session;
                await EnsureStopRecoveryReminderAsync();
                await ObserveWorkflowExecutionAsync(
                    turn,
                    SessionWorkflowObservationKind.StopUnconfirmed,
                    reason ?? "stop-unconfirmed",
                    stopOperationId: operationId);
                return;

            case AgentSessionStopDisposition.Stopped:
                if (control is not null)
                {
                    var events = session.MarkTurnTerminal(turnId, AgentTurnStatus.Cancelled, null, Now());
                    session.SettleTurnStop(turnId, operationId, disposition, reason);
                    if (events.Count > 0)
                        await CommitAsync(session, events);
                    else
                        await _stateStore.SaveAsync(SessionId, session);
                }
                else
                {
                    session.SettleTurnStop(turnId, operationId, disposition, reason);
                    await _stateStore.SaveAsync(SessionId, session);
                }

                _session = session;
                await EnsureStopRecoveryReminderAsync();
                await ObserveWorkflowExecutionAsync(
                    turn,
                    SessionWorkflowObservationKind.Stopped,
                    reason ?? "session-stop",
                    stopOperationId: operationId);
                return;

            default:
                session.SettleTurnStop(turnId, operationId, disposition, reason);
                await _stateStore.SaveAsync(SessionId, session);
                _session = session;
                await EnsureStopRecoveryReminderAsync();
                return;
        }
    }

    public async Task<AgentSessionStopClaim?> GetStopClaimAsync() =>
        (await GetRequiredAsync()).Status.PendingStop;

    public async Task<AgentTurnControlState?> ResolveTurnControlAsync(string turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId))
            return null;
        var session = await GetRequiredAsync();
        return session.ResolveTurnControl(turnId);
    }

    public async Task<AgentTurnControlState?> ResolveCurrentTurnControlAsync()
    {
        var session = await GetRequiredAsync();
        return session.ResolveCurrentTurnControl();
    }

    public async Task<AgentInitialLaunchSnapshot?> GetInitialLaunchAsync()
    {
        var session = await GetRequiredAsync();
        var inputs = session.Status.Inputs ?? [];
        var turns = session.Status.Turns ?? [];
        if (inputs.Count == 0 && turns.Count == 0)
            return null;
        var turn = turns.Count > 0 ? turns[0] : null;
        return new AgentInitialLaunchSnapshot(
            SessionId: SessionId,
            Input: inputs.Count > 0 ? inputs[0] : null,
            Turn: turn is null ? null : CopyTurnForBoundary(turn));
    }

    public async Task<IReadOnlyList<AgentTurnRecord>> ListTurnsAsync()
    {
        var session = await GetRequiredAsync();
        return session.Status.Turns is { } turns
            ? turns.Select(CopyTurnForBoundary).ToArray()
            : Array.Empty<AgentTurnRecord>();
    }

    private static AgentTurnRecord CopyTurnForBoundary(AgentTurnRecord turn) =>
        turn with { InputIds = turn.InputIds.ToArray() };

    private static Dictionary<string, AgentTurnStatus> SnapshotNonLaunchTurnStatuses(AgentSession session)
    {
        var turns = session.Status.Turns;
        if (turns is null || turns.Count == 0) return new();
        return turns
            .Where(t => string.IsNullOrWhiteSpace(t.JobId))
            .ToDictionary(t => t.Id, t => t.Status);
    }

    private static bool HasNewNonSuccessTurnSettlement(
        AgentSession session,
        IReadOnlyDictionary<string, AgentTurnStatus> before)
    {
        var turns = session.Status.Turns;
        if (turns is null || turns.Count == 0) return false;

        return turns.Any(turn =>
            string.IsNullOrWhiteSpace(turn.JobId)
            && turn.Status is AgentTurnStatus.Failed or AgentTurnStatus.Cancelled or AgentTurnStatus.Unknown
            && (!before.TryGetValue(turn.Id, out var prior)
                || prior is not (AgentTurnStatus.Failed or AgentTurnStatus.Cancelled or AgentTurnStatus.Unknown)));
    }

    private async Task TryEmitFollowupTerminalDeliveriesAsync(
        AgentSession session,
        Dictionary<string, AgentTurnStatus> before)
    {
        var turns = session.Status.Turns;
        if (turns is null || turns.Count == 0) return;
        foreach (var turn in turns)
        {
            if (!string.IsNullOrWhiteSpace(turn.JobId)) continue;
            before.TryGetValue(turn.Id, out var prior);
            if (IsTerminalTurn(prior) || !IsTerminalTurn(turn.Status)) continue;
            await TryEmitFollowupDeliveryAsync(session, turn);
        }
    }

    private static bool IsTerminalTurn(AgentTurnStatus status) =>
        status is not AgentTurnStatus.Queued and not AgentTurnStatus.Executing;

    private async Task TryEmitFollowupDeliveryAsync(AgentSession session, AgentTurnRecord turn)
    {
        var metadata = session.Metadata;
        if (metadata is null) return;

        var connectionId = metadata.Label(AgentSessionQueryMetadataKeys.ConnectionId);
        var workspaceTeamId = metadata.Label(AgentSessionQueryMetadataKeys.SlackWorkspaceTeamId);
        var conversationId = metadata.Label(AgentSessionQueryMetadataKeys.SlackConversationId);
        if (string.IsNullOrWhiteSpace(connectionId)
            || string.IsNullOrWhiteSpace(workspaceTeamId)
            || string.IsNullOrWhiteSpace(conversationId))
            return;

        var threadTs = metadata.Label(AgentSessionQueryMetadataKeys.SlackThreadTs);
        var title = metadata.Label(AgentSessionQueryMetadataKeys.Title);
        var projectId = metadata.Label(AgentSessionQueryMetadataKeys.ProjectId);
        var status = turn.Status switch
        {
            AgentTurnStatus.Cancelled => "failed",
            _ => turn.Status.ToString().ToLowerInvariant(),
        };

        var delivery = new
        {
            jobKey = $"agent-session-followup:{session.Id}:{turn.Id}",
            workLabel = !string.IsNullOrWhiteSpace(title) ? title : "Follow-up",
            connectionId,
            workspaceTeamId,
            slackUserId = (string?)metadata.Label(AgentSessionQueryMetadataKeys.SlackUserId),
            conversationId,
            threadTs,
            messageTs = (string?)null,
            status,
            message = turn.Result?.Message,
            failureReason = (string?)null,
            failureCategory = (string?)null,
            artifactCount = 0,
            exitCode = (int?)null,
            assistantText = AgentJobLineage.ExtractAssistantText(turn.Result?.Output),
        };
        var data = JsonSerializer.SerializeToElement(delivery, CloudEvent.JsonOptions);
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(projectId))
            extensions[EventCatalog.Lineage.ProjectId] = projectId;

        var envelope = new CloudEvent(
            id: $"followup-delivery:{session.Id}:{turn.Id}",
            source: new Uri($"/mohist/agent-session/{session.Id}", UriKind.Relative),
            type: EventCatalog.ReverseDns.AgentSessionFollowupDelivery,
            time: _timeProvider.GetUtcNow(),
            data: data,
            subject: session.Id,
            extensions: extensions);

        try
        {
            await _eventStore.AppendAsync(envelope, CancellationToken.None);
            EventDispatcherPoke.PokeAfterCommit(GrainFactory, _log, nameof(AgentSessionGrain), _backgroundTasks);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentSession {SessionId} follow-up delivery event could not be emitted for turn {TurnId}",
                session.Id,
                turn.Id);
        }
    }
}
