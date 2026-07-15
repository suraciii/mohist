using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Epic;

public static class EpicIssueAffiliationResolver
{
    public static async Task<string?> ResolveAsync(
        MohistDbContext db,
        string projectId,
        string issueId,
        CancellationToken ct = default)
    {
        var activeEpicId = await db.EpicActiveIssues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.IssueId == issueId)
            .OrderBy(row => row.EpicId)
            .Select(row => row.EpicId)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(activeEpicId))
            return activeEpicId;

        var retainedLinks = await db.EpicIssues.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.IssueId == issueId)
            .Select(row => new { row.EpicId, row.CreatedAt })
            .ToListAsync(ct);
        return retainedLinks
            .OrderByDescending(row => row.CreatedAt)
            .ThenBy(row => row.EpicId, StringComparer.Ordinal)
            .Select(row => row.EpicId)
            .FirstOrDefault();
    }
}
