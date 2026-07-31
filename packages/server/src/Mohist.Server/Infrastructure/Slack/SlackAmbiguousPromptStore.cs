using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Slack;

/// <summary>
/// Workspace-scoped dedup store for the choose-one prompt. Each
/// mentioned Bot receives the Slack event independently (Slack fans
/// out per App), so without this store every mentioned Connection
/// would post its own prompt and break "only one prompt per
/// ambiguous message" (spec
/// <c>channel-attribution/spec.md#The choose-one prompt is sent at
/// most once per ambiguous message</c>). D5: the row is
/// <c>INSERT ... ON CONFLICT DO NOTHING</c>; the race winner records
/// <see cref="SlackAmbiguousPromptResult.Claimed"/> = true and posts
/// the prompt from its own outbox, losers observe the row exists and
/// no-op.
/// </summary>
/// <remarks>
/// The row is short-lived advisory state that does NOT participate in
/// per-Connection cleanup (<c>IAgentConnectionProviderCleanup</c>) —
/// the prompt is connection-agnostic and the race winner may come
/// from any of the mentioned Connections. A future cleanup pass can
/// reap rows older than the Slack redelivery window.
/// </remarks>
public sealed class SlackAmbiguousPromptStore : IScopedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public SlackAmbiguousPromptStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// First-writer-wins claim. Returns
    /// <see cref="SlackAmbiguousPromptResult.Claimed"/> = true when this
    /// caller inserted the row and must post the prompt; the race
    /// loser observes the row already exists and the prompt should
    /// not be posted by this caller. Losers can still read
    /// <see cref="SlackAmbiguousPromptResult.Existing"/> to confirm
    /// who won the race; only the winner needs the outbox post.
    /// </summary>
    public async Task<SlackAmbiguousPromptResult> TryClaimAsync(
        string projectId,
        string workspaceTeamId,
        string conversationId,
        string messageTs,
        string? threadTs,
        string winningConnectionId,
        IReadOnlyList<string> mentionedConnectionIds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("ProjectId is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(workspaceTeamId))
            throw new ArgumentException("WorkspaceTeamId is required.", nameof(workspaceTeamId));
        if (string.IsNullOrWhiteSpace(conversationId))
            throw new ArgumentException("ConversationId is required.", nameof(conversationId));
        if (string.IsNullOrWhiteSpace(messageTs))
            throw new ArgumentException("MessageTs is required.", nameof(messageTs));
        if (string.IsNullOrWhiteSpace(winningConnectionId))
            throw new ArgumentException("WinningConnectionId is required.", nameof(winningConnectionId));
        if (mentionedConnectionIds is null)
            throw new ArgumentNullException(nameof(mentionedConnectionIds));

        var now = _timeProvider.GetUtcNow();
        var serialized = JsonSerializer.Serialize(mentionedConnectionIds, JsonOptions);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "SlackAmbiguousPrompts" (
                "Id", "ProjectId", "WorkspaceTeamId", "ConversationId", "MessageTs",
                "ThreadTs", "WinningConnectionId", "MentionedConnectionIdsJson",
                "PromptedAt", "CreatedAt", "UpdatedAt")
            VALUES (
                {$"slkamb_{Guid.NewGuid():N}"}, {projectId}, {workspaceTeamId},
                {conversationId}, {messageTs},
                {threadTs}, {winningConnectionId}, {serialized},
                {now}, {now}, {now})
            ON CONFLICT("WorkspaceTeamId", "ConversationId", "MessageTs") DO NOTHING;
            """, ct);

        var existing = await db.SlackAmbiguousPrompts.AsNoTracking()
            .Where(row => row.WorkspaceTeamId == workspaceTeamId
                && row.ConversationId == conversationId
                && row.MessageTs == messageTs)
            .Select(row => new
            {
                row.Id,
                row.WinningConnectionId,
                row.ThreadTs,
                row.MentionedConnectionIdsJson,
            })
            .SingleAsync(ct);

        var claimed = string.Equals(existing.WinningConnectionId, winningConnectionId, StringComparison.Ordinal)
            && (threadTs is null
                || string.Equals(existing.ThreadTs ?? string.Empty, threadTs, StringComparison.Ordinal));
        var mentioned = DeserializeMentioned(existing.MentionedConnectionIdsJson);
        return new SlackAmbiguousPromptResult(claimed, existing.Id, existing.WinningConnectionId, existing.ThreadTs, mentioned);
    }

    private static IReadOnlyList<string> DeserializeMentioned(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>
/// Outcome of <see cref="SlackAmbiguousPromptStore.TryClaimAsync"/>.
/// When <see cref="Claimed"/> is true the caller is the race winner
/// and must post the prompt via its own outbox; when false another
/// caller already claimed it (likely a concurrent per-Connection
/// ingress for the same ambiguous message, or a Slack redelivery)
/// and the prompt is the loser's no-op.
/// </summary>
public sealed record SlackAmbiguousPromptResult(
    bool Claimed,
    string RowId,
    string WinningConnectionId,
    string? ThreadTs,
    IReadOnlyList<string> MentionedConnectionIds);