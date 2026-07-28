using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.SpecTests.Support;

public static class AgentSessionPersistenceTestHelper
{
    public static async Task WaitForTranscriptPartsAsync(
        this IDbContextFactory<MohistDbContext> dbFactory,
        string sessionId,
        int expectedCount,
        IGrainFactory grains,
        AgentSessionPersistenceTestProbe persistence)
    {
        await grains.GetGrain<IAgentSessionGrain>(sessionId).WaitForPersistenceAsync(persistence);
        var count = await CountTranscriptPartsAsync(dbFactory, sessionId);
        if (count < expectedCount)
            throw new InvalidOperationException(
                $"Expected at least {expectedCount} transcript part(s) for session {sessionId}, but found {count}");
    }

    private static async Task<int> CountTranscriptPartsAsync(IDbContextFactory<MohistDbContext> dbFactory, string sessionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var turnIds = await db.AgentSessionTranscriptTurns
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .Select(t => t.Id)
            .ToListAsync();
        return turnIds.Count == 0
            ? 0
            : await db.AgentSessionTranscriptParts
                .AsNoTracking()
                .Where(p => turnIds.Contains(p.TurnId))
                .CountAsync();
    }
}
