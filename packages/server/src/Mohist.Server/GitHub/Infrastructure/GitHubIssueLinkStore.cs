using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.GitHub;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.GitHub.Infrastructure;

public sealed class GitHubIssueLinkStore : IScopedService
{
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly TimeProvider _timeProvider;

    public GitHubIssueLinkStore(
        IDbContextFactory<MohistDbContext> dbFactory,
        TimeProvider timeProvider)
    {
        _dbFactory = dbFactory;
        _timeProvider = timeProvider;
    }

    public async Task<GitHubIssueLink?> GetAsync(
        string projectId,
        string repositoryName,
        int githubIssueNumber,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);
        if (githubIssueNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(githubIssueNumber));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueLinks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId
                && r.RepositoryName == repositoryName
                && r.GithubIssueNumber == githubIssueNumber, ct);
        return row is null ? null : ToDomain(row);
    }

    /// <summary>
    /// Finds the link for a Mohist issue. The write-back writer projects
    /// progress from Mohist events and needs the reverse lookup; the unique
    /// issue index guarantees at most one link per Mohist issue.
    /// </summary>
    public async Task<GitHubIssueLink?> GetByIssueAsync(
        string projectId,
        int issueNumber,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (issueNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(issueNumber));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueLinks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ProjectId == projectId && r.IssueNumber == issueNumber, ct);
        return row is null ? null : ToDomain(row);
    }

    /// <summary>
    /// First-writer-wins insert: the unique index on
    /// <c>(ProjectId, RepositoryName, GithubIssueNumber)</c> makes a
    /// concurrent or redelivered mirror/command deterministically lose to
    /// the existing link, which is then returned. Returns the winning link
    /// in both cases.
    /// </summary>
    public async Task<GitHubIssueLink> CreateAsync(
        string projectId,
        string repositoryName,
        int githubIssueNumber,
        int issueNumber,
        bool commandRequested = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);
        if (githubIssueNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(githubIssueNumber));
        if (issueNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(issueNumber));

        var now = _timeProvider.GetUtcNow();
        var row = new GitHubIssueLinkRow
        {
            Id = $"ghlink_{Guid.NewGuid():N}",
            ProjectId = projectId,
            RepositoryName = repositoryName,
            GithubIssueNumber = githubIssueNumber,
            IssueNumber = issueNumber,
            MirrorMarker = null,
            MirrorCreateAttempted = false,
            CommandRequested = commandRequested,
            PostedCommentsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.GitHubIssueLinks.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            var existing = await db.GitHubIssueLinks.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ProjectId == projectId
                    && r.RepositoryName == repositoryName
                    && r.GithubIssueNumber == githubIssueNumber, ct)
                ?? await db.GitHubIssueLinks.AsNoTracking()
                    .FirstAsync(r => r.ProjectId == projectId && r.IssueNumber == issueNumber, ct);
            return ToDomain(existing);
        }
        return ToDomain(row);
    }

    public async Task<GitHubIssueLink> CreatePendingAsync(
        string projectId,
        string repositoryName,
        int issueNumber,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);
        if (issueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(issueNumber));
        var now = _timeProvider.GetUtcNow();
        var id = $"ghlink_{Guid.NewGuid():N}";
        var row = new GitHubIssueLinkRow
        {
            Id = id,
            ProjectId = projectId,
            RepositoryName = repositoryName,
            GithubIssueNumber = 0,
            IssueNumber = issueNumber,
            MirrorMarker = GitHubMirrorMarker.For(id),
            MirrorCreateAttempted = false,
            CommandRequested = false,
            PostedCommentsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.GitHubIssueLinks.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsIssueUniqueViolation(ex))
        {
            var existing = await db.GitHubIssueLinks.AsNoTracking()
                .FirstAsync(r => r.ProjectId == projectId && r.IssueNumber == issueNumber, ct);
            return ToDomain(existing);
        }
        return ToDomain(row);
    }

    public async Task<GitHubIssueLink?> SetMirrorAsync(
        string id,
        int githubIssueNumber,
        CancellationToken ct = default)
    {
        if (githubIssueNumber <= 0) throw new ArgumentOutOfRangeException(nameof(githubIssueNumber));
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueLinks.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return null;
        row.GithubIssueNumber = githubIssueNumber;
        row.MirrorCreateAttempted = true;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    public async Task<GitHubIssueLink?> MarkMirrorCreateAttemptedAsync(
        string id,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueLinks.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return null;
        if (!row.MirrorCreateAttempted)
        {
            row.MirrorCreateAttempted = true;
            row.UpdatedAt = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return ToDomain(row);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueLinks.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is not null)
        {
            db.GitHubIssueLinks.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task MarkCommentPostedAsync(string id, string commentKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(commentKey);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueLinks.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
            return;
        var posted = DeserializePosted(row.PostedCommentsJson);
        if (posted.Contains(commentKey))
            return;
        posted.Add(commentKey);
        row.PostedCommentsJson = SerializePosted(posted);
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Persists the state label projected onto the GitHub issue. No-op
    /// when the label is already recorded, so redelivery never re-runs the
    /// label write-back.
    /// </summary>
    public async Task SetStateLabelAsync(string id, string stateLabel, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateLabel);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueLinks.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null || string.Equals(row.StateLabel, stateLabel, StringComparison.Ordinal))
            return;
        row.StateLabel = stateLabel;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static GitHubIssueLink ToDomain(GitHubIssueLinkRow row) => new()
    {
        Id = row.Id,
        ProjectId = row.ProjectId,
        RepositoryName = row.RepositoryName,
        GithubIssueNumber = row.GithubIssueNumber,
        IssueNumber = row.IssueNumber,
        MirrorMarker = row.MirrorMarker,
        MirrorCreateAttempted = row.MirrorCreateAttempted,
        CommandRequested = row.CommandRequested,
        PostedComments = DeserializePosted(row.PostedCommentsJson),
        StateLabel = row.StateLabel,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

    private static HashSet<string> DeserializePosted(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var values = JsonSerializer.Deserialize<List<string>>(json, JSON.Options);
            return values is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(values, StringComparer.Ordinal);
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static string SerializePosted(IReadOnlySet<string> posted) =>
        JsonSerializer.Serialize(posted.OrderBy(k => k, StringComparer.Ordinal), JSON.Options);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite
        && sqlite.SqliteErrorCode == 19
        && sqlite.Message.Contains("GitHubIssueLinks", StringComparison.OrdinalIgnoreCase);

    private static bool IsIssueUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite
        && sqlite.SqliteErrorCode == 19
        && sqlite.Message.Contains("IssueNumber", StringComparison.OrdinalIgnoreCase);
}
