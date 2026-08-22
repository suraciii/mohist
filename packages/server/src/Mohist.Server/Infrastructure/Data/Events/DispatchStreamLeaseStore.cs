using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Events;

/// <summary>
/// Stream lease arbitration for dispatch workers. Claim, park, touch, and
/// release are each one SQL statement so SQLite's serialized writer makes
/// them atomic without an explicit transaction. Every mutation is
/// owner-checked: a worker that lost its lease observes zero changed rows
/// and stops touching the stream.
/// </summary>
public interface IDispatchStreamLeaseStore
{
    Task<int?> ClaimAsync(
        string origin,
        string source,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<bool> ParkAsync(
        string origin,
        string source,
        string owner,
        int attempts,
        DateTimeOffset nextAttemptAt,
        string? lastError,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task<bool> TouchAsync(
        string origin,
        string source,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task<bool> ResetAttemptsAsync(
        string origin,
        string source,
        string owner,
        DateTimeOffset now,
        CancellationToken ct = default);

    Task ReleaseAsync(
        string origin,
        string source,
        string owner,
        CancellationToken ct = default);

    Task<int> CountParkedAsync(DateTimeOffset now, CancellationToken ct = default);
}

public sealed class DispatchStreamLeaseStore : IDispatchStreamLeaseStore
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;

    public DispatchStreamLeaseStore(IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Claims the stream for <paramref name="owner"/> when no live lease or
    /// backoff parking holds it. An expired lease is stolen. Returns the
    /// attempts carried over from the previous holder, or null when the
    /// claim lost.
    /// </summary>
    async Task<int?> IDispatchStreamLeaseStore.ClaimAsync(
        string origin,
        string source,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var steal = new SqliteParameter[]
        {
            new("@owner", owner),
            new("@until", now + leaseDuration),
            new("@now", now),
            new("@origin", origin),
            new("@source", source),
        };
        var stolen = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "DispatchStreamLeases"
            SET "LeaseOwner" = @owner, "LeaseUntil" = @until, "NextAttemptAt" = NULL, "UpdatedAt" = @now
            WHERE "Origin" = @origin AND "Source" = @source
              AND ("LeaseUntil" <= @now OR "LeaseOwner" = @owner)
              AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= @now)
            """,
            steal,
            ct);
        if (stolen == 1)
        {
            var attempts = await db.DispatchStreamLeases.AsNoTracking()
                .Where(l => l.Origin == origin && l.Source == source)
                .Select(l => l.Attempts)
                .FirstAsync(ct);
            return attempts;
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "DispatchStreamLeases"
                    ("Origin", "Source", "LeaseOwner", "LeaseUntil", "Attempts", "NextAttemptAt", "LastError", "UpdatedAt")
                VALUES (@origin, @source, @owner, @until, 0, NULL, NULL, @now)
                """,
                steal,
                ct);
            return 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // Primary-key conflict: another worker inserted first.
            return null;
        }
    }

    /// <summary>
    /// Parks the claimed stream after a failed delivery attempt: records
    /// the attempt budget and expires the lease at the backoff gate, so any
    /// worker may reclaim the stream exactly when the next attempt is due.
    /// Returns false when the lease was lost and the caller must stop
    /// draining.
    /// </summary>
    async Task<bool> IDispatchStreamLeaseStore.ParkAsync(
        string origin,
        string source,
        string owner,
        int attempts,
        DateTimeOffset nextAttemptAt,
        string? lastError,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "DispatchStreamLeases"
            SET "Attempts" = @attempts, "NextAttemptAt" = @next, "LeaseUntil" = @next, "LastError" = @error, "UpdatedAt" = @now
            WHERE "Origin" = @origin AND "Source" = @source AND "LeaseOwner" = @owner
            """,
            new SqliteParameter[]
            {
                new("@attempts", attempts),
                new("@next", nextAttemptAt),
                new("@error", lastError is null ? DBNull.Value : lastError),
                new("@now", now),
                new("@origin", origin),
                new("@source", source),
                new("@owner", owner),
            },
            ct);
        return changed == 1;
    }

    /// <summary>Extends the lease for a long drain. Owner-checked.</summary>
    async Task<bool> IDispatchStreamLeaseStore.TouchAsync(
        string origin,
        string source,
        string owner,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "DispatchStreamLeases"
            SET "LeaseUntil" = @until, "UpdatedAt" = @now
            WHERE "Origin" = @origin AND "Source" = @source AND "LeaseOwner" = @owner
            """,
            new SqliteParameter[]
            {
                new("@until", now + leaseDuration),
                new("@now", now),
                new("@origin", origin),
                new("@source", source),
                new("@owner", owner),
            },
            ct);
        return changed == 1;
    }

    /// <summary>
    /// Zeroes the attempt budget after the parked head dead-letters and the
    /// stream advances to a new head. Owner-checked.
    /// </summary>
    async Task<bool> IDispatchStreamLeaseStore.ResetAttemptsAsync(
        string origin,
        string source,
        string owner,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "DispatchStreamLeases"
            SET "Attempts" = 0, "NextAttemptAt" = NULL, "LastError" = NULL, "UpdatedAt" = @now
            WHERE "Origin" = @origin AND "Source" = @source AND "LeaseOwner" = @owner
            """,
            new SqliteParameter[]
            {
                new("@now", now),
                new("@origin", origin),
                new("@source", source),
                new("@owner", owner),
            },
            ct);
        return changed == 1;
    }

    /// <summary>
    /// Deletes the lease when the stream drained clean. The stream's
    /// attempt budget dies with it; a later failure starts from zero.
    /// </summary>
    async Task IDispatchStreamLeaseStore.ReleaseAsync(
        string origin,
        string source,
        string owner,
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM "DispatchStreamLeases"
            WHERE "Origin" = @origin AND "Source" = @source AND "LeaseOwner" = @owner
            """,
            new SqliteParameter[]
            {
                new("@origin", origin),
                new("@source", source),
                new("@owner", owner),
            },
            ct);
    }

    /// <summary>Number of streams parked in backoff — the blocked-streams
    /// operational signal, durable and exact.</summary>
    async Task<int> IDispatchStreamLeaseStore.CountParkedAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DispatchStreamLeases.AsNoTracking()
            .CountAsync(l => l.NextAttemptAt != null && l.NextAttemptAt > now, ct);
    }
}
