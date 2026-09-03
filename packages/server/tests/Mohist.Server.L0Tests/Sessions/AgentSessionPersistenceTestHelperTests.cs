using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.L0Tests.Sessions;

[Trait("level", "L0")]
public class AgentSessionPersistenceTestHelperTests
{
    [Fact]
    public async Task WaitForTranscriptPartsAsync_WaitsPastCompletedCycleUntilExpectedCountExists()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        MigratedSqliteTemplate.CopyModelSchemaTo(connection);

        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestContextFactory(options);
        const string sessionId = "session-1";

        await AddTranscriptPartAsync(factory, sessionId, partId: 1, sequence: 1);

        var probe = new AgentSessionPersistenceTestProbe();
        var checkpoint = probe.Checkpoint(sessionId);
        var firstCycleId = probe.StartCycle(sessionId);
        probe.Report(new AgentSessionPersistenceResult(
            sessionId,
            firstCycleId,
            AgentSessionPersistenceOutcome.Succeeded));

        var wait = factory.WaitForTranscriptPartsAsync(sessionId, 2, checkpoint);

        await AddTranscriptPartAsync(factory, sessionId, partId: 2, sequence: 2);
        var secondCycleId = probe.StartCycle(sessionId);
        probe.Report(new AgentSessionPersistenceResult(
            sessionId,
            secondCycleId,
            AgentSessionPersistenceOutcome.Succeeded));

        await wait;
    }

    private static async Task AddTranscriptPartAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sessionId,
        long partId,
        long sequence)
    {
        await using var db = await factory.CreateDbContextAsync();
        if (!await db.AgentSessionTranscriptTurns.AnyAsync(turn => turn.SessionId == sessionId))
        {
            db.AgentSessionTranscriptTurns.Add(new AgentSessionTranscriptTurnRow
            {
                Id = 1,
                SessionId = sessionId,
                Sequence = 1,
            });
        }

        db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
        {
            Id = partId,
            TurnId = 1,
            Sequence = sequence,
            Type = "runtime-event",
            CorrelationKey = $"part-{partId}",
            RawEventCount = 1,
        });
        await db.SaveChangesAsync();
    }

    private sealed class TestContextFactory(DbContextOptions<MohistDbContext> options)
        : IDbContextFactory<MohistDbContext>
    {
        public MohistDbContext CreateDbContext() => new(options);
    }
}
