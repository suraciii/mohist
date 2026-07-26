using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Shared loader for the transcript turns/parts read sequence used by the
/// five former duplication sites. Returns the
/// raw materials — the loaded turn rows, a turn-id → session-id dictionary,
/// and the materialized parts list (optionally pre-filtered by part type so SQL stays the
/// filter boundary) — and lets each caller impose its own ordering and
/// last-wins reduction. Pure refactor: every caller's resolved projection
/// is byte-identical to the pre-consolidation result.
/// </summary>
internal static class TranscriptPartLoader
{
    /// <summary>
    /// Loads the transcript turn rows for <paramref name="sessionIds"/>, then
    /// the parts for those turn ids (optionally restricted to a single
    /// <paramref name="partType"/>), and returns the turn-id → session-id
    /// map alongside the loaded turns and materialized parts list. Returns
    /// empty collections when <paramref name="sessionIds"/> is empty. When
    /// <paramref name="sessionIds"/> is non-empty but no turns exist for it,
    /// returns an empty map and an empty list (no empty-marker records).
    /// SQL ordering is intentionally not imposed: callers apply their own
    /// <c>OrderBy</c> via LINQ-to-Objects so identical observable projections
    /// survive the consolidation. Single-session callers pass a one-element
    /// <c>string[]</c>.
    /// </summary>
    internal static async Task<TranscriptPartLoaderResult> LoadAsync(
        MohistDbContext db,
        IEnumerable<string> sessionIds,
        CancellationToken ct = default,
        string? partType = null)
    {
        var ids = sessionIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return TranscriptPartLoaderResult.Empty;

        var turns = await db.AgentSessionTranscriptTurns.AsNoTracking()
            .Where(t => ids.Contains(t.SessionId))
            .ToListAsync(ct);
        var turnIds = turns.Select(t => t.Id).ToArray();
        if (turnIds.Length == 0) return TranscriptPartLoaderResult.Empty;

        var sessionByTurnId = turns.ToDictionary(t => t.Id, t => t.SessionId);

        var partsQuery = db.AgentSessionTranscriptParts.AsNoTracking()
            .Where(e => turnIds.Contains(e.TurnId));
        if (partType is not null)
            partsQuery = partsQuery.Where(e => e.Type == partType);

        var parts = await partsQuery.ToListAsync(ct);

        return new TranscriptPartLoaderResult(turns, sessionByTurnId, parts);
    }
}

internal readonly record struct TranscriptPartLoaderResult(
    IReadOnlyList<AgentSessionTranscriptTurnRow> Turns,
    IReadOnlyDictionary<long, string> SessionByTurnId,
    IReadOnlyList<AgentSessionTranscriptPartRow> Parts)
{
    public static TranscriptPartLoaderResult Empty { get; } =
        new(Array.Empty<AgentSessionTranscriptTurnRow>(), new Dictionary<long, string>(0), Array.Empty<AgentSessionTranscriptPartRow>());
}
