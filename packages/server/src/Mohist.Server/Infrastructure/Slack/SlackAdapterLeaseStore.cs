using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Slack.Domain;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// EF-backed, durable <see cref="ISlackLeaseStore"/>. Each mutation is a
/// single atomic statement that encodes its own fencing predicate, so a
/// superseded or expired lease can never renew or confirm hello even under
/// concurrent adapters.
/// </summary>
public sealed class SlackAdapterLeaseStore(
    IDbContextFactory<MohistDbContext> dbFactory) : ISlackLeaseStore, IScopedService
{
    public async Task<SlackLeaseRecord> IssueAsync(
        string targetKey, string kind, string adapterId, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ValidateKind(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "SlackAdapterLeases" (
                "TargetKey", "Generation", "LeaseId", "LeaseKind", "AdapterId", "IssuedAt", "ExpiresAt", "UpdatedAt")
            VALUES (
                {targetKey}, 1, {NewLeaseId()}, {kind}, {adapterId}, {now}, {expiresAt}, {now})
            ON CONFLICT("TargetKey") DO UPDATE SET
                "Generation" = "SlackAdapterLeases"."Generation" + 1,
                "LeaseId" = excluded."LeaseId",
                "LeaseKind" = excluded."LeaseKind",
                "AdapterId" = excluded."AdapterId",
                "IssuedAt" = excluded."IssuedAt",
                "ExpiresAt" = excluded."ExpiresAt",
                "UpdatedAt" = excluded."UpdatedAt";
            """, ct);

        return await ReadRecordAsync(db, targetKey, ct);
    }

    public async Task<SlackLeaseRecord?> RenewAsync(
        string targetKey, string leaseId, string adapterId, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SlackAdapterLeases"
            SET "ExpiresAt" = {expiresAt}, "UpdatedAt" = {now}
            WHERE "TargetKey" = {targetKey}
              AND "LeaseId" = {leaseId}
              AND "AdapterId" = {adapterId}
              AND "LeaseKind" IS NOT NULL
              AND "ExpiresAt" > {now};
            """, ct);
        return affected == 0 ? null : await ReadRecordAsync(db, targetKey, ct);
    }

    public async Task<bool> ConfirmHelloAsync(string targetKey, string leaseId, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "SlackAdapterLeases"
            SET "Generation" = "Generation" + 1,
                "LeaseId" = NULL,
                "LeaseKind" = NULL,
                "AdapterId" = NULL,
                "IssuedAt" = NULL,
                "ExpiresAt" = NULL,
                "UpdatedAt" = {now}
            WHERE "TargetKey" = {targetKey}
              AND "LeaseId" = {leaseId}
              AND "LeaseKind" = 'validation'
              AND "ExpiresAt" > {now};
            """, ct);
        return affected > 0;
    }

    public async Task<SlackLeaseRecord?> GetActiveAsync(string targetKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.SlackAdapterLeases.AsNoTracking()
            .Where(row => row.TargetKey == targetKey && row.LeaseId != null)
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return null;
        return new SlackLeaseRecord(
            row.LeaseId!, row.LeaseKind!, row.Generation, row.AdapterId!, row.IssuedAt!.Value, row.ExpiresAt!.Value);
    }

    public async Task<int> GetGenerationAsync(string targetKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SlackAdapterLeases.AsNoTracking()
            .Where(row => row.TargetKey == targetKey)
            .Select(row => row.Generation)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<SlackLeaseRecord> ReadRecordAsync(MohistDbContext db, string targetKey, CancellationToken ct)
    {
        var row = await db.SlackAdapterLeases.AsNoTracking()
            .SingleAsync(row => row.TargetKey == targetKey, ct);
        if (row.LeaseId is null || row.LeaseKind is null || row.AdapterId is null
            || row.IssuedAt is null || row.ExpiresAt is null)
        {
            throw new InvalidOperationException(
                $"Slack adapter lease '{targetKey}' has no active lease to read.");
        }
        return new SlackLeaseRecord(
            row.LeaseId, row.LeaseKind, row.Generation, row.AdapterId, row.IssuedAt.Value, row.ExpiresAt.Value);
    }

    private static string NewLeaseId() => $"slklease_{Guid.NewGuid():N}";

    private static void ValidateKind(string kind)
    {
        if (kind != SlackLeaseKind.Validation && kind != SlackLeaseKind.Runtime)
            throw new ArgumentException($"Unknown lease kind '{kind}'.", nameof(kind));
    }
}
