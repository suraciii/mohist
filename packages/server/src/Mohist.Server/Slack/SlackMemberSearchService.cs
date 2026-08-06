using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Slack;

public sealed record SlackMemberSearchEntry(string SlackUserId, string? DisplayName, string? AvatarUrl);

public sealed class SlackMemberSearchService(IDbContextFactory<MohistDbContext> dbFactory) : IScopedService
{
    public async Task<IReadOnlyList<SlackMemberSearchEntry>> SearchAsync(
        string projectId,
        string connectionId,
        string? query,
        int? limit,
        CancellationToken ct = default)
    {
        var value = query?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return [];
        var maximum = Math.Clamp(limit ?? 50, 1, 50);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SlackConnectionAllowedMembers.AsNoTracking()
            .Where(member => member.ProjectId == projectId && member.ConnectionId == connectionId && member.SlackUserId.Contains(value))
            .OrderBy(member => member.SlackUserId)
            .Take(maximum)
            .Select(member => new SlackMemberSearchEntry(member.SlackUserId, null, null))
            .ToListAsync(ct);
    }
}
