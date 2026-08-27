using Microsoft.EntityFrameworkCore;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.GitHub;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.GitHub.Infrastructure;

public sealed class GitHubCommandReplyStore : IScopedService
{
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
        string marker,
        string body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(githubIssueNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(githubCommentId);
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
            Marker = marker,
            Body = body,
            PostedAt = null,
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
                    && existing.GithubCommentId == githubCommentId, ct);
        }
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
        if (row.PostedAt is null)
        {
            row.PostedAt = _timeProvider.GetUtcNow();
            row.UpdatedAt = row.PostedAt.Value;
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
        Marker = row.Marker,
        Body = row.Body,
        PostedAt = row.PostedAt,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite
        && sqlite.SqliteErrorCode == 19
        && sqlite.Message.Contains("GitHubCommandReplies", StringComparison.OrdinalIgnoreCase);
}
