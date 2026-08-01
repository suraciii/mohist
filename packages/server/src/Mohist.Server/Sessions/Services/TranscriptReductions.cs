using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

internal static class TranscriptReductions
{
    internal static async Task<Dictionary<string, AgentSessionTranscriptSummary>> LoadEventSummariesAsync(
        MohistDbContext db,
        IEnumerable<string> sessionIds,
        CancellationToken ct) =>
        (await LoadEventSummariesWithCountAsync(db, sessionIds, ct)).Summaries;

    internal static async Task<TranscriptSummaryLoad> LoadEventSummariesWithCountAsync(
        MohistDbContext db,
        IEnumerable<string> sessionIds,
        CancellationToken ct)
    {
        var loaded = await TranscriptPartLoader.LoadAsync(db, sessionIds, ct: ct);
        if (loaded.Parts.Count == 0) return new([], 0);

        var turnSequenceByTurnId = loaded.Turns.ToDictionary(t => t.Id, t => t.Sequence);
        var summaries = loaded.Parts
            .Where(part => loaded.SessionByTurnId.ContainsKey(part.TurnId))
            .Select(part => AgentSessionDtoMapper.ToProjection(loaded.SessionByTurnId[part.TurnId], part))
            .OrderBy(e => turnSequenceByTurnId.GetValueOrDefault(e.TurnId, long.MaxValue))
            .ThenBy(e => e.Sequence)
            .ThenBy(e => e.Id)
            .GroupBy(e => e.SessionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => TranscriptEventSummaryProjector.Summarize(
                    group.Select(e => new TranscriptSummaryEvent(
                        TurnSequence: turnSequenceByTurnId.GetValueOrDefault(e.TurnId, 0),
                        Sequence: e.Sequence,
                        PartId: e.Id.ToString(),
                        Type: e.Type,
                        PayloadJson: e.PayloadJson))),
                StringComparer.Ordinal);

        return new(summaries, loaded.Parts.Count);
    }
}

internal readonly record struct TranscriptSummaryLoad(
    Dictionary<string, AgentSessionTranscriptSummary> Summaries,
    long TranscriptRecords);
