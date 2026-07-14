using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

internal static class TranscriptReductions
{
    internal static async Task<Dictionary<string, AgentSessionTranscriptSummary>> LoadEventSummariesAsync(
        MohistDbContext db,
        IEnumerable<string> sessionIds,
        CancellationToken ct)
    {
        var loaded = await TranscriptPartLoader.LoadAsync(db, sessionIds, ct: ct);
        if (loaded.Parts.Count == 0) return [];

        return loaded.Parts
            .Where(part => loaded.SessionByTurnId.ContainsKey(part.TurnId))
            .Select(part => AgentSessionDtoMapper.ToProjection(loaded.SessionByTurnId[part.TurnId], part))
            .OrderBy(e => e.Sequence)
            .GroupBy(e => e.SessionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => TranscriptEventSummaryProjector.Summarize(
                    group.Select(e => new TranscriptSummaryEvent(e.Sequence, e.Type, e.PayloadJson))),
                StringComparer.Ordinal);
    }
}
