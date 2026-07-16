using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;

namespace Mohist.Server.Infrastructure.Data.Epic;

internal static class EpicIssueAffiliationResolver
{
    public static async Task<int?> ResolveAsync(
        MohistDbContext db,
        string projectId,
        int issueNumber,
        int? excludedEpicNumber = null,
        CancellationToken ct = default)
    {
        var activeEpicNumber = await db.EpicActiveIssues.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.IssueNumber == issueNumber
                && (excludedEpicNumber == null || row.EpicNumber != excludedEpicNumber))
            .OrderBy(row => row.EpicNumber)
            .Select(row => (int?)row.EpicNumber)
            .FirstOrDefaultAsync(ct);
        if (activeEpicNumber.HasValue)
            return activeEpicNumber;

        var retainedLinks = await db.EpicIssues.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.IssueNumber == issueNumber
                && (excludedEpicNumber == null || row.EpicNumber != excludedEpicNumber))
            .Select(row => new { row.EpicNumber, row.CreatedAt })
            .ToListAsync(ct);
        return retainedLinks
            .OrderByDescending(row => row.CreatedAt)
            .ThenBy(row => row.EpicNumber)
            .Select(row => (int?)row.EpicNumber)
            .FirstOrDefault();
    }
}
