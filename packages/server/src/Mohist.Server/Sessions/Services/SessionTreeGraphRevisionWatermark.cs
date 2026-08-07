using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Sessions;

namespace Mohist.Server.Sessions.Services;

public static class SessionTreeGraphRevisionWatermark
{
    public static async Task<long> ReadPublishedRevisionAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        string projectId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var revision = await db.SessionTreeGraphRevisions.AsNoTracking()
            .Where(row => row.ProjectId == projectId)
            .Select(row => (long?)row.PublishedRevision)
            .FirstOrDefaultAsync(ct);
        return revision ?? 0;
    }

    public static async Task PublishAsync(
        IDbContextFactory<MohistDbContext> dbFactory,
        string projectId,
        long revision,
        DateTimeOffset publishedAt,
        CancellationToken ct = default)
    {
        var stamp = publishedAt.ToString("O", CultureInfo.InvariantCulture);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO ""SessionTreeGraphRevisions"" (""ProjectId"", ""PublishedRevision"", ""PublishedAt"")
VALUES ({projectId}, {revision}, {stamp})
ON CONFLICT(""ProjectId"") DO UPDATE SET
  ""PublishedRevision"" = MAX(""PublishedRevision"", excluded.""PublishedRevision""),
  ""PublishedAt"" = CASE WHEN excluded.""PublishedRevision"" > ""PublishedRevision""
    THEN excluded.""PublishedAt"" ELSE ""PublishedAt"" END", ct);
    }
}
