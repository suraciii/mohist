using Microsoft.EntityFrameworkCore;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
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
}
