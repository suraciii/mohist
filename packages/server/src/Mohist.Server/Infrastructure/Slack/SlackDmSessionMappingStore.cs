using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Persistence boundary for the Slack DM "current AgentSession" mapping.
/// The DM ingress reads the mapping to decide between follow-up
/// (continue the existing session) and launch (first DM or explicit New
/// task) — without this read every DM would mint a new AgentJob, the
/// behavior the product spec rules out. Lives in Server infrastructure
/// alongside the inbox / outbox stores; conversation mapping belongs to
/// infrastructure, not to the AgentConnection or AgentSession domain.
/// </summary>
/// <remarks>
/// <para>
/// Scope is <see cref="IScopedService"/> so it shares the request
/// scope with the inbox store, dispatcher, and resolver it collaborates
/// with. Cascades via <see cref="IAgentConnectionProviderCleanup"/> on
/// Connection deletion — the mapping is keyed by Connection and never
/// referenced after the Connection is gone.
/// </para>
/// <para>
/// The mapping is read-once-per-ingress, write-once-per-launch; no
/// grain-style concurrency machinery is needed. The read in
/// <see cref="GetCurrentSessionIdAsync"/> is non-blocking (no
/// exclusive transaction); the upsert in
/// <see cref="SetCurrentSessionIdAsync"/> takes a short transaction so a
/// redelivered launch that races itself cannot insert two rows.
/// </para>
/// </remarks>
public sealed class SlackDmSessionMappingStore : IScopedService, IAgentConnectionProviderCleanup
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public SlackDmSessionMappingStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the current AgentSession id for one DM conversation, or
    /// <c>null</c> when the conversation has no current session yet
    /// (first DM after Connection setup, after a Connection delete, or
    /// before the first launch upsert has committed). Read-only; safe
    /// under concurrent follow-up dispatches.
    /// </summary>
    public async Task<string?> GetCurrentSessionIdAsync(
        string projectId,
        string connectionId,
        string dmConversationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(dmConversationId))
            throw new ArgumentException("DmConversationId is required.", nameof(dmConversationId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackDmSessionMappings.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.ConnectionId == connectionId
                && row.DmConversationId == dmConversationId)
            .Select(row => row.CurrentSessionId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Records <paramref name="sessionId"/> as the current AgentSession
    /// for the DM conversation. Called by the ingress after a successful
    /// launch. Upserts on the unique
    /// <c>(ConnectionId, DmConversationId)</c> index so an idempotent
    /// replay (e.g. an inbox-dedup redelivery that re-runs the launch
    /// path before the first upsert committed) collapses to the same
    /// row.
    /// </summary>
    public async Task SetCurrentSessionIdAsync(
        string projectId,
        string connectionId,
        string workspaceTeamId,
        string slackUserId,
        string dmConversationId,
        string sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(dmConversationId))
            throw new ArgumentException("DmConversationId is required.", nameof(dmConversationId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId is required.", nameof(sessionId));

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.SlackDmSessionMappings
            .FirstOrDefaultAsync(row => row.ProjectId == projectId
                && row.ConnectionId == connectionId
                && row.DmConversationId == dmConversationId, ct);
        if (existing is null)
        {
            db.SlackDmSessionMappings.Add(new SlackDmSessionMappingRow
            {
                Id = $"slkdmmp_{Guid.NewGuid():N}",
                ProjectId = projectId,
                ConnectionId = connectionId,
                WorkspaceTeamId = workspaceTeamId,
                SlackUserId = slackUserId,
                DmConversationId = dmConversationId,
                CurrentSessionId = sessionId,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.CurrentSessionId = sessionId;
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Cascades provider rows for one Connection. Called from
    /// <c>AgentConnectionStore.DeleteAsync</c>; idempotent and tolerant
    /// of missing Connections (returns 0). The mapping itself is a
    /// derived convenience index, not a domain fact — removing it does
    /// not touch AgentJob or AgentSession rows, and a future DM
    /// conversation would simply establish a new current session.
    /// </summary>
    public async Task<int> DeleteForConnectionAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackDmSessionMappings
            .Where(row => row.ProjectId == projectId && row.ConnectionId == connectionId)
            .ExecuteDeleteAsync(ct);
    }
}