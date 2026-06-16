using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed class AgentSessionGrain : Grain, IAgentSessionGrain
{
    private static readonly TimeSpan PersistTimerDueTime = TimeSpan.FromMilliseconds(200);

    private readonly IAgentSessionStore _stateStore;
    private readonly IAgentSessionTranscriptStore _transcriptStore;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventPublisher _eventPublisher;
    private readonly ITranscriptEventPublisher _transcriptPublisher;
    private readonly ILogger<AgentSessionGrain> _log;
    private readonly TranscriptAccumulator _transcript = new();
    private AgentSession? _session;
    private AgentSessionTranscriptSummary? _cachedSummary;
    private long _realtimeSequence;
    private IDisposable? _persistTimer;
    private bool _stateDirty;

    public AgentSessionGrain(
        IAgentSessionStore stateStore,
        IAgentSessionTranscriptStore transcriptStore,
        IDbContextFactory<MohistDbContext> dbFactory,
        IEventPublisher eventPublisher,
        ITranscriptEventPublisher transcriptPublisher,
        ILogger<AgentSessionGrain> log)
    {
        _stateStore = stateStore;
        _transcriptStore = transcriptStore;
        _dbFactory = dbFactory;
        _eventPublisher = eventPublisher;
        _transcriptPublisher = transcriptPublisher;
        _log = log;
    }

    private string SessionId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        _session = await _stateStore.LoadAsync(SessionId);
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _persistTimer?.Dispose();
        _persistTimer = null;

        await FlushAsync(ct);
    }

    public async Task<AgentSessionInfo> OpenAsync(OpenAgentSessionCommand command)
    {
        if (_session is null)
        {
            _session = CreateSession(command);
        }
        else
        {
            _ = _session.MergeMetadata(command.Metadata);
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
            command.Metadata);
        session.Settings = new AgentSessionSettings(command.Model);
        return session;
    }

    public async Task<AgentSessionInfo> AttachPhysicalSessionAsync(AttachPhysicalSessionCommand command)
    {
        var session = await GetRequiredAsync();

        var now = DateTime.UtcNow;
        var events = session.AttachPhysicalSession(command.AgentSessionId, command.Model, command.WorkDir, command.ChangeDir, command.ProcessPid, now);
        if (events.Count == 0)
        {
            await _stateStore.SaveAsync(SessionId, session);
            _session = session;
            return await ToInfoAsync(session);
        }

        await CommitAsync(session, events);
        return await ToInfoAsync(session);
    }

    public async Task<IReadOnlyList<AgentSessionRuntimeEventInfo>> AppendRuntimeEventsAsync(AppendAgentSessionRuntimeEventsCommand command)
    {
        if (command.RuntimeEvents.Count == 0) return [];

        var session = await GetRequiredAsync();

        var now = DateTime.UtcNow;
        var events = new List<AgentSessionEvent>();
        events.AddRange(session.RecordActivity(now));
        _stateDirty = true;

        var entries = new List<RuntimeEventEnvelope>();
        foreach (var e in command.RuntimeEvents)
        {
            var domainEvents = ApplyRuntimeEventToDomain(session, e, now);
            events.AddRange(domainEvents);

            entries.Add(new RuntimeEventEnvelope
            {
                Id = -(_realtimeSequence + 1),
                SessionId = session.Id,
                AgentSessionId = session.Status.AgentRuntimeSessionId,
                Sequence = ++_realtimeSequence,
                Type = e.Type,
                PayloadJson = string.IsNullOrWhiteSpace(e.PayloadJson) ? "{}" : e.PayloadJson,
                CreatedAt = now,
            });
        }

        _transcript.Accept(session, entries, now);

        _session = session;
        _cachedSummary = null;

        EnsurePersistenceTimer();

        await FanOutRealtimeAsync(
            session,
            entries,
            events);

        return entries.Select(e => ToEventInfo(e)).ToList();
    }

    private async Task FanOutRealtimeAsync(
        AgentSession session,
        IReadOnlyList<RuntimeEventEnvelope> entries,
        IReadOnlyList<AgentSessionEvent> domainEvents)
    {
        var source = AgentSessionSource(session);

        foreach (var domainEvent in domainEvents)
        {
            try
            {
                await PublishAgentSessionLifecycleAsync(domainEvent, source, session.Id);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to publish domain event for {SessionId}",
                    session.Id);
            }
        }

        if (entries.Count == 0) return;

        foreach (var row in entries)
        {
            if (!TranscriptAccumulator.EventTypes.Contains(row.Type))
                continue;

            JsonElement payload;
            try
            {
                payload = JsonSerializer.Deserialize<JsonElement>(row.PayloadJson);
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
                AgentSessionId: row.AgentSessionId,
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

        var now = DateTime.UtcNow;
        var transcript = _transcript.BuildFlush(_session, now);
        var stateSaved = !_stateDirty;

        if (_stateDirty)
        {
            try
            {
                await _stateStore.SaveAsync(SessionId, _session, Array.Empty<AgentSessionEvent>(), ct);
                stateSaved = true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to save state for {SessionId}",
                    SessionId);
            }
        }

        var transcriptSaved = transcript is null;
        if (transcript is not null)
        {
            try
            {
                await _transcriptStore.SaveAsync(transcript, ct);
                transcriptSaved = true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to save transcript for {SessionId}; parts={PartCount}",
                    SessionId, transcript.Parts.Count);
            }
        }

        if (stateSaved && transcriptSaved)
        {
            if (transcript is not null)
                _transcript.CommitFlush();
            _stateDirty = false;
            return true;
        }
        return false;
    }

    private void DisposePersistTimer()
    {
        _persistTimer?.Dispose();
        _persistTimer = null;
    }

    public async Task<AgentSessionInfo?> GetAsync()
    {
        return _session is null ? null : await ToInfoAsync(_session);
    }

    private async Task<AgentSession> GetRequiredAsync()
    {
        if (_session is not null) return _session;

        _session = await _stateStore.LoadAsync(SessionId);
        return _session ?? throw new InvalidOperationException($"Agent session {SessionId} does not exist.");
    }

    private async Task CommitAsync(AgentSession session, IReadOnlyList<AgentSessionEvent> events)
    {
        await _stateStore.SaveAsync(SessionId, session, events);
        _session = session;

        var source = AgentSessionSource(session);
        foreach (var domainEvent in events)
        {
            try
            {
                await PublishAgentSessionLifecycleAsync(domainEvent, source, session.Id);
            }
            catch (InvalidOperationException)
            {
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "AgentSessionGrain failed to publish lifecycle event for {SessionId}",
                    session.Id);
            }
        }
    }

    private static string AgentSessionSource(AgentSession session) =>
        $"/mohist/agent-session/{session.Id}";

    private async Task PublishAgentSessionLifecycleAsync(AgentSessionEvent? domainEvent, string source, string subject)
    {
        if (domainEvent is null) return;
        var concrete = domainEvent.Value;
        var type = AgentSessionEventSerializer.BusType(concrete);
        var data = AgentSessionEventSerializer.ToData(concrete);
        await _eventPublisher.PublishAsync(
            data,
            type,
            source,
            subject: subject,
            ct: CancellationToken.None);
    }

    private async Task<AgentSessionInfo> ToInfoAsync(AgentSession s)
    {
        var eventSummary = await LoadEventSummaryAsync(s.Id);
        var usage = AgentSessionJsonHelper.Usage(s);
        return new AgentSessionInfo(
        s.Id,
        s.Runtime.RunnerId,
        s.Status.AgentRuntimeSessionId,
        AgentSessionJsonHelper.StatusName(s),
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
            eventSummary.ToolErrorCount);
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
        var payload = JsonSerializer.Deserialize<JsonElement>(runtimeEvent.PayloadJson);
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
                AgentSessionJsonHelper.GetContextWindowSize(payload)),
            RuntimeEventTypes.ModelResolved => session.ResolveModel(
                AgentSessionJsonHelper.GetStringProp(payload, "resolvedModel") ?? AgentSessionJsonHelper.GetStringProp(payload, "model"),
                now),
            _ => []
        };
    }
}
