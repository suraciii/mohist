using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Issue-327 T-002 / design D3: locks in the consolidated
/// <see cref="TranscriptPartLoader.LoadAsync(MohistDbContext, IEnumerable{string}, CancellationToken, string?)"/>
/// helper — the single transcript turns/parts load sequence (turns → turnIds
/// → parts → sessionByTurnId map) used by the five former duplication
/// sites. Verifies empty input, multi-session reshape, single-session
/// narrow, and part-type SQL filtering. Pure refactor: ordering is left
/// to each caller (per design D3 — "loader returns raw materials, not
/// pre-reduced, so each caller imposes its own ordering") so the
/// observable projections across the read side remain byte-identical to
/// the pre-consolidation results.
/// </summary>
public sealed class TranscriptPartLoaderSpecs
{
    private static readonly DateTime FixedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task LoadAsync_EmptySessionIds_ReturnsEmptyResult()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await using var db = fixture.CreateDbContext();

        var result = await TranscriptPartLoader.LoadAsync(db, Array.Empty<string>());

        Assert.Empty(result.SessionByTurnId);
        Assert.Empty(result.Turns);
        Assert.Empty(result.Parts);
    }

    [Fact]
    public async Task LoadAsync_DuplicateSessionIds_AreDedupedBeforeQuery()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        var now = FixedTime;
        await SeedSessionAsync(fixture, "sess_a", now);
        await SeedSessionAsync(fixture, "sess_b", now);
        await using var db = fixture.CreateDbContext();

        var result = await TranscriptPartLoader.LoadAsync(
            db,
            new[] { "sess_a", "sess_a", "sess_b", "sess_b" });

        Assert.Equal(2, result.SessionByTurnId.Count);
        Assert.Equal(2, result.Turns.Count);
        Assert.Empty(result.Parts);
        Assert.Contains(1L, result.SessionByTurnId.Keys);
        Assert.Contains(2L, result.SessionByTurnId.Keys);
    }

    [Fact]
    public async Task LoadAsync_MultipleSessions_ReturnsSessionByTurnId_AndAllParts()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        var now = FixedTime;
        await SeedSessionAsync(fixture, "sess_a", now, parts: new[]
        {
            ("text", "{}", now.AddSeconds(1)),
            ("tool", "{}", now.AddSeconds(2)),
        });
        await SeedSessionAsync(fixture, "sess_b", now, parts: new[]
        {
            ("status", "{}", now.AddSeconds(1)),
        });
        await using var db = fixture.CreateDbContext();

        var result = await TranscriptPartLoader.LoadAsync(
            db,
            new[] { "sess_a", "sess_b" });

        Assert.Equal(2, result.SessionByTurnId.Count);
        Assert.Equal(2, result.Turns.Count);
        Assert.Equal(3, result.Parts.Count);
        Assert.All(result.Parts, p => Assert.True(result.SessionByTurnId.ContainsKey(p.TurnId)));
        Assert.All(result.Parts, p => Assert.Contains(result.Turns, t => t.Id == p.TurnId));
    }

    [Fact]
    public async Task LoadAsync_OnlySessionIdsWithoutTurns_ReturnsEmptyResult()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        await using var db = fixture.CreateDbContext();

        var result = await TranscriptPartLoader.LoadAsync(
            db,
            new[] { "sess_missing" });

        Assert.Empty(result.SessionByTurnId);
        Assert.Empty(result.Turns);
        Assert.Empty(result.Parts);
    }

    [Fact]
    public async Task LoadAsync_PartTypeFilter_ReturnsOnlyMatchingParts()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        var now = FixedTime;
        await SeedSessionAsync(fixture, "sess_a", now, parts: new[]
        {
            ("session.closed", "{}", now.AddSeconds(1)),
            ("text", "{}", now.AddSeconds(2)),
            ("session.closed", "{}", now.AddSeconds(3)),
        });
        await using var db = fixture.CreateDbContext();

        var result = await TranscriptPartLoader.LoadAsync(
            db,
            new[] { "sess_a" },
            partType: TranscriptPartTypes.SessionClosed);

        Assert.Single(result.SessionByTurnId);
        Assert.Single(result.Turns);
        Assert.Equal(2, result.Parts.Count);
        Assert.All(result.Parts, p => Assert.Equal(TranscriptPartTypes.SessionClosed, p.Type));
    }

    [Fact]
    public async Task LoadAsync_PartTypeFilter_NoMatches_ReturnsEmptyPartsList()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        var now = FixedTime;
        await SeedSessionAsync(fixture, "sess_a", now, parts: new[]
        {
            ("text", "{}", now.AddSeconds(1)),
        });
        await using var db = fixture.CreateDbContext();

        var result = await TranscriptPartLoader.LoadAsync(
            db,
            new[] { "sess_a" },
            partType: TranscriptPartTypes.SessionClosed);

        Assert.Single(result.SessionByTurnId);
        Assert.Single(result.Turns);
        Assert.Empty(result.Parts);
    }

    [Fact]
    public async Task LoadAsync_DoesNotImposeOrderingOnMaterializedParts()
    {
        using var database = TestSqliteDatabase.CreateMigrated();
        var fixture = new TestDbContextFactory(database.Options);
        var now = FixedTime;
        await SeedSessionAsync(fixture, "sess_a", now, parts: new[]
        {
            ("a", "{}", now.AddSeconds(5)),
            ("b", "{}", now.AddSeconds(1)),
            ("c", "{}", now.AddSeconds(3)),
        });
        await using var db = fixture.CreateDbContext();

        var result = await TranscriptPartLoader.LoadAsync(
            db,
            new[] { "sess_a" });

        Assert.Equal(new[] { "a", "b", "c" }, result.Parts.Select(p => p.Type).ToArray());
    }

    private static async Task SeedSessionAsync(
        IDbContextFactory<MohistDbContext> factory,
        string sessionId,
        DateTime baseTime,
        (string Type, string Payload, DateTime LastSeenAt)[]? parts = null)
    {
        await using var db = factory.CreateDbContext();
        db.AgentSessionTranscriptTurns.Add(new AgentSessionTranscriptTurnRow
        {
            SessionId = sessionId,
            Sequence = 1,
            StartedAt = baseTime,
            UpdatedAt = baseTime,
        });
        await db.SaveChangesAsync();

        if (parts is { Length: > 0 })
        {
            var turn = db.AgentSessionTranscriptTurns.Single(t => t.SessionId == sessionId);
            for (var i = 0; i < parts.Length; i++)
            {
                var (type, payload, lastSeenAt) = parts[i];
                db.AgentSessionTranscriptParts.Add(new AgentSessionTranscriptPartRow
                {
                    TurnId = turn.Id,
                    Sequence = i + 1,
                    Type = type,
                    CorrelationKey = $"key_{i}",
                    PayloadJson = payload,
                    LastSeenAt = lastSeenAt,
                });
            }
            await db.SaveChangesAsync();
        }
    }
}
