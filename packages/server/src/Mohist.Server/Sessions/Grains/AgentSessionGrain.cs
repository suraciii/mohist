using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed class AgentSessionGrain : Grain, IAgentSessionGrain
{
    private static readonly TimeSpan PersistTimerDueTime = TimeSpan.FromMilliseconds(200);
    private const string OpenCodeRuntime = "opencode";

    private readonly IAgentSessionStore _stateStore;
    private readonly IAgentSessionTranscriptStore _transcriptStore;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ITranscriptEventPublisher _transcriptPublisher;
    private readonly ILogger<AgentSessionGrain> _log;
    private readonly TimeProvider _timeProvider;
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
        TimeProvider timeProvider,
        ILogger<AgentSessionGrain> log)
    {
        _stateStore = stateStore;
        _transcriptStore = transcriptStore;
        _dbFactory = dbFactory;
        _transcriptPublisher = transcriptPublisher;
        _timeProvider = timeProvider;
        _log = log;
    }

    private string SessionId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _sessionReloadRequired = false;
        _session = await _stateStore.LoadAsync(SessionId);
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
            // agent-session launch endpoint, T-003) the launch endpoint
            // opened the session with an empty RunnerId; the runner's
            // subsequent open call carries the authoritative RunnerId,
            // and we stamp it onto the runtime exactly once. An
            // already-bound RunnerId is left untouched so workflow
            // sessions remain sticky across reopens (the existing
            // semantics the runner-side retry flow relies on).
            if (string.IsNullOrWhiteSpace(_session.Runtime.RunnerId)
                && !string.IsNullOrWhiteSpace(command.RunnerId))
            {
                _session.Runtime = _session.Runtime with { RunnerId = command.RunnerId };
            }
        }

        await _stateStore.SaveAsync(SessionId, _session);
        return await ToInfoAsync(_session);
    }

    private AgentSession CreateSession(OpenAgentSessionCommand command)
    {
        var session = AgentSession.Create(
            SessionId,
            command.RunnerId ?? string.Empty,
            command.WorkDir,
            command.Metadata,
            Now(),
            runtime: command.AgentRuntime);
        session.Settings = new AgentSessionSettings(command.Model);
        return session;
    }

    public async Task<AgentSessionInfo> AttachPhysicalSessionAsync(AttachPhysicalSessionCommand command)
    {
        var session = await GetRequiredAsync();

        var now = Now();
        var events = session.AttachPhysicalSession(
            command.AgentSessionId,
            command.Model,
            command.WorkDir,
            command.ChangeDir,
            command.ProcessPid,
            now);
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            return await ToInfoAsync(session);
        }

        await CommitAsync(session, events);
        return await ToInfoAsync(session);
    }

    public async Task<AgentSessionRecoveryResult> CompactAsync(CompactAgentSessionCommand command)
    {
        var session = await GetRequiredAsync();
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
        EnsureSessionIdleForRecovery(session);
        session.EnsureExpectedRuntimeSession(command.ExpectedRuntimeSessionId);

        var now = Now();
        var usage = AgentSessionJsonHelper.Usage(session);
        var usedBefore = usage.ContextWindowUsed;
        var size = usage.ContextWindowSize;

        var events = session.RebindRuntimeSession(
            command.ReplacementRuntimeSessionId,
            usedBefore,
            size,
            now,
            command.ReplacementRuntime);

        await PersistRecoveryAsync(session, events, []);

        return BuildRecoveryResult(session, usedBefore, size, "reset", wasCompacted: false);
    }

    public async Task<SessionCommandRequest> PrepareSessionCommandAsync(SessionCommandKind command)
    {
        if (command is not (SessionCommandKind.Compact or SessionCommandKind.Reset))
            throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported session command");

        return await BeginSessionCommandAsync(command);
    }

    public Task<SessionCommandRequest> BeginResetAsync() =>
        BeginSessionCommandAsync(SessionCommandKind.Reset);

    private async Task<SessionCommandRequest> BeginSessionCommandAsync(SessionCommandKind command)
    {
        var session = await GetRequiredAsync();
        EnsureSessionIdleForRecovery(session);

        if (session.Status.PendingReset is { } pending)
        {
            if (command == SessionCommandKind.Reset
                && string.Equals(pending.Command, CommandName(command), StringComparison.Ordinal))
            {
                return BuildSessionCommandRequest(session, command, pending);
            }

            throw new RecoveryOperationInProgressException(session.Id, pending.Command);
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
            CommandName(command));
        session.Status = session.Status with { PendingReset = reservation };
        await CommitAsync(session, []);
        return BuildSessionCommandRequest(session, command, reservation);
    }

    public async Task<AgentSessionRecoveryResult> CompleteCompactAsync(CompleteCompactAgentSessionCommand command)
    {
        var session = await GetRequiredAsync();
        var reservation = RequireReservation(session, command.OperationId, SessionCommandKind.Compact);
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
        session.Status = session.Status with { PendingReset = null };
        await PersistRecoveryAsync(session, events, transcriptEntries);
        return BuildRecoveryResult(session, usedBefore, size, "compact", wasCompacted: true);
    }

    public async Task<AgentSessionRecoveryResult> CompleteResetAsync(CompleteResetAgentSessionCommand command)
    {
        var session = await GetRequiredAsync();
        var reservation = RequireReservation(session, command.OperationId, SessionCommandKind.Reset);
        if (!string.Equals(reservation.Runtime, command.ReplacementRuntime, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Reset replacement runtime for AgentSession {session.Id} does not match its reservation.");

        EnsureSessionIdleForRecovery(session);
        session.EnsureExpectedRuntimeSession(reservation.ExpectedRuntimeSessionId);

        var now = Now();
        var usage = AgentSessionJsonHelper.Usage(session);
        var usedBefore = usage.ContextWindowUsed;
        var size = usage.ContextWindowSize;
        var events = session.RebindRuntimeSession(
            command.ReplacementRuntimeSessionId,
            usedBefore,
            size,
            now,
            command.ReplacementRuntime);
        session.Status = session.Status with { PendingReset = null };
        await PersistRecoveryAsync(session, events, []);
        return BuildRecoveryResult(session, usedBefore, size, "reset", wasCompacted: false);
    }

    public async Task AbandonResetAsync(string operationId)
    {
        var session = await GetRequiredAsync();
        if (!string.Equals(session.Status.PendingReset?.OperationId, operationId, StringComparison.Ordinal))
            return;

        session.Status = session.Status with { PendingReset = null };
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
            OperationId: reservation?.OperationId);

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
        string.Equals(runtime, OpenCodeRuntime, StringComparison.OrdinalIgnoreCase);

    private void EnsureSessionIdleForRecovery(AgentSession session)
    {
        if (AgentSessionJsonHelper.StatusName(session, Now()) == "active")
            throw new InvalidOperationException(
                $"AgentSession {session.Id} is currently active; Compact and Reset require an idle session.");
    }

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
        IReadOnlyList<RuntimeEventEnvelope> transcriptEntries)
    {
        var now = Now();
        _transcript.Accept(session, transcriptEntries, now);
        var transcript = _transcript.BuildFlush(session, now);

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
        // The state/event transaction committed atomically. The recovery
        // transition is durable; a later Compact/Reset retry must not
        // re-append them. Treat the transcript like the normal flush path:
        // if its save fails, the committed domain events stay committed and
        // the un-committed transcript flush stays in _transcript for the next
        // retry. The recovery command returns success because the domain
        // fact (rebind or compaction) is persistent; only the transcript
        // evidence is pending.
        _session = session;
        if (transcript is not null)
        {
            try
            {
                await _transcriptStore.SaveAsync(transcript);
                _transcript.CommitFlush();
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to save recovery transcript for {SessionId}; parts={PartCount}",
                    SessionId, transcript.Parts.Count);
            }
        }

        await FanOutRealtimeAsync(session, transcriptEntries, events);
    }

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
            AgentSessionJsonHelper.StatusName(session, Now()),
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
        if (command.RuntimeEvents.Any(e => !string.Equals(e.Type, RuntimeEventTypes.SessionClosed, StringComparison.Ordinal)))
            throw new InvalidOperationException("System AgentSession events are limited to session.closed.");
        return AppendEventsAsync(command.RuntimeEvents, null, requireCurrentRuntimeBinding: false);
    }

    private async Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendEventsAsync(
        IReadOnlyList<AgentSessionRuntimeEventInput> runtimeEvents,
        string? runtimeSessionId,
        bool requireCurrentRuntimeBinding)
    {
        if (runtimeEvents.Count == 0) return [];

        var session = await GetRequiredAsync();
        if (requireCurrentRuntimeBinding
            && (string.IsNullOrWhiteSpace(runtimeSessionId)
                || !string.Equals(runtimeSessionId, session.Status.AgentRuntimeSessionId, StringComparison.Ordinal)))
        {
            return [];
        }

        var now = Now();
        var events = new List<AgentSessionEvent>();
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

            var payloadJson = string.IsNullOrWhiteSpace(e.PayloadJson) ? "{}" : e.PayloadJson;
            var classifiedPayload = ClassifySessionClosedPayload(session, e.Type, payloadJson, now);
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

        _transcript.Accept(session, allEntries, now);

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

    private string? ClassifySessionClosedPayload(AgentSession session, string type, string payloadJson, DateTime now)
    {
        if (!string.Equals(type, "session.closed", StringComparison.Ordinal))
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

        var status = AgentSessionJsonHelper.GetStringProp(payload, "status");
        var usage = AgentSessionJsonHelper.Usage(session);
        var elapsed = session.Status.LastDataAt is { } last
            && session.Status.BoundAt is { } bound
            && last > bound
            ? last - bound
            : (TimeSpan?)null;

        var producedArtifacts = AgentSessionJsonHelper.GetBoolProp(payload, "producedArtifacts")
            ?? AgentSessionJsonHelper.GetBoolProp(payload, "producedExpectedOutput")
            ?? false;

        var result = ContextExhaustionClassifier.ClassifyClose(
            status,
            usage.ContextWindowUsed,
            usage.ContextWindowSize,
            elapsed,
            producedArtifacts);
        if (result.Category is null)
        {
            // Try the rapid-completion heuristic for successful closes that
            // look suspiciously fast and did not produce expected output.
            result = ContextExhaustionClassifier.ClassifyRapidCompletion(
                status,
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
        if (_session is null || (!_stateDirty && !_transcript.HasPending))
        {
            DisposePersistTimer();
            return;
        }

        var success = await FlushAsync(CancellationToken.None);
        if (success)
            DisposePersistTimer();
    }

    private async Task<bool> FlushAsync(CancellationToken ct)
    {
        if (_session is null) return true;
        // A prior event-aware save on this activation failed and quarantined
        // it; do not attempt another flush from the same dirty state.
        if (_sessionReloadRequired)
            throw new InvalidOperationException($"Agent session {SessionId} must reload after a failed event-aware save");

        var now = Now();
        var transcript = _transcript.BuildFlush(_session, now);

        if (_stateDirty)
        {
            var pendingEvents = _pendingDomainEvents.Count == 0
                ? Array.Empty<AgentSessionEvent>()
                : _pendingDomainEvents.ToArray();
            try
            {
                await _stateStore.SaveAsync(SessionId, _session, pendingEvents, ct);
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

    public Task DeactivateForTestAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public async Task FlushForTestAsync()
    {
        if (await FlushAsync(CancellationToken.None))
            DisposePersistTimer();
    }

    private async Task<AgentSession> GetRequiredAsync()
    {
        if (_sessionReloadRequired)
            throw new InvalidOperationException($"Agent session {SessionId} must reload after a failed event-aware save");
        if (_session is not null) return _session;

        _session = await _stateStore.LoadAsync(SessionId);
        return _session ?? throw new InvalidOperationException($"Agent session {SessionId} does not exist.");
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
        AgentSessionJsonHelper.StatusName(s, Now()),
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
            s.Runtime.Runtime);
    }

    private async Task<AgentSessionTranscriptSummary> LoadEventSummaryAsync(string sessionId)
    {
        if (_cachedSummary is not null)
            return _cachedSummary;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var turnIds = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.Id)
            .ToListAsync();
        if (turnIds.Count == 0)
            return _cachedSummary = AgentSessionTranscriptSummary.Empty;

        var parts = await db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(e => turnIds.Contains(e.TurnId))
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToListAsync();

        var events = parts
            .Select(part => new TranscriptSummaryEvent(part.Sequence, part.Type, part.PayloadJson));
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
            RuntimeEventTypes.SessionLiveness => session.RecordActivity(now),
            RuntimeEventTypes.SessionClosed => session.RecordActivity(now),
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
                now),
            RuntimeEventTypes.ModelResolved => session.ResolveModel(
                AgentSessionJsonHelper.GetStringProp(payload, "resolvedModel") ?? AgentSessionJsonHelper.GetStringProp(payload, "model"),
                now),
            _ => []
        };
    }
}
