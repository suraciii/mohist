using Microsoft.EntityFrameworkCore;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Persistence boundary for the Slack channel "thread bound to
/// AgentSession" mapping. Each row pins one
/// <c>(Connection, Workspace, Conversation, Thread)</c> tuple to one
/// <see cref="SessionId"/> so a reply in a previously-root-mentioned
/// thread can be routed to that Agent's session without re-mention.
/// Lives next to <see cref="SlackDmSessionMappingStore"/>; the two
/// stores are deliberately separate because their keys and semantics
/// diverge (thread has no New-task swap, thread may bind several
/// Agents across Connections).
/// </summary>
/// <remarks>
/// <para>
/// Scope is <see cref="IScopedService"/> so it shares the request
/// scope with the inbox store, launcher, and resolver it collaborates
/// with. Cascades via <see cref="IAgentConnectionProviderCleanup"/> on
/// Connection deletion — the mapping is keyed by Connection and never
/// referenced after the Connection is gone.
/// </para>
/// <para>
/// Reads are non-blocking (no exclusive transaction); the upsert in
/// <see cref="UpsertAsync"/> uses <c>INSERT ... ON CONFLICT DO NOTHING</c>
/// on the unique key so a redelivered launch that races itself cannot
/// insert two rows or swap a previously-bound session.
/// </para>
/// </remarks>
public sealed class SlackThreadSessionMappingStore : IScopedService, IAgentConnectionProviderCleanup
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public SlackThreadSessionMappingStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the bound session id for one Connection × workspace ×
    /// channel × thread, or <c>null</c> when no row exists for this
    /// Connection. Used by the channel ingress to look up
    /// "is this thread bound to me?" without scanning other
    /// Connections' mappings in the same workspace.
    /// </summary>
    public async Task<string?> GetSessionIdAsync(
        string projectId,
        string workspaceTeamId,
        string connectionId,
        string conversationId,
        string threadTs,
        CancellationToken ct = default)
    {
        ValidateArgs(projectId, connectionId, conversationId, threadTs, nameof(threadTs));
        if (string.IsNullOrWhiteSpace(workspaceTeamId))
            throw new ArgumentException("WorkspaceTeamId is required.", nameof(workspaceTeamId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackThreadSessionMappings.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.WorkspaceTeamId == workspaceTeamId
                && row.ConnectionId == connectionId
                && row.ConversationId == conversationId
                && row.ThreadTs == threadTs)
            .Select(row => row.SessionId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Lists every binding for one (workspace, conversation, thread)
    /// tuple across Connections. The cardinality of the result
    /// distinguishes the "exactly one Agent bound" branch from the
    /// "multi-Agent thread, do not guess" branch. Always scoped to the
    /// inbound workspace so two workspaces that share channel/thread
    /// identifiers cannot share a binding list.
    /// </summary>
    public async Task<IReadOnlyList<SlackThreadBinding>> ListBindingsAsync(
        string projectId,
        string workspaceTeamId,
        string conversationId,
        string threadTs,
        CancellationToken ct = default)
    {
        ValidateArgs(projectId, "connectionId-not-required", conversationId, threadTs, nameof(conversationId));
        if (string.IsNullOrWhiteSpace(workspaceTeamId))
            throw new ArgumentException("WorkspaceTeamId is required.", nameof(workspaceTeamId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackThreadSessionMappings.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.WorkspaceTeamId == workspaceTeamId
                && row.ConversationId == conversationId
                && row.ThreadTs == threadTs)
            .OrderBy(row => row.ConnectionId)
            .Select(row => new SlackThreadBinding(
                row.ConnectionId,
                row.SessionId,
                row.RootMessageTs))
            .ToListAsync(ct);
    }

/// <summary>
/// Inserts a thread binding if and only if no row exists for the
/// same <c>(Connection, Workspace, Conversation, Thread)</c>. An
/// idempotent upsert that does NOT swap a previously-bound session:
/// thread semantics are append-once. A redelivered launch that
/// races the first write collapses to a single row; if the caller
/// already persisted a different session id under the same key, the
/// stored session id remains and this method reports it back so the
/// caller can re-stamp its inbox route.
/// </summary>
public async Task<SlackThreadBindingResult> UpsertAsync(
        string projectId,
        string workspaceTeamId,
        string connectionId,
        string conversationId,
        string threadTs,
        string slackUserId,
        string sessionId,
        string rootMessageTs,
        CancellationToken ct = default)
    {
        ValidateArgs(projectId, connectionId, conversationId, threadTs, nameof(threadTs));
        if (string.IsNullOrWhiteSpace(workspaceTeamId))
            throw new ArgumentException("WorkspaceTeamId is required.", nameof(workspaceTeamId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("SessionId is required.", nameof(sessionId));

        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "SlackThreadSessionMappings" (
                "Id", "ProjectId", "ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs",
                "SlackUserId", "SessionId", "RootMessageTs", "CreatedAt", "UpdatedAt")
            VALUES (
                {$"slkthrdsmp_{Guid.NewGuid():N}"}, {projectId}, {connectionId}, {workspaceTeamId},
                {conversationId}, {threadTs},
                {slackUserId}, {sessionId}, {rootMessageTs}, {now}, {now})
            ON CONFLICT("ConnectionId", "WorkspaceTeamId", "ConversationId", "ThreadTs") DO NOTHING;
            """, ct);

        var stored = await db.SlackThreadSessionMappings.AsNoTracking()
            .Where(row => row.ProjectId == projectId
                && row.WorkspaceTeamId == workspaceTeamId
                && row.ConnectionId == connectionId
                && row.ConversationId == conversationId
                && row.ThreadTs == threadTs)
            .Select(row => new { row.SessionId })
            .SingleAsync(ct);
        return new SlackThreadBindingResult(stored.SessionId, AlreadyExisted: stored.SessionId != sessionId);
    }

    /// <summary>
    /// Cascades provider rows for one Connection. Called from
    /// <c>AgentConnectionStore.DeleteAsync</c>; idempotent and tolerant
    /// of missing Connections (returns 0). The mapping itself is a
    /// derived convenience index, not a domain fact — removing it does
    /// not touch AgentJob or AgentSession rows.
    /// </summary>
    public async Task<int> DeleteForConnectionAsync(string projectId, string connectionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.SlackThreadSessionMappings
            .Where(row => row.ProjectId == projectId && row.ConnectionId == connectionId)
            .ExecuteDeleteAsync(ct);
    }

    private static void ValidateArgs(string projectId, string connectionId, string conversationId, string threadTs, string threadTsName)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("ConnectionId is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(conversationId))
            throw new ArgumentException("ConversationId is required.", nameof(conversationId));
        if (string.IsNullOrWhiteSpace(threadTs))
            throw new ArgumentException("ThreadTs is required.", threadTsName);
    }
}

/// <summary>
/// Compact per-binding projection of a thread mapping row: the
/// Connection that owns the binding, the bound session id, and the
/// root message ts that anchors the thread. The <see cref="ConnectionId"/>
/// is the key piece the channel state machine uses to resolve
/// "exactly one binding is mine" / "exactly one binding belongs to
/// another Connection" / "more than one binding, ambiguous".
/// </summary>
public sealed record SlackThreadBinding(
    string ConnectionId,
    string SessionId,
    string RootMessageTs);

/// <summary>
/// Outcome of <see cref="SlackThreadSessionMappingStore.UpsertAsync"/>.
/// When <see cref="AlreadyExisted"/> is true the row was already
/// present with a different session id (a previous launch already
/// persisted the session); the caller MUST reconcile the inbox route
/// to <see cref="SessionId"/> before responding so that a redelivery
/// does not bounce between the two ids.
/// </summary>
public sealed record SlackThreadBindingResult(string SessionId, bool AlreadyExisted);