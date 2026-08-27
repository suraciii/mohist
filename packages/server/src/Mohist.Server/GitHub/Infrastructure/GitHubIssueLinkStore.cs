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
    private static readonly TimeSpan OperationLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromMinutes(5);

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
    public async Task<IReadOnlyList<GitHubIssueLink>> ListByConnectionAsync(
        string projectId,
        string repositoryName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.GitHubIssueLinks.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.RepositoryName == repositoryName)
            .OrderBy(r => r.IssueNumber)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

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

    public async Task<GitHubIssueLink?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueLinks.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
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
            SyncStatus = GitHubSyncStatus.Healthy,
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

    public async Task<GitHubIssueLinkClaim> ClaimAsync(
        string projectId,
        string repositoryName,
        int githubIssueNumber,
        int issueNumber,
        CancellationToken ct = default)
    {
        var link = await CreateAsync(
            projectId,
            repositoryName,
            githubIssueNumber,
            issueNumber,
            commandRequested: false,
            ct: ct);
        var won = link.IssueNumber == issueNumber && link.GithubIssueNumber == githubIssueNumber;
        return won ? new GitHubIssueLinkClaim(true, link) : new GitHubIssueLinkClaim(false, null);
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
            SyncStatus = GitHubSyncStatus.Healthy,
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

    public async Task<GitHubIssueLink?> EnsureMirrorMarkerAsync(
        string id,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueLinks.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return null;
        if (string.IsNullOrWhiteSpace(row.MirrorMarker))
        {
            row.MirrorMarker = GitHubMirrorMarker.For(row.Id);
            row.UpdatedAt = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return ToDomain(row);
    }

    public async Task<GitHubIssueLink?> MarkErrorAsync(
        string id,
        GitHubSyncError error,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(error);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueLinks.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return null;
        row.SyncStatus = GitHubSyncStatus.Error;
        row.LastErrorOperation = error.Operation;
        row.LastErrorCode = error.Code;
        row.LastErrorDetail = error.Detail;
        row.LastErrorAt = error.OccurredAt;
        row.UpdatedAt = _timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    public async Task<GitHubIssueLink?> ClearErrorAsync(
        string id,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueLinks.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return null;
        if (row.SyncStatus != GitHubSyncStatus.Healthy || row.LastErrorOperation is not null
            || row.LastErrorCode is not null || row.LastErrorDetail is not null || row.LastErrorAt is not null)
        {
            row.SyncStatus = GitHubSyncStatus.Healthy;
            row.LastErrorOperation = null;
            row.LastErrorCode = null;
            row.LastErrorDetail = null;
            row.LastErrorAt = null;
            row.UpdatedAt = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
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

    /// <summary>
    /// Claims an existing pending mirror intent for a manual link. The
    /// conditional update is the ownership fence: once mirror creation has
    /// reserved the row, manual linking cannot delete or replace it.
    /// </summary>
    public async Task<GitHubIssueLinkClaim?> TryClaimPendingForManualLinkAsync(
        string id,
        int githubIssueNumber,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (githubIssueNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(githubIssueNumber));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var updated = 0;
        try
        {
            updated = await db.GitHubIssueLinks
                .Where(row => row.Id == id
                    && row.GithubIssueNumber <= 0
                    && !row.MirrorCreateAttempted)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.GithubIssueNumber, githubIssueNumber)
                    .SetProperty(row => row.MirrorCreateAttempted, true)
                    .SetProperty(row => row.UpdatedAt, _timeProvider.GetUtcNow()), ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another link won the GitHub issue identity race. Returning the
            // current row below lets the caller distinguish that owner from a
            // pending intent that was already reserved by mirror creation.
        }

        var row = await db.GitHubIssueLinks.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
        if (row is null)
            return null;
        var link = ToDomain(row);
        var won = updated == 1 || (link.IssueNumber > 0 && link.GithubIssueNumber == githubIssueNumber);
        return new GitHubIssueLinkClaim(won, won ? link : null);
    }

    public async Task<GitHubMirrorCreateReservation?> TryReserveMirrorCreateAsync(
        string id,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _timeProvider.GetUtcNow();
        var updated = await db.GitHubIssueLinks
            .Where(row => row.Id == id && row.GithubIssueNumber <= 0 && !row.MirrorCreateAttempted)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.MirrorCreateAttempted, true)
                .SetProperty(row => row.UpdatedAt, now), ct);
        var row = await db.GitHubIssueLinks.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
        return row is null ? null : new GitHubMirrorCreateReservation(ToDomain(row), updated == 1);
    }

    public async Task<GitHubIssueLink?> MarkMirrorCreateAttemptedAsync(
        string id,
        CancellationToken ct = default)
    {
        var reservation = await TryReserveMirrorCreateAsync(id, ct);
        return reservation?.Link;
    }

    /// <summary>
    /// Releases a mirror-create reservation only while the link is still
    /// pending. A definite provider rejection can safely be retried; an
    /// unknown outcome must keep the reservation so reconciliation, rather
    /// than a second POST, remains the next operation.
    /// </summary>
    public async Task<GitHubIssueLink?> ResetMirrorCreateAttemptAsync(
        string id,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.GitHubIssueLinks
            .Where(row => row.Id == id && row.GithubIssueNumber <= 0 && row.MirrorCreateAttempted)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.MirrorCreateAttempted, false)
                .SetProperty(row => row.UpdatedAt, _timeProvider.GetUtcNow()), ct);
        var row = await db.GitHubIssueLinks.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<GitHubIssueLink?> ResetMirrorAsync(
        string id,
        int expectedGithubIssueNumber,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (expectedGithubIssueNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedGithubIssueNumber));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = _timeProvider.GetUtcNow();
        var updated = await db.GitHubIssueLinks
            .Where(row => row.Id == id && row.GithubIssueNumber == expectedGithubIssueNumber)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.GithubIssueNumber, 0)
                .SetProperty(row => row.MirrorCreateAttempted, false)
                .SetProperty(row => row.PostedCommentsJson, "[]")
                .SetProperty(row => row.StateLabel, (string?)null)
                .SetProperty(row => row.UpdatedAt, now), ct);
        if (updated == 1)
        {
            await db.GitHubIssueCommentOperations
                .Where(operation => operation.LinkId == id)
                .ExecuteDeleteAsync(ct);
        }
        await transaction.CommitAsync(ct);
        var row = await db.GitHubIssueLinks.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.GitHubIssueLinks.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is not null)
        {
            await db.GitHubIssueCommentOperations
                .Where(operation => operation.LinkId == id)
                .ExecuteDeleteAsync(ct);
            db.GitHubIssueLinks.Remove(row);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public Task<bool> TryReserveCommentAsync(
        string id,
        string commentKey,
        CancellationToken ct = default) =>
        TryReserveCommentAsync(
            id,
            commentKey,
            GitHubCommentOperationKind.Comment,
            body: string.Empty,
            stateReason: null,
            ct);

    public async Task<bool> TryReserveCommentAsync(
        string id,
        string commentKey,
        string kind,
        string? body,
        string? stateReason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(commentKey);
        if (kind is not (GitHubCommentOperationKind.Comment or GitHubCommentOperationKind.Close))
            throw new ArgumentException("Unknown GitHub comment operation kind.", nameof(kind));
        if (kind == GitHubCommentOperationKind.Comment && body is null)
            throw new ArgumentNullException(nameof(body));
        if (kind == GitHubCommentOperationKind.Close && string.IsNullOrWhiteSpace(stateReason))
            throw new ArgumentException("A close operation requires a state reason.", nameof(stateReason));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var link = await db.GitHubIssueLinks.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, ct);
        if (link is null || link.GithubIssueNumber <= 0 || DeserializePosted(link.PostedCommentsJson).Contains(commentKey))
            return false;

        var existing = await db.GitHubIssueCommentOperations.AsNoTracking()
            .FirstOrDefaultAsync(operation => operation.LinkId == id && operation.CommentKey == commentKey, ct);
        if (existing is not null)
            return false;

        var now = _timeProvider.GetUtcNow();
        db.GitHubIssueCommentOperations.Add(new GitHubIssueCommentOperationRow
        {
            Id = $"ghcomment_{Guid.NewGuid():N}",
            LinkId = id,
            CommentKey = commentKey,
            Kind = kind,
            Body = body,
            StateReason = stateReason,
            Marker = kind == GitHubCommentOperationKind.Comment
                ? GitHubCommentOperationMarker.For(id, commentKey)
                : null,
            Status = GitHubCommentOperationStatus.Reserved,
            AttemptCount = 0,
            NextAttemptAt = null,
            LeaseUntil = now.Add(OperationLeaseDuration),
            LastError = null,
            FailedAt = null,
            CreatedAt = now,
            UpdatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsCommentOperationUniqueViolation(ex))
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<GitHubIssueCommentOperation>> ListPendingCommentOperationsAsync(
        int limit = 100,
        CancellationToken ct = default)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var now = _timeProvider.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.GitHubIssueCommentOperations.AsNoTracking()
            .Where(operation => operation.Status == GitHubCommentOperationStatus.Reserved)
            .ToListAsync(ct);
        return rows
            .Where(operation => (operation.NextAttemptAt is null || operation.NextAttemptAt <= now)
                && (operation.LeaseUntil is null || operation.LeaseUntil <= now))
            .OrderBy(operation => operation.CreatedAt)
            .Take(limit)
            .Select(ToDomain)
            .ToList();
    }

    public async Task<GitHubIssueCommentOperation?> TryClaimCommentOperationAsync(
        string id,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        var now = _timeProvider.GetUtcNow();
        var leaseUntil = now.Add(leaseDuration);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "GitHubIssueCommentOperations"
            SET "LeaseUntil" = {leaseUntil}, "UpdatedAt" = {now}
            WHERE "Id" = {id}
              AND "Status" = {GitHubCommentOperationStatus.Reserved}
              AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {now})
              AND ("LeaseUntil" IS NULL OR "LeaseUntil" <= {now})
            """, ct);
        if (affected != 1)
            return null;
        var row = await db.GitHubIssueCommentOperations.AsNoTracking()
            .SingleOrDefaultAsync(operation => operation.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<GitHubIssueCommentOperation?> DeferCommentOperationAsync(
        string linkId,
        string commentKey,
        string error,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commentKey);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var id = await db.GitHubIssueCommentOperations
            .Where(operation => operation.LinkId == linkId && operation.CommentKey == commentKey)
            .Select(operation => operation.Id)
            .SingleOrDefaultAsync(ct);
        return id is null ? null : await DeferCommentOperationAsync(id, error, ct);
    }

    public async Task<GitHubIssueCommentOperation?> DeferCommentOperationAsync(
        string id,
        string error,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueCommentOperations
            .FirstOrDefaultAsync(operation => operation.Id == id, ct);
        if (row is null || row.Status != GitHubCommentOperationStatus.Reserved)
            return row is null ? null : ToDomain(row);
        var now = _timeProvider.GetUtcNow();
        row.AttemptCount++;
        row.LastError = error;
        row.LeaseUntil = null;
        row.NextAttemptAt = now.Add(OperationBackoff(row.AttemptCount));
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    public async Task<GitHubIssueCommentOperation?> MarkCommentOperationAmbiguousAsync(
        string id,
        string error,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueCommentOperations
            .FirstOrDefaultAsync(operation => operation.Id == id, ct);
        if (row is null)
            return null;
        if (row.Status == GitHubCommentOperationStatus.Reserved)
        {
            var now = _timeProvider.GetUtcNow();
            row.Status = GitHubCommentOperationStatus.Ambiguous;
            row.LastError = error;
            row.FailedAt = now;
            row.LeaseUntil = null;
            row.NextAttemptAt = null;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }
        return ToDomain(row);
    }

    public async Task MarkCommentPostedAsync(string id, string commentKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(commentKey);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.GitHubIssueLinks.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null)
            return;

        var now = _timeProvider.GetUtcNow();
        var operation = await db.GitHubIssueCommentOperations
            .FirstOrDefaultAsync(item => item.LinkId == id && item.CommentKey == commentKey, ct);
        if (operation is null)
        {
            operation = new GitHubIssueCommentOperationRow
            {
                Id = $"ghcomment_{Guid.NewGuid():N}",
                LinkId = id,
                CommentKey = commentKey,
                Kind = GitHubCommentOperationKind.Comment,
                Status = GitHubCommentOperationStatus.Posted,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.GitHubIssueCommentOperations.Add(operation);
        }
        else if (operation.Status == GitHubCommentOperationStatus.Ambiguous)
        {
            // A recovery worker found conflicting remote evidence. A stale
            // in-flight owner must not overwrite that fail-closed decision.
            return;
        }
        else if (operation.Status != GitHubCommentOperationStatus.Posted)
        {
            operation.Status = GitHubCommentOperationStatus.Posted;
            operation.LeaseUntil = null;
            operation.NextAttemptAt = null;
            operation.LastError = null;
            operation.UpdatedAt = now;
        }

        var posted = DeserializePosted(row.PostedCommentsJson);
        if (posted.Add(commentKey))
        {
            row.PostedCommentsJson = SerializePosted(posted);
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task ReleaseCommentReservationAsync(string id, string commentKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(commentKey);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.GitHubIssueCommentOperations
            .Where(operation => operation.LinkId == id
                && operation.CommentKey == commentKey
                && operation.Status == GitHubCommentOperationStatus.Reserved)
            .ExecuteDeleteAsync(ct);
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

    private static GitHubIssueCommentOperation ToDomain(GitHubIssueCommentOperationRow row) => new()
    {
        Id = row.Id,
        LinkId = row.LinkId,
        CommentKey = row.CommentKey,
        Kind = string.IsNullOrWhiteSpace(row.Kind) ? GitHubCommentOperationKind.Comment : row.Kind,
        Body = row.Body,
        StateReason = row.StateReason,
        Marker = row.Marker,
        Status = row.Status,
        AttemptCount = row.AttemptCount,
        NextAttemptAt = row.NextAttemptAt,
        LeaseUntil = row.LeaseUntil,
        LastError = row.LastError,
        FailedAt = row.FailedAt,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };

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
        SyncStatus = string.IsNullOrWhiteSpace(row.SyncStatus) ? GitHubSyncStatus.Healthy : row.SyncStatus,
        LastError = row.LastErrorOperation is null || row.LastErrorCode is null || row.LastErrorDetail is null || row.LastErrorAt is null
            ? null
            : new GitHubSyncError(row.LastErrorOperation, row.LastErrorCode, row.LastErrorDetail, row.LastErrorAt.Value),
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

    private static TimeSpan OperationBackoff(int attemptCount)
    {
        var multiplier = Math.Pow(2, Math.Min(Math.Max(attemptCount - 1, 0), 10));
        var ticks = Math.Min(RetryBaseDelay.Ticks * multiplier, RetryMaxDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite
        && sqlite.SqliteErrorCode == 19
        && sqlite.Message.Contains("GitHubIssueLinks", StringComparison.OrdinalIgnoreCase);

    private static bool IsIssueUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite
        && sqlite.SqliteErrorCode == 19
        && sqlite.Message.Contains("IssueNumber", StringComparison.OrdinalIgnoreCase);

    private static bool IsCommentOperationUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite
        && sqlite.SqliteErrorCode == 19
        && sqlite.Message.Contains("GitHubIssueCommentOperations", StringComparison.OrdinalIgnoreCase);
}
