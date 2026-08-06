using Microsoft.EntityFrameworkCore;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.GitHub;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.GitHub.Infrastructure;

public sealed class GitHubWriteBackFailureStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public GitHubWriteBackFailureStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task CreateAsync(GitHubWriteBackFailure failure, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.GitHubWriteBackFailures.Add(new GitHubWriteBackFailureRow
        {
            Id = $"ghwbf_{Guid.NewGuid():N}",
            ProjectId = failure.ProjectId,
            ConnectionId = failure.ConnectionId,
            RepositoryName = failure.RepositoryName,
            GithubIssueNumber = failure.GithubIssueNumber,
            IssueNumber = failure.IssueNumber,
            EventType = failure.EventType,
            Operation = failure.Operation,
            ErrorCode = failure.ErrorCode,
            ErrorDetail = failure.ErrorDetail,
            CreatedAt = _timeProvider.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<GitHubWriteBackFailure>> ListRecentAsync(
        string projectId,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.GitHubWriteBackFailures.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .ToListAsync(ct);
        return rows
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .Select(row => new GitHubWriteBackFailure
        {
            Id = row.Id,
            ProjectId = row.ProjectId,
            ConnectionId = row.ConnectionId,
            RepositoryName = row.RepositoryName,
            GithubIssueNumber = row.GithubIssueNumber,
            IssueNumber = row.IssueNumber,
            EventType = row.EventType,
            Operation = row.Operation,
            ErrorCode = row.ErrorCode,
            ErrorDetail = row.ErrorDetail,
            CreatedAt = row.CreatedAt,
        }).ToList();
    }
}
