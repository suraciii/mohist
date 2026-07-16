using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Sessions;

public interface IAgentSessionTranscriptStore
{
    Task SaveAsync(AgentSessionTranscriptFlush transcript, CancellationToken ct = default);
}

public sealed record AgentSessionTranscriptFlush(
    bool StartNewTurn,
    AgentSessionTranscriptTurnUpsert Turn,
    IReadOnlyList<AgentSessionTranscriptPartDelta> Parts);

public sealed record AgentSessionTranscriptTurnUpsert(
    string SessionId,
    long Sequence,
    string PromptText,
    string PromptKind,
    DateTime StartedAt,
    DateTime UpdatedAt,
    string? RuntimeSessionId = null);

public sealed record AgentSessionTranscriptPartDelta(
    string Type,
    string CorrelationKey,
    string? CorrelationId,
    string? TextDelta,
    string PayloadJson,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    int RawEventCount);

public sealed class AgentSessionTranscriptStore : IAgentSessionTranscriptStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public AgentSessionTranscriptStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task SaveAsync(AgentSessionTranscriptFlush transcript, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var turn = transcript.StartNewTurn
                ? null
                : await db.AgentSessionTranscriptTurns
                    .OrderByDescending(t => t.Sequence)
                    .FirstOrDefaultAsync(t => t.SessionId == transcript.Turn.SessionId, ct);
            if (turn is null)
            {
                var sequence = transcript.StartNewTurn || transcript.Turn.Sequence <= 0
                    ? (await db.AgentSessionTranscriptTurns
                        .Where(t => t.SessionId == transcript.Turn.SessionId)
                        .Select(t => (long?)t.Sequence)
                        .MaxAsync(ct) ?? 0) + 1
                    : transcript.Turn.Sequence;
                turn = new AgentSessionTranscriptTurnRow
                {
                    SessionId = transcript.Turn.SessionId,
                    RuntimeSessionId = transcript.Turn.RuntimeSessionId,
                    Sequence = sequence,
                    PromptText = transcript.Turn.PromptText,
                    PromptKind = transcript.Turn.PromptKind,
                    StartedAt = transcript.Turn.StartedAt,
                    UpdatedAt = transcript.Turn.UpdatedAt,
                };
                db.AgentSessionTranscriptTurns.Add(turn);
                await db.SaveChangesAsync(ct);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(transcript.Turn.PromptText))
                    turn.PromptText = transcript.Turn.PromptText;
                if (!string.IsNullOrWhiteSpace(transcript.Turn.RuntimeSessionId))
                    turn.RuntimeSessionId = transcript.Turn.RuntimeSessionId;
                turn.PromptKind = transcript.Turn.PromptKind;
                turn.UpdatedAt = transcript.Turn.UpdatedAt;
            }

            await SavePartsAsync(db, turn.Id, transcript.Parts, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task SavePartsAsync(
        MohistDbContext db,
        long turnId,
        IReadOnlyList<AgentSessionTranscriptPartDelta> parts,
        CancellationToken ct)
    {
        if (parts.Count == 0)
            return;

        var types = parts.Select(p => p.Type).Distinct(StringComparer.Ordinal).ToArray();
        var keys = parts.Select(p => p.CorrelationKey).Distinct(StringComparer.Ordinal).ToArray();
        var existingParts = await db.AgentSessionTranscriptParts
            .Where(p => p.TurnId == turnId && types.Contains(p.Type) && keys.Contains(p.CorrelationKey))
            .ToListAsync(ct);
        var existingByKey = existingParts.ToDictionary(p => PartKey(p.Type, p.CorrelationKey), StringComparer.Ordinal);
        var nextSequence = (await db.AgentSessionTranscriptParts
            .Where(p => p.TurnId == turnId)
            .Select(p => (long?)p.Sequence)
            .MaxAsync(ct) ?? 0) + 1;

        foreach (var delta in parts)
        {
            var partKey = PartKey(delta.Type, delta.CorrelationKey);
            if (existingByKey.TryGetValue(partKey, out var part))
            {
                part.CorrelationId = delta.CorrelationId ?? part.CorrelationId;
                if (!string.IsNullOrEmpty(delta.TextDelta))
                    part.Text += delta.TextDelta;
                part.PayloadJson = string.IsNullOrWhiteSpace(delta.PayloadJson) ? part.PayloadJson : delta.PayloadJson;
                part.LastSeenAt = delta.LastSeenAt;
                part.RawEventCount += Math.Max(0, delta.RawEventCount);
                continue;
            }

            part = new AgentSessionTranscriptPartRow
            {
                TurnId = turnId,
                Sequence = nextSequence++,
                Type = delta.Type,
                CorrelationKey = delta.CorrelationKey,
                CorrelationId = delta.CorrelationId,
                Text = delta.TextDelta ?? string.Empty,
                PayloadJson = string.IsNullOrWhiteSpace(delta.PayloadJson) ? "{}" : delta.PayloadJson,
                FirstSeenAt = delta.FirstSeenAt,
                LastSeenAt = delta.LastSeenAt,
                RawEventCount = Math.Max(0, delta.RawEventCount),
            };
            db.AgentSessionTranscriptParts.Add(part);
            existingByKey[partKey] = part;
        }
    }

    private static string PartKey(string type, string correlationKey) => $"{type}\u001f{correlationKey}";
}
