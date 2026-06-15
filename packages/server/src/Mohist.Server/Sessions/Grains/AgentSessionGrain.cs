using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Domain.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed class AgentSessionGrain : Grain, IAgentSessionGrain
{
    private readonly IAgentSessionStore _stateStore;
    private readonly IAgentSessionTranscriptStore _transcriptStore;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IEventPublisher _eventPublisher;
    private readonly ITranscriptEventPublisher _transcriptPublisher;
    private readonly ILogger<AgentSessionGrain> _log;
    private readonly TranscriptAccumulator _transcript = new();
    private AgentSession? _session;
    private long _realtimeSequence;

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
        if (_session is null || !_transcript.HasPending)
            return;

        var now = DateTime.UtcNow;
        var transcript = _transcript.Flush(_session, now);
        if (transcript is null)
            return;

        await _transcriptStore.SaveAsync(transcript, ct);
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

        var entries = new List<RuntimeEventEnvelope>();
        foreach (var e in command.RuntimeEvents)
        {
            events.AddRange(ApplyRuntimeEventToDomain(session, e, now));

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

        var transcript = _transcript.Accept(
            session,
            entries,
            now,
            forceFlushPending: command.RuntimeEvents.Count > 1);

        await _stateStore.SaveAsync(SessionId, session, events);
        if (transcript is not null)
            await _transcriptStore.SaveAsync(transcript);
        _session = session;

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

    private async Task<AgentSessionEventSummary> LoadEventSummaryAsync(string sessionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var turnIds = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.Id)
            .ToListAsync();
        var transcriptParts = turnIds.Count == 0
            ? new List<AgentSessionTranscriptPartRow>()
            : await db.AgentSessionTranscriptParts.AsNoTracking()
                .Where(e => turnIds.Contains(e.TurnId))
                .OrderBy(e => e.Sequence)
                .ToListAsync();
        var events = transcriptParts
            .Select(ToEventEnvelope)
            .OrderBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .ToList();

        string? resolvedModel = null;
        string? failureCategory = null;
        var toolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var failedToolCallIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in events)
        {
            if (e.Type == "model.resolved" || e.Type == "model")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                resolvedModel = AgentSessionJsonHelper.GetStringProp(payload, "resolvedModel") ?? resolvedModel;
            }
            else if (e.Type == "session.closed" || e.Type == "session_closed")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                failureCategory = AgentSessionJsonHelper.GetStringProp(payload, "failureCategory") ?? failureCategory;
            }
            else if (e.Type == "tool_call.started" || e.Type == "tool_call.updated" || e.Type == "tool_call.completed" || e.Type == "tool_call" || e.Type == "tool")
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.PayloadJson);
                var toolCallId = AgentSessionJsonHelper.GetToolStringProp(payload, "toolCallId")
                    ?? AgentSessionJsonHelper.GetToolStringProp(payload, "id")
                    ?? AgentSessionJsonHelper.GetToolStringProp(payload, "callId")
                    ?? e.Sequence.ToString();
                toolCallIds.Add(toolCallId);
                var status = AgentSessionJsonHelper.GetToolStringProp(payload, "status") ?? AgentSessionJsonHelper.GetToolStringProp(payload, "state");
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    failedToolCallIds.Add(toolCallId);
            }
        }

        return new AgentSessionEventSummary(
            resolvedModel,
            failureCategory,
            toolCallIds.Count == 0 ? null : toolCallIds.Count,
            failedToolCallIds.Count == 0 ? null : failedToolCallIds.Count);
    }

    private static AgentSessionRuntimeEventInfo ToEventInfo(RuntimeEventEnvelope e) => new(
        e.Id.ToString(),
        e.SessionId,
        e.AgentSessionId,
        e.Sequence,
        e.Type,
        e.PayloadJson,
        e.CreatedAt.ToString("o"));

    private static RuntimeEventEnvelope ToEventEnvelope(AgentSessionTranscriptPartRow part) => new()
    {
        Id = part.Id,
        Sequence = part.Sequence,
        Type = part.Type,
        PayloadJson = part.PayloadJson,
        CreatedAt = part.FirstSeenAt,
    };

    private static IReadOnlyList<AgentSessionEvent> ApplyRuntimeEventToDomain(
        AgentSession session,
        AgentSessionRuntimeEventInput runtimeEvent,
        DateTime now)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(runtimeEvent.PayloadJson);
        return runtimeEvent.Type switch
        {
            "session.liveness" => session.RecordActivity(now),
            "session.closed" => session.RecordActivity(now),
            "usage.updated" => session.ApplyUsage(
                AgentSessionJsonHelper.GetLongProp(payload, "inputTokens"),
                AgentSessionJsonHelper.GetLongProp(payload, "outputTokens"),
                AgentSessionJsonHelper.GetLongProp(payload, "totalTokens"),
                AgentSessionJsonHelper.GetLongProp(payload, "cachedReadTokens"),
                AgentSessionJsonHelper.GetLongProp(payload, "thoughtTokens"),
                AgentSessionJsonHelper.GetCostAmount(payload),
                AgentSessionJsonHelper.GetCostCurrency(payload),
                AgentSessionJsonHelper.GetContextWindowUsed(payload),
                AgentSessionJsonHelper.GetContextWindowSize(payload)),
            "model.resolved" => session.ResolveModel(
                AgentSessionJsonHelper.GetStringProp(payload, "resolvedModel") ?? AgentSessionJsonHelper.GetStringProp(payload, "model"),
                now),
            _ => []
        };
    }

    private sealed record AgentSessionEventSummary(
        string? ResolvedModel,
        string? FailureCategory,
        int? ToolCallCount,
        int? ToolErrorCount);
}
