using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Tests.Support;

public static class AgentSessionPersistenceTestHelper
{
    public static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(2);

    public static async Task WaitForTranscriptPartsAsync(
        this IDbContextFactory<MohistDbContext> dbFactory,
        string sessionId,
        int expectedCount,
        TimeSpan? timeout = null)
    {
        var maxWait = timeout ?? DefaultFlushTimeout;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < maxWait)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var turnIds = await db.AgentSessionTranscriptTurns
                .AsNoTracking()
                .Where(t => t.SessionId == sessionId)
                .Select(t => t.Id)
                .ToListAsync();
            var count = turnIds.Count == 0
                ? 0
                : await db.AgentSessionTranscriptParts
                    .AsNoTracking()
                    .Where(p => turnIds.Contains(p.TurnId))
                    .CountAsync();
            if (count >= expectedCount)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"Expected at least {expectedCount} transcript part(s) for session {sessionId} within {maxWait.TotalSeconds}s.");
    }
}
