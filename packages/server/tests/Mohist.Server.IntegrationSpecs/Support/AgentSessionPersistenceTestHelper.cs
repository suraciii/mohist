using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.IntegrationSpecs.Support;

public static class AgentSessionPersistenceTestHelper
{
    public static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultFlushStep = TimeSpan.FromMilliseconds(50);

    public static Task WaitForTranscriptPartsAsync(
        this IDbContextFactory<MohistDbContext> dbFactory,
        string sessionId,
        int expectedCount,
        IGrainFactory grains,
        TimeSpan? timeout = null)
        => WaitForTranscriptPartsAsync(
            dbFactory,
            sessionId,
            expectedCount,
            timeout,
            async () => await grains.GetGrain<IAgentSessionGrain>(sessionId).FlushForTestAsync());

    public static Task WaitForTranscriptPartsAsync(
        this IDbContextFactory<MohistDbContext> dbFactory,
        string sessionId,
        int expectedCount,
        FakeTimeProvider timeProvider,
        TimeSpan? timeout = null)
        => WaitForTranscriptPartsAsync(
            dbFactory,
            sessionId,
            expectedCount,
            timeout,
            () =>
            {
                timeProvider.Advance(TimeSpan.FromMilliseconds(250));
                return Task.CompletedTask;
            });

    public static async Task WaitForTranscriptPartsAsync(
        this IDbContextFactory<MohistDbContext> dbFactory,
        string sessionId,
        int expectedCount,
        TimeSpan? timeout = null,
        Func<Task>? advance = null)
    {
        var maxWait = timeout ?? DefaultFlushTimeout;
        await TestWait.ForAsync(
            async () => await CountTranscriptPartsAsync(dbFactory, sessionId),
            count => count >= expectedCount,
            maxWait,
            DefaultFlushStep,
            $"at least {expectedCount} transcript part(s) for session {sessionId}",
            advance);
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
