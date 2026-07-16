using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Issue.Services.Attachments;

public sealed class AttachmentService : IScopedService
{
    public const string OwnerKindIssue = "issue";
    public const string OwnerKindComment = "comment";
    public static readonly TimeSpan PendingTtl = TimeSpan.FromHours(24);

    private static readonly Regex AttachmentReferenceRegex = new(
        @"!?\[[^\]]*\]\(att:(?<id>att_[A-Za-z0-9_\-]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> InlineImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
    };

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly IAttachmentStorage _storage;
    private readonly AttachmentStorageOptions _options;
    private readonly TimeProvider _time;

    public AttachmentService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IAttachmentStorage storage,
        IOptions<AttachmentStorageOptions> options)
        : this(dbFactory, storage, options.Value, TimeProvider.System)
    {
    }

    public AttachmentService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IAttachmentStorage storage,
        AttachmentStorageOptions options,
        TimeProvider time)
    {
        _dbFactory = dbFactory;
        _storage = storage;
        _options = options;
        _time = time;
    }

    public async Task<AttachmentUploadResult> UploadAsync(
        string projectId,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file.Length > _options.MaxFileBytes)
            throw new AttachmentLimitException($"Attachment upload exceeds the configured size limit of {_options.MaxFileBytes} bytes.");
        if (file.Length < 0)
            throw new AttachmentValidationException("Attachment size is invalid.");

        var attachmentId = $"att_{Guid.NewGuid():N}";
        var now = _time.GetUtcNow();
        var storagePath = _storage.GenerateStoragePath(projectId, attachmentId);
        await using var content = file.OpenReadStream();
        AttachmentStorageWriteResult write;
        try
        {
            write = await _storage.WriteFileAsync(
                storagePath,
                content,
                new AttachmentFileWrite
                {
                    OriginalFileName = SanitizeFileName(file.FileName),
                    ContentType = NormalizeContentType(file.ContentType),
                    Size = file.Length,
                    MaxSize = _options.MaxFileBytes,
                },
                now,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AttachmentStorageLimitException ex)
        {
            throw new AttachmentLimitException(ex.Message);
        }

        var row = new AttachmentRow
        {
            Id = attachmentId,
            ProjectId = projectId,
            OwnerKind = null,
            OwnerId = null,
            OriginalFileName = SanitizeFileName(file.FileName),
            ContentType = NormalizeContentType(file.ContentType),
            Size = write.Size,
            StoragePath = write.StoragePath,
            CreatedAt = now,
            ExpiresAt = now.Add(PendingTtl),
        };

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Attachments.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToUploadResult(row);
    }

    public async Task BindIssueAsync(
        string projectId,
        string issueId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken = default) =>
        await BindAsync(projectId, OwnerKindIssue, issueId, attachmentIds, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Unbinds every attachment currently owned by the given issue. Used by
    /// the PATCH path when <c>attachmentIds</c> is present-and-null, which is
    /// the "clear all" three-state case (<c>absent</c> means keep; <c>null</c>
    /// means clear; <c>value</c> means replace).
    /// </summary>
    public async Task UnbindAllIssueAsync(
        string projectId,
        string issueId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Attachments.Where(a =>
                a.ProjectId == projectId
                && a.OwnerKind == OwnerKindIssue
                && a.OwnerId == issueId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return;

        foreach (var row in rows)
        {
            row.OwnerKind = null;
            row.OwnerId = null;
            row.ExpiresAt = _time.GetUtcNow().Add(PendingTtl);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces every attachment currently owned by the issue with the given
    /// list. The PATCH path uses this when <c>attachmentIds</c> is present
    /// with a value (replace semantics). The combined limit is checked
    /// against the new list size (not the prior size), so this path requires
    /// the caller to have already validated the new list length.
    /// </summary>
    public async Task ReplaceIssueAsync(
        string projectId,
        string issueId,
        IReadOnlyCollection<string> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        var ids = await ValidateBindAsync(projectId, OwnerKindIssue, issueId, attachmentIds, cancellationToken).ConfigureAwait(false);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Length > 0)
        {
            var rows = await db.Attachments.Where(a =>
                    a.ProjectId == projectId
                    && ids.Contains(a.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in rows)
            {
                row.OwnerKind = OwnerKindIssue;
                row.OwnerId = issueId;
                row.ExpiresAt = null;
            }
        }

        // Unbind anything previously bound to the issue that is no longer in
        // the new list, so that present-with-value behaves as a full replace.
        var keepSet = ids.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(ids, StringComparer.Ordinal);
        var stale = await db.Attachments
            .Where(a => a.ProjectId == projectId
                && a.OwnerKind == OwnerKindIssue
                && a.OwnerId == issueId
                && !keepSet.Contains(a.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (stale.Count > 0)
        {
            var pendingExpiry = _time.GetUtcNow().Add(PendingTtl);
            foreach (var row in stale)
            {
                row.OwnerKind = null;
                row.OwnerId = null;
                row.ExpiresAt = pendingExpiry;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ValidateIssueBindAsync(
        string projectId,
        string issueId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken = default) =>
        await ValidateBindAsync(projectId, OwnerKindIssue, issueId, attachmentIds, cancellationToken).ConfigureAwait(false);

    public async Task BindCommentAsync(
        string projectId,
        string commentId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken = default) =>
        await BindAsync(projectId, OwnerKindComment, commentId, attachmentIds, cancellationToken).ConfigureAwait(false);

    public async Task ValidateCommentBindAsync(
        string projectId,
        string commentId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken = default) =>
        await ValidateBindAsync(projectId, OwnerKindComment, commentId, attachmentIds, cancellationToken).ConfigureAwait(false);

    public async Task<AttachmentContentResult?> OpenIssueContentAsync(
        string projectId,
        string issueId,
        string attachmentId,
        CancellationToken cancellationToken = default) =>
        await OpenContentAsync(projectId, OwnerKindIssue, issueId, attachmentId, cancellationToken).ConfigureAwait(false);

    public async Task<AttachmentContentResult?> OpenCommentContentAsync(
        string projectId,
        int issueNumber,
        string commentId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (await LoadCommentAsync(projectId, issueNumber, commentId, cancellationToken).ConfigureAwait(false) is null)
            return null;
        return await OpenContentAsync(projectId, OwnerKindComment, commentId, attachmentId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AttachmentRemovalResult> RemoveIssueAttachmentAsync(
        string projectId,
        Domain.Issue issue,
        string attachmentId,
        IIssueGrain grain,
        CancellationToken cancellationToken = default)
    {
        if (!IsIssueEditable(issue))
            throw new AttachmentEditabilityException("Attachment cannot be removed because the issue is no longer editable.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Attachments.FirstOrDefaultAsync(a =>
            a.ProjectId == projectId
            && a.Id == attachmentId
            && a.OwnerKind == OwnerKindIssue
            && a.OwnerId == issue.Id,
            cancellationToken).ConfigureAwait(false);
        if (row is null) return AttachmentRemovalResult.NotFound;

        var updatedBody = StripReferences(issue.Body, attachmentId);
        await grain.UpdateFullAsync(new UpdateIssueData(
            Title: issue.Title,
            Body: updatedBody,
            Labels: issue.Labels,
            Priority: issue.Priority,
            AttachmentIds: [],
            PresentFields: new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(UpdateIssueData.Body),
                nameof(UpdateIssueData.AttachmentIds),
            }));
        await _storage.DeleteAsync(row.StoragePath, cancellationToken).ConfigureAwait(false);
        db.Attachments.Remove(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return AttachmentRemovalResult.Removed;
    }

    public async Task<AttachmentRemovalResult> RemoveCommentAttachmentAsync(
        string projectId,
        int issueNumber,
        string commentId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        var comment = await LoadCommentAsync(projectId, issueNumber, commentId, cancellationToken).ConfigureAwait(false);
        if (comment is null) return AttachmentRemovalResult.NotFound;
        if (!await IsIssueEditableAsync(projectId, comment.IssueId, cancellationToken).ConfigureAwait(false))
            throw new AttachmentEditabilityException("Attachment cannot be removed because the comment owner is no longer editable.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Attachments.FirstOrDefaultAsync(a =>
            a.ProjectId == projectId
            && a.Id == attachmentId
            && a.OwnerKind == OwnerKindComment
            && a.OwnerId == comment.Id,
            cancellationToken).ConfigureAwait(false);
        if (row is null) return AttachmentRemovalResult.NotFound;

        var storedComment = await db.IssueComments.FirstAsync(c => c.Id == comment.Id, cancellationToken).ConfigureAwait(false);
        storedComment.Body = StripReferences(storedComment.Body, attachmentId) ?? string.Empty;
        await _storage.DeleteAsync(row.StoragePath, cancellationToken).ConfigureAwait(false);
        db.Attachments.Remove(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return AttachmentRemovalResult.Removed;
    }

    public async Task<int> CleanupExpiredPendingAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pending = await db.Attachments
            .Where(a => a.OwnerKind == null && a.ExpiresAt != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var expired = pending
            .Where(a => a.ExpiresAt <= now)
            .ToList();

        foreach (var row in expired)
        {
            await _storage.DeleteAsync(row.StoragePath, cancellationToken).ConfigureAwait(false);
            db.Attachments.Remove(row);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return expired.Count;
    }

    private async Task BindAsync(
        string projectId,
        string ownerKind,
        string ownerId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken)
    {
        var ids = await ValidateBindAsync(projectId, ownerKind, ownerId, attachmentIds, cancellationToken).ConfigureAwait(false);
        if (ids.Length == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Attachments.Where(a =>
                a.ProjectId == projectId
                && ids.Contains(a.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.OwnerKind = ownerKind;
            row.OwnerId = ownerId;
            row.ExpiresAt = null;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string[]> ValidateBindAsync(
        string projectId,
        string ownerKind,
        string ownerId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken)
    {
        var ids = attachmentIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (ids.Length == 0) return [];

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existingCount = await db.Attachments.CountAsync(a =>
            a.ProjectId == projectId
            && a.OwnerKind == ownerKind
            && a.OwnerId == ownerId,
            cancellationToken).ConfigureAwait(false);
        if (existingCount + ids.Length > _options.MaxCountPerOwner)
            throw new AttachmentLimitException($"Attachment count exceeds the configured per-owner limit of {_options.MaxCountPerOwner}.");

        var rows = await db.Attachments.AsNoTracking().Where(a =>
                a.ProjectId == projectId
                && ids.Contains(a.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count != ids.Length)
            throw new AttachmentValidationException("One or more attachment ids were not found for this project.");

        foreach (var row in rows)
        {
            if (row.OwnerKind is not null)
                throw new AttachmentValidationException($"Attachment '{row.Id}' is already bound to an owner.");
        }

        return ids;
    }

    private async Task<AttachmentContentResult?> OpenContentAsync(
        string projectId,
        string ownerKind,
        string ownerId,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a =>
            a.ProjectId == projectId
            && a.Id == attachmentId
            && a.OwnerKind == ownerKind
            && a.OwnerId == ownerId,
            cancellationToken).ConfigureAwait(false);
        if (row is null) return null;

        var stream = _storage.OpenFileContent(row.StoragePath);
        var contentType = NormalizeContentType(row.ContentType) ?? "application/octet-stream";
        var dispositionType = InlineImageContentTypes.Contains(contentType) ? "inline" : "attachment";
        return new AttachmentContentResult(stream, contentType, BuildContentDisposition(dispositionType, row.OriginalFileName));
    }

    private async Task<bool> IsIssueEditableAsync(string projectId, string issueId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Issues.AsNoTracking()
            .FirstOrDefaultAsync(i => i.ProjectId == projectId && i.IssueId == issueId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return false;
        var issue = IssueStore.Deserialize(row.State);
        return issue is not null && IsIssueEditable(issue);
    }

    private async Task<IssueCommentRow?> LoadCommentAsync(
        string projectId,
        int issueNumber,
        string commentId,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.IssueComments.AsNoTracking().FirstOrDefaultAsync(c =>
            c.ProjectId == projectId && c.IssueNumber == issueNumber && c.Id == commentId,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsIssueEditable(Domain.Issue issue) =>
        issue.ArchivedAt is null && issue.Status is not IssueStatus.Done and not IssueStatus.Cancelled;

    private static string? StripReferences(string? body, string attachmentId)
    {
        if (string.IsNullOrEmpty(body)) return body;
        return AttachmentReferenceRegex.Replace(body, match =>
            string.Equals(match.Groups["id"].Value, attachmentId, StringComparison.Ordinal) ? string.Empty : match.Value);
    }

    private static AttachmentUploadResult ToUploadResult(AttachmentRow row) => new(
        row.Id,
        row.OriginalFileName,
        row.ContentType,
        row.Size,
        row.ExpiresAt);

    private static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName);
        return string.IsNullOrWhiteSpace(name) ? "attachment" : name;
    }

    private static string? NormalizeContentType(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? null : contentType.Trim();

    private static string BuildContentDisposition(string dispositionType, string fileName)
    {
        var escaped = SanitizeFileName(fileName).Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"{dispositionType}; filename=\"{escaped}\"";
    }
}

public sealed record AttachmentUploadResult(
    string Id,
    string FileName,
    string? ContentType,
    long Size,
    DateTimeOffset? ExpiresAt);

public sealed record AttachmentContentResult(
    Stream Content,
    string ContentType,
    string ContentDisposition);

public enum AttachmentRemovalResult
{
    Removed,
    NotFound,
}

public class AttachmentValidationException(string message) : Exception(message);

public sealed class AttachmentLimitException(string message) : AttachmentValidationException(message);

public sealed class AttachmentEditabilityException(string message) : AttachmentValidationException(message);
