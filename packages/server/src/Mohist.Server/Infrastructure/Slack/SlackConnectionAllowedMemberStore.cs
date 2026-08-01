using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Persistence boundary for the allowlist members a Connection Owner
/// explicitly grants access to under the <c>allowlist</c> access policy.
/// The Owner is never stored in this table — owner authority is implicit
/// and unconditional, so "Owner is always in the allowlist and cannot be
/// removed" is a structural invariant rather than a check at lookup time.
/// A row exists iff an Owner chose <c>allowlist</c> AND explicitly named
/// this Slack user id; the empty set is the default and is meaningful
/// (only the Owner is allowed). Cascades via
/// <see cref="IAgentConnectionProviderCleanup"/> on Connection deletion.
/// </summary>
public sealed class SlackConnectionAllowedMemberStore : IScopedService, IAgentConnectionProviderCleanup
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public SlackConnectionAllowedMemberStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<int> DeleteForConnectionAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackConnectionAllowedMembers
            .Where(row => row.ProjectId == projectId && row.ConnectionId == connectionId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListAsync(
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

    /// <summary>
    /// Adds one member to the allowlist. Idempotent on the unique
    /// <c>(ProjectId, ConnectionId, SlackUserId)</c> index — a duplicate
    /// insert is a no-op rather than an error. The Owner is silently
    /// ignored (see "Owner never stored" invariant). Workspace team id is
    /// denormalized for downstream consistency checks but not used for
    /// authorization; the unique row is the only identity the decider
    /// queries.
    /// </summary>
    public async Task<bool> IsAllowedAsync(
        string projectId,
        string connectionId,
        string slackUserId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(slackUserId))
            throw new ArgumentException("SlackUserId is required.", nameof(slackUserId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackConnectionAllowedMembers.AsNoTracking()
            .AnyAsync(row => row.ProjectId == projectId
                && row.ConnectionId == connectionId
                && row.SlackUserId == slackUserId, ct);
    }
}
