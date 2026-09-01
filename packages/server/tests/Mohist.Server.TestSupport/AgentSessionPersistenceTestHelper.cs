using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.TestSupport;

public static class AgentSessionPersistenceTestHelper
{
    public static async Task WaitForTranscriptPartsAsync(
        this IDbContextFactory<MohistDbContext> dbFactory,
        string sessionId,
        int expectedCount,
        AgentSessionPersistenceCheckpoint persistence)
    {
        var checkpoint = persistence;
        while (true)
        {
            var count = await CountTranscriptPartsAsync(dbFactory, sessionId);
            if (count >= expectedCount)
                return;

            var result = await checkpoint.WaitAsync();
            if (result.Outcome != AgentSessionPersistenceOutcome.Succeeded)
                throw new InvalidOperationException(
                    $"Persistence cycle {result.CycleId} for session {sessionId} completed with {result.Outcome}");

            checkpoint = checkpoint with { CycleId = result.CycleId };
        }
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
