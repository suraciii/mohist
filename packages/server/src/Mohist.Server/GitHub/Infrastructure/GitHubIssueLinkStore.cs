using System.Data;
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

    /// <summary>
    /// Reserves a comment delivery before calling GitHub. The reservation is
    /// durable and serialized with the link row, so a duplicate event cannot
    /// issue a second POST while the first delivery is in flight or has an
    /// unknown outcome. A reservation remains in progress until the caller
    /// records delivery; fail-closed recovery belongs to reconciliation.
    /// </summary>
    public async Task<GitHubCommentDeliveryReservation> ReserveCommentAsync(
        string id,
        string commentKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(commentKey);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var row = await db.GitHubIssueLinks.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
            throw new InvalidOperationException($"GitHub issue link '{id}' does not exist.");

        var posted = DeserializePosted(row.PostedCommentsJson);
        if (posted.Contains(commentKey))
        {
            await transaction.CommitAsync(ct);
            return new GitHubCommentDeliveryReservation(
                GitHubCommentDeliveryDisposition.Delivered,
                commentKey);
        }

        var reservationKey = ReservedCommentKey(commentKey);
        if (posted.Contains(reservationKey))
        {
            await transaction.CommitAsync(ct);
            return new GitHubCommentDeliveryReservation(
                GitHubCommentDeliveryDisposition.InProgress,
                commentKey);
        }

        posted.Add(reservationKey);
        row.PostedCommentsJson = SerializePosted(posted);
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new GitHubCommentDeliveryReservation(
            GitHubCommentDeliveryDisposition.Reserved,
            commentKey);
    }

    public async Task MarkCommentPostedAsync(string id, string commentKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(commentKey);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var row = await db.GitHubIssueLinks.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
        {
            await transaction.CommitAsync(ct);
            return;
        }
        var posted = DeserializePosted(row.PostedCommentsJson);
        posted.Remove(ReservedCommentKey(commentKey));
        if (posted.Contains(commentKey))
        {
            await transaction.CommitAsync(ct);
            return;
        }
        posted.Add(commentKey);
        row.PostedCommentsJson = SerializePosted(posted);
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
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
        PostedComments = DeserializePosted(row.PostedCommentsJson)
            .Where(key => !key.StartsWith(ReservedCommentPrefix, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal),
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

    private const string ReservedCommentPrefix = "pending-comment:";

    private static string ReservedCommentKey(string commentKey) =>
        ReservedCommentPrefix + commentKey;

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
