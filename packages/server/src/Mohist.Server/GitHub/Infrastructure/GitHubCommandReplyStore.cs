using Microsoft.EntityFrameworkCore;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.GitHub;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.GitHub.Infrastructure;

public sealed class GitHubCommandReplyStore : IScopedService
{
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public GitHubCommandReplyStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<GitHubCommandReply> GetOrCreateAsync(
        string projectId,
        string connectionId,
        string repositoryName,
        int githubIssueNumber,
        string githubCommentId,
        string operationKey,
        string marker,
        string body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(githubIssueNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(githubCommentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        ArgumentNullException.ThrowIfNull(body);

        var now = _timeProvider.GetUtcNow();
        var row = new GitHubCommandReplyRow
        {
            Id = $"ghreply_{Guid.NewGuid():N}",
            ProjectId = projectId,
            ConnectionId = connectionId,
            RepositoryName = repositoryName,
            GithubIssueNumber = githubIssueNumber,
            GithubCommentId = githubCommentId,
            OperationKey = operationKey,
            Marker = marker,
            Body = body,
            PostedAt = null,
            AttemptCount = 0,
            NextAttemptAt = null,
            LeaseUntil = null,
            LastError = null,
            FailedAt = null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.GitHubCommandReplies.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            row = await db.GitHubCommandReplies.AsNoTracking()
                .SingleAsync(existing => existing.ConnectionId == connectionId
                    && existing.GithubIssueNumber == githubIssueNumber
                    && existing.GithubCommentId == githubCommentId
                    && existing.OperationKey == operationKey, ct);
        }
        return ToDomain(row);
    }

    public async Task<GitHubCommandReply?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubCommandReplies.AsNoTracking()
            .FirstOrDefaultAsync(reply => reply.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<GitHubCommandReply>> ListPendingAsync(
        int limit = 100,
        CancellationToken ct = default)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.GitHubCommandReplies.AsNoTracking()
            .Where(reply => reply.PostedAt == null && reply.FailedAt == null)
            .ToListAsync(ct);
        return rows
            .Where(reply => (reply.NextAttemptAt is null || reply.NextAttemptAt <= now)
                && (reply.LeaseUntil is null || reply.LeaseUntil <= now))
            .OrderBy(reply => reply.CreatedAt)
            .Take(limit)
            .Select(ToDomain)
            .ToList();
    }

    /// <summary>
    /// Reserves one pending operation for a delivery attempt. The conditional
    /// update is the cross-process idempotency fence between an ingress
    /// handler and the hosted retry worker.
    /// </summary>
    public async Task<GitHubCommandReply?> TryClaimAsync(
        string id,
        TimeSpan leaseDuration,
        bool force = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        var now = _timeProvider.GetUtcNow();
        var leaseUntil = now.Add(leaseDuration);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var affected = force
            ? await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "GitHubCommandReplies"
                SET "LeaseUntil" = {leaseUntil}, "UpdatedAt" = {now}
                WHERE "Id" = {id}
                  AND "PostedAt" IS NULL
                  AND "FailedAt" IS NULL
                  AND ("LeaseUntil" IS NULL OR "LeaseUntil" <= {now})
                """, ct)
            : await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "GitHubCommandReplies"
                SET "LeaseUntil" = {leaseUntil}, "UpdatedAt" = {now}
                WHERE "Id" = {id}
                  AND "PostedAt" IS NULL
                  AND "FailedAt" IS NULL
                  AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {now})
                  AND ("LeaseUntil" IS NULL OR "LeaseUntil" <= {now})
                """, ct);
        if (affected == 0)
            return null;

        var row = await db.GitHubCommandReplies.AsNoTracking()
            .SingleAsync(reply => reply.Id == id, ct);
        return ToDomain(row);
    }

    public async Task<GitHubCommandReply?> MarkPostedAsync(
        string id,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubCommandReplies.FirstOrDefaultAsync(reply => reply.Id == id, ct);
        if (row is null)
            return null;
        if (row.PostedAt is null && row.FailedAt is null)
        {
            row.PostedAt = _timeProvider.GetUtcNow();
            row.LeaseUntil = null;
            row.NextAttemptAt = null;
            row.LastError = null;
            row.UpdatedAt = row.PostedAt.Value;
            await db.SaveChangesAsync(ct);
        }
        return ToDomain(row);
    }

    public async Task<GitHubCommandReply?> RecordFailureAsync(
        string id,
        string error,
        bool terminal = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubCommandReplies.FirstOrDefaultAsync(reply => reply.Id == id, ct);
        if (row is null)
            return null;
        if (row.PostedAt is null && row.FailedAt is null)
        {
            var now = _timeProvider.GetUtcNow();
            row.AttemptCount++;
            row.LastError = error;
            row.LeaseUntil = null;
            row.FailedAt = terminal ? now : null;
            row.NextAttemptAt = terminal ? null : now.Add(Backoff(row.AttemptCount));
            row.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }
        return ToDomain(row);
    }

    private static GitHubCommandReply ToDomain(GitHubCommandReplyRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        ConnectionId = row.ConnectionId,
        RepositoryName = row.RepositoryName,
        GithubIssueNumber = row.GithubIssueNumber,
        GithubCommentId = row.GithubCommentId,
        OperationKey = row.OperationKey,
        Marker = row.Marker,
        Body = row.Body,
        PostedAt = row.PostedAt,
        AttemptCount = row.AttemptCount,
        NextAttemptAt = row.NextAttemptAt,
        LeaseUntil = row.LeaseUntil,
        LastError = row.LastError,
        FailedAt = row.FailedAt,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite
        && sqlite.SqliteErrorCode == 19
        && sqlite.Message.Contains("GitHubCommandReplies", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan Backoff(int attemptCount)
    {
        var multiplier = Math.Pow(2, Math.Min(Math.Max(attemptCount - 1, 0), 10));
        var ticks = Math.Min(RetryBaseDelay.Ticks * multiplier, RetryMaxDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }
}
