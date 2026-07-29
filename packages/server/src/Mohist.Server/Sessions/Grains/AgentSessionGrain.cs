using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Agent.Grains;

namespace Mohist.Server.Sessions.Grains;

public sealed class AgentSessionGrain : Grain, IAgentSessionGrain
{
    private static readonly TimeSpan FollowupLeaseWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PersistTimerDueTime = TimeSpan.FromMilliseconds(200);
    private const string OpenCodeRuntime = "opencode";
    private const string PiRuntime = "pi";

    private readonly IAgentSessionStore _stateStore;
    private readonly IAgentSessionTranscriptStore _transcriptStore;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ITranscriptEventPublisher _transcriptPublisher;
    private readonly IAgentSessionPersistenceObserver _persistenceObserver;
    private readonly ILogger<AgentSessionGrain> _log;
    private readonly TimeProvider _timeProvider;
    private readonly IAgentSessionConnectionRegistry _connections;
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
        ILogger<AgentSessionGrain> log)
    {
        _stateStore = stateStore;
        _transcriptStore = transcriptStore;
        _dbFactory = dbFactory;
        _transcriptPublisher = transcriptPublisher;
        _persistenceObserver = persistenceObserver;
        _timeProvider = timeProvider;
        _connections = connections;
        _log = log;
    }

    private string SessionId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _sessionReloadRequired = false;
        _session = await _stateStore.LoadAsync(SessionId);
        if (_session is not null)
            _session.PersistedActivitySummary = (_session.PersistedActivitySummary ?? AgentSessionActivitySummaryState.Empty).Normalize();
        if (_session?.Status.PendingTranscriptEvidence?.Count > 0)
            EnsurePersistenceTimer();
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
        session.Settings = new AgentSessionSettings(command.Model, command.Definition);
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
            now);
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
        var now = Now();
        var usage = AgentSessionJsonHelper.Usage(session);
        var usedBefore = usage.ContextWindowUsed;
        var size = usage.ContextWindowSize;

        var events = session.RebindRuntimeSession(
            new AgentRuntimeBinding(session.Runtime.RunnerId, session.Runtime.Runtime, command.ExpectedRuntimeSessionId),
            new AgentRuntimeBinding(session.Runtime.RunnerId, command.ReplacementRuntime, command.ReplacementRuntimeSessionId),
            "reset",
            now);

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
            IdempotencyKey: key);
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
        var now = Now();
        var usage = AgentSessionJsonHelper.Usage(session);
        var usedBefore = usage.ContextWindowUsed;
        var size = usage.ContextWindowSize;
        var events = session.RebindRuntimeSession(
            new AgentRuntimeBinding(session.Runtime.RunnerId, session.Runtime.Runtime, reservation.ExpectedRuntimeSessionId),
            new AgentRuntimeBinding(session.Runtime.RunnerId, command.ReplacementRuntime, command.ReplacementRuntimeSessionId),
            "reset",
            now);
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
            throw new FollowupOperationInProgressException(session.Id);

        var lease = new AgentSessionFollowupLease(
            Guid.NewGuid().ToString("N"),
            session.Status.AgentRuntimeSessionId!,
            StartedAt: Now());
        SetPendingFollowups(session, pending.Append(lease).ToArray());
        await CommitAsync(session, []);
        return new AgentSessionFollowupReservation(lease.OperationId, StartsIdleTurn: true);
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
        SetPendingFollowups(session, remaining);
        await CommitAsync(session, []);
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

    private void EnsureSessionIdleForRecovery(AgentSession session)
    {
        var pending = GetPendingFollowups(session);
        if (pending.Count > 0 || session.Status.Activity != AgentSessionActivity.Idle)
            throw new InvalidOperationException(
                $"AgentSession {session.Id} is currently active; Compact and Reset require an idle session. "
                + $"Activity={session.Status.Activity}, PendingFollowups={pending.Count}.");
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
            AgentSessionId = null,
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

    public Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendRuntimeEventsAsync(AppendAgentSessionRuntimeEventsCommand command) =>
        AppendEventsAsync(command.RuntimeEvents, command.RuntimeSessionId, requireCurrentRuntimeBinding: true);

    public Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendSystemEventsAsync(AppendAgentSessionSystemEventsCommand command)
    {
        if (command.RuntimeEvents.Any(e => !string.Equals(e.Type, RuntimeEventTypes.SessionActivity, StringComparison.Ordinal)))
            throw new InvalidOperationException("System AgentSession events are limited to session.activity.");
        return AppendEventsAsync(command.RuntimeEvents, null, requireCurrentRuntimeBinding: false);
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
        foreach (var e in runtimeEvents)
        {
            var domainEvents = ApplyRuntimeEventToDomain(session, e, now);
            events.AddRange(domainEvents);

            if (e.Type == RuntimeEventTypes.SessionActivity)
            {
                var activityPayload = JSON.DeserializeElement(e.PayloadJson);
                if (ParseActivity(activityPayload) == AgentSessionActivity.Idle)
                {
                    var operationId = AgentSessionJsonHelper.GetStringProp(activityPayload, "operationId");
                    if (!string.IsNullOrWhiteSpace(operationId))
                    {
                        var pending = GetPendingFollowups(session);
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

    public async Task<AgentSessionInfo?> GetAsync()
    {
        // A quarantined activation holds a session mutated past a rolled-back
        // state/event transaction. Do not expose that dirty view; reject until
        // the grain reactivates and reloads from storage.
        if (_sessionReloadRequired)
            throw new InvalidOperationException($"Agent session {SessionId} must reload after a failed event-aware save");
        return _session is null ? null : await ToInfoAsync(_session);
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
        var now = Now();
        session.SetActivity(AgentSessionActivity.Unknown, now);
        var payload = JSON.Serialize(new { activity = "unknown", observedAt = now.ToString("o"), reason = "runner-disconnected" });
        await AppendEventsAsync([new AgentSessionRuntimeEventInput(RuntimeEventTypes.SessionActivity, payload)], session.Status.AgentRuntimeSessionId, true);
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

    private void RejectIfReloadRequired()
    {
        if (_sessionReloadRequired)
            throw new InvalidOperationException($"Agent session {SessionId} must reload after a failed event-aware save");
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
            usage.CachedWriteTokens);
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
        return runtimeEvent.Type switch
        {
            RuntimeEventTypes.SessionInput => session.SetActivity(
                session.Status.Activity == AgentSessionActivity.Unknown
                    ? AgentSessionActivity.Unknown
                    : AgentSessionActivity.Active,
                now),
            RuntimeEventTypes.SessionActivity => session.SetActivity(
                ParseActivity(payload),
                now),
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

    private static AgentSessionActivity ParseActivity(JsonElement payload) =>
        AgentSessionJsonHelper.GetStringProp(payload, "activity")?.ToLowerInvariant() switch
        {
            "active" => AgentSessionActivity.Active,
            "unknown" => AgentSessionActivity.Unknown,
            _ => AgentSessionActivity.Idle,
        };

    public async Task<EnsureInitialLaunchResult> EnsureInitialLaunchAsync(EnsureInitialLaunchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.InputId))
            throw new ArgumentException("Input id is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.TurnId))
            throw new ArgumentException("Turn id is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(command));
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
                Metadata: command.Metadata));
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
                now: Now());
        }

        await _stateStore.SaveAsync(SessionId, _session);
        _connections.RegisterSession(_session.Runtime.RunnerId, SessionId);

        return new EnsureInitialLaunchResult(
            SessionId: SessionId,
            InputId: command.InputId,
            TurnId: command.TurnId,
            AlreadyPersisted: alreadyPersisted);
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
                || !string.Equals(inputMatch.JobId, command.JobId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"AgentSession {SessionId} already has input '{command.InputId}' with different content/source/job.");
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

    public async Task MarkInitialTurnTerminalAsync(string jobId, AgentTurnStatus status, AgentTurnResult? result)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return;
        var session = await GetRequiredAsync();
        var events = session.MarkInitialTurnTerminal(jobId, status, result, Now());
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            return;
        }
        await CommitAsync(session, events);
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
            Turn: turn is null ? null : turn with { InputIds = turn.InputIds.ToArray() });
    }
}
