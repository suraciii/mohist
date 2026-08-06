using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Slack;

public static class SlackConnectionAccessContract
{
    public const string AnyoneDisclosure = "Invoking this Bot grants channel members the Agent's configured repository-write, tool, and credential authority.";
}

public sealed class SlackConnectionAccessManager : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _time;

    public SlackConnectionAccessManager(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    public async Task<IReadOnlyList<string>> ListMembersAsync(
        string projectId,
        string connectionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackConnectionAllowedMembers.AsNoTracking()
            .Where(row => row.ProjectId == projectId && row.ConnectionId == connectionId)
            .OrderBy(row => row.SlackUserId)
            .Select(row => row.SlackUserId)
            .ToListAsync(ct);
    }

    public async Task<bool> ReplaceAsync(
        string projectId,
        string connectionId,
        string? accessPolicy,
        IReadOnlyList<string>? allowMembers,
        CancellationToken ct = default)
    {
        var policy = NormalizePolicy(accessPolicy);
        var members = NormalizeMembers(allowMembers);
        ValidatePolicyAndMembers(policy, members);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var connection = await db.AgentConnections.FirstOrDefaultAsync(
            row => row.ProjectId == projectId && row.Id == connectionId && row.DeletedAt == null,
            ct);
        if (connection is null)
            return false;

        var storedMembers = members
            .Where(member => !string.Equals(member, connection.OwnerSlackUserId, StringComparison.Ordinal))
            .ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SlackConnectionAllowedMembers
            .Where(row => row.ProjectId == projectId && row.ConnectionId == connectionId)
            .ExecuteDeleteAsync(ct);

        var now = _time.GetUtcNow();
        connection.AccessPolicy = policy;
        connection.UpdatedAt = now;
        db.SlackConnectionAllowedMembers.AddRange(storedMembers.Select(member => new SlackConnectionAllowedMemberRow
        {
            Id = $"allowed_member_{Guid.NewGuid():N}",
            ProjectId = projectId,
            ConnectionId = connectionId,
            SlackUserId = member,
            WorkspaceTeamId = connection.WorkspaceTeamId,
            CreatedAt = now,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static string NormalizePolicy(string? value)
    {
        var policy = value?.Trim().ToLowerInvariant();
        if (policy is AccessPolicyKind.OwnerOnly or AccessPolicyKind.Allowlist or AccessPolicyKind.Anyone)
            return policy;
        throw new SlackConnectionAccessValidationException(
            "accessPolicy must be owner_only, allowlist, or anyone.",
            "invalid_access_policy");
    }

    private static IReadOnlyList<string> NormalizeMembers(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return Array.Empty<string>();

        var members = new List<string>(values.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new SlackConnectionAccessValidationException(
                    "allowMembers cannot contain an empty Slack user id.",
                    "invalid_allow_member");
            var member = value.Trim();
            if (seen.Add(member))
                members.Add(member);
        }
        return members;
    }

    private static void ValidatePolicyAndMembers(string policy, IReadOnlyList<string> members)
    {
        if (members.Count > 0 && policy is AccessPolicyKind.OwnerOnly or AccessPolicyKind.Anyone)
            throw new SlackConnectionAccessValidationException(
                "allowMembers may be supplied only when accessPolicy is allowlist.",
                "allow_members_not_allowed");
    }

}

public sealed class SlackConnectionAccessValidationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}
