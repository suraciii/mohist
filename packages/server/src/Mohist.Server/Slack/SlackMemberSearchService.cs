using System.Text;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Agent;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Security.Secrets;

namespace Mohist.Server.Slack;

public sealed record SlackMemberSearchEntry(
    string SlackUserId,
    string? DisplayName,
    string? AvatarUrl);

public sealed class SlackMemberSearchService : IScopedService
{
    private const int MaxPageSize = 50;

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly ISecretStore _secrets;
    private readonly ISlackApiClient _slack;

    public SlackMemberSearchService(
        IDbContextFactory<MohistDbContext> dbFactory,
        ISecretStore secrets,
        ISlackApiClient slack)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _slack = slack;
    }

    public async Task<IReadOnlyList<SlackMemberSearchEntry>> SearchAsync(
        string projectId,
        string connectionId,
        string? query,
        int? limit,
        CancellationToken ct = default)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
            return Array.Empty<SlackMemberSearchEntry>();

        var max = Math.Clamp(limit ?? MaxPageSize, 1, MaxPageSize);
        var matches = new List<SlackMemberSearchEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var connection = await db.AgentConnections.AsNoTracking().FirstOrDefaultAsync(
            row => row.ProjectId == projectId && row.Id == connectionId && row.DeletedAt == null, ct);
        if (connection is null)
            return Array.Empty<SlackMemberSearchEntry>();
        if (string.IsNullOrWhiteSpace(connection.WorkspaceTeamId))
            return Array.Empty<SlackMemberSearchEntry>();

        byte[]? tokenBytes;
        try
        {
            tokenBytes = await _secrets.LoadAsync(
                new SecretStoreAddress(projectId, connectionId, SecretKind.BotToken), ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Array.Empty<SlackMemberSearchEntry>();
        }
        if (tokenBytes is null || tokenBytes.Length == 0)
            return Array.Empty<SlackMemberSearchEntry>();
        var botToken = Encoding.UTF8.GetString(tokenBytes);

        var cursor = (string?)null;
        var safetyPages = 0;
        do
        {
            if (ct.IsCancellationRequested) break;
            SlackUsersListResponse page;
            try
            {
                page = await _slack.UsersListAsync(cursor, botToken, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                break;
            }
            if (!page.Ok || page.Members is null)
                break;

            foreach (var member in page.Members)
            {
                if (member is null || string.IsNullOrWhiteSpace(member.Id))
                    continue;
                if (!MatchesQuery(member, trimmed))
                    continue;
                if (!SlackOwnerClaimService.IsEligibleMember(new SlackUserInfoResponse(true, null, member), connection.WorkspaceTeamId, member.Id))
                    continue;
                if (!seen.Add(member.Id))
                    continue;
                matches.Add(new SlackMemberSearchEntry(member.Id, member.DisplayName, member.AvatarUrl));
                if (matches.Count >= max)
                    return matches;
            }

            cursor = string.IsNullOrWhiteSpace(page.ResponseMetadata?.NextCursor)
                ? null
                : page.ResponseMetadata!.NextCursor;
            safetyPages++;
        } while (!string.IsNullOrEmpty(cursor) && safetyPages < 4);

        return matches;
    }

    private static bool MatchesQuery(SlackUserInfo member, string query)
    {
        if (string.Equals(member.Id, query, StringComparison.Ordinal))
            return true;
        if (member.DisplayName is not null && member.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        if (member.RealName is not null && member.RealName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        if (member.Email is not null && member.Email.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}
