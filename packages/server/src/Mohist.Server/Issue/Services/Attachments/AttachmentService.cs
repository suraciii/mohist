using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Contracts;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.Issue.Services.Attachments;

public sealed class AttachmentService : IScopedService
{
    public const string OwnerKindIssue = "issue";
    public const string OwnerKindComment = "comment";
    public const string OwnerKindAgentInput = "agent-input";
    public const string AgentInputAttachmentReservationPending = "pending";
    public const string AgentInputAttachmentReservationCoordinatorPending = "coordinator-pending";
    public const string AgentInputAttachmentReservationAdopted = "adopted";
    private const string AgentInputAttachmentReservationReleased = "released";
    public static readonly TimeSpan PendingTtl = TimeSpan.FromHours(24);
    public const string AgentInputOwnerIdSeparator = "/";
    public const string DefaultUploadSource = "upload";

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
            Source = DefaultUploadSource,
        };

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Attachments.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToUploadResult(row);
    }

    /// <summary>
    /// Server-side analog of <see cref="UploadAsync"/> for provider-sourced
    /// streams (e.g. Slack files fetched Server-side via the Connection's
    /// bot token). Writes the bytes into <see cref="IAttachmentStorage"/>
    /// under a caller-supplied <paramref name="deterministicId"/>, creating
    /// a pending <see cref="AttachmentRow"/> stamped with the supplied
    /// <paramref name="source"/>. Insert-if-absent on the id: when a row
    /// with that id already exists it is returned unchanged and nothing
    /// is written, so redelivery is a storage-level no-op.
    /// </summary>
    public async Task<AttachmentUploadResult> IngestProviderFileAsync(
        string projectId,
        string deterministicId,
        string source,
        string fileName,
        string? contentType,
        long size,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deterministicId))
            throw new ArgumentException("Deterministic attachment id is required.", nameof(deterministicId));
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Attachment source is required.", nameof(source));
        ArgumentNullException.ThrowIfNull(content);
        if (size < 0)
            throw new AttachmentValidationException("Attachment size is invalid.");
        if (size > _options.MaxFileBytes)
            throw new AttachmentLimitException($"Attachment upload exceeds the configured size limit of {_options.MaxFileBytes} bytes.");

        var now = _time.GetUtcNow();
        var sanitizedFileName = SanitizeFileName(fileName);
        var normalizedContentType = NormalizeContentType(contentType);
        var storagePath = _storage.GenerateStoragePath(projectId, deterministicId);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.Attachments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == deterministicId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            await content.DisposeAsync().ConfigureAwait(false);
            return ToUploadResult(existing);
        }

        AttachmentStorageWriteResult write;
        try
        {
            write = await _storage.WriteFileAsync(
                storagePath,
                content,
                new AttachmentFileWrite
                {
                    OriginalFileName = sanitizedFileName,
                    ContentType = normalizedContentType,
                    Size = size,
                    MaxSize = _options.MaxFileBytes,
                },
                now,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AttachmentStorageLimitException ex)
        {
            await content.DisposeAsync().ConfigureAwait(false);
            throw new AttachmentLimitException(ex.Message);
        }

        var row = new AttachmentRow
        {
            Id = deterministicId,
            ProjectId = projectId,
            OwnerKind = null,
            OwnerId = null,
            OriginalFileName = sanitizedFileName,
            ContentType = normalizedContentType,
            Size = write.Size,
            StoragePath = write.StoragePath,
            CreatedAt = now,
            ExpiresAt = now.Add(PendingTtl),
            Source = source,
        };

        db.Attachments.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent ingest for the same deterministic id won the row.
            // The bytes were written to the same content-addressed path the
            // winner occupies (GenerateStoragePath is deterministic on the id),
            // so there is nothing of ours to delete — removing the path would
            // destroy the winner's content. Just adopt the winning row.
            var winning = await LoadRowAsync(deterministicId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Attachment row '{deterministicId}' disappeared between insert and conflict resolution.");
            return ToUploadResult(winning);
        }

        return ToUploadResult(row);
    }

    public async Task<bool> ExistsAsync(
        string projectId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Attachments.AsNoTracking().AnyAsync(
            row => row.ProjectId == projectId && row.Id == attachmentId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AttachmentRow?> LoadRowAsync(string id, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Attachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task BindIssueAsync(
        string projectId,
        int issueNumber,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken = default)
    {
        var ids = await ValidateIssueBindCoreAsync(projectId, issueNumber, attachmentIds, cancellationToken).ConfigureAwait(false);
        if (ids.Length == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Attachments.Where(a =>
                a.ProjectId == projectId
                && ids.Contains(a.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.OwnerKind = OwnerKindIssue;
            row.OwnerId = null;
            row.OwnerIssueNumber = issueNumber;
            row.ExpiresAt = null;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Unbinds every attachment currently owned by the given issue. Used by
    /// the PATCH path when <c>attachmentIds</c> is present-and-null, which is
    /// the "clear all" three-state case (<c>absent</c> means keep; <c>null</c>
    /// means clear; <c>value</c> means replace).
    /// </summary>
    public async Task UnbindAllIssueAsync(
        string projectId,
        int issueNumber,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Attachments.Where(a =>
                a.ProjectId == projectId
                && a.OwnerKind == OwnerKindIssue
                && a.OwnerIssueNumber == issueNumber)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return;

        foreach (var row in rows)
        {
            row.OwnerKind = null;
            row.OwnerId = null;
            row.OwnerIssueNumber = null;
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
        int issueNumber,
        IReadOnlyCollection<string> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        var ids = await ValidateIssueBindCoreAsync(projectId, issueNumber, attachmentIds, cancellationToken).ConfigureAwait(false);

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
                row.OwnerId = null;
                row.OwnerIssueNumber = issueNumber;
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
                && a.OwnerIssueNumber == issueNumber
                && !keepSet.Contains(a.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (stale.Count > 0)
        {
            var pendingExpiry = _time.GetUtcNow().Add(PendingTtl);
            foreach (var row in stale)
            {
                row.OwnerKind = null;
                row.OwnerId = null;
                row.OwnerIssueNumber = null;
                row.ExpiresAt = pendingExpiry;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ValidateIssueBindAsync(
        string projectId,
        int issueNumber,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken = default) =>
        await ValidateIssueBindCoreAsync(projectId, issueNumber, attachmentIds, cancellationToken).ConfigureAwait(false);

    public async Task BindCommentAsync(
        string projectId,
        string commentId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken = default)
    {
        var ids = await ValidateCommentBindCoreAsync(projectId, commentId, attachmentIds, cancellationToken).ConfigureAwait(false);
        if (ids.Length == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Attachments.Where(a =>
                a.ProjectId == projectId
                && ids.Contains(a.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.OwnerKind = OwnerKindComment;
            row.OwnerId = commentId;
            row.OwnerIssueNumber = null;
            row.ExpiresAt = null;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ValidateCommentBindAsync(
        string projectId,
        string commentId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken = default) =>
        await ValidateCommentBindCoreAsync(projectId, commentId, attachmentIds, cancellationToken).ConfigureAwait(false);

    public async Task BindAgentInputAsync(
        string projectId,
        string agentSessionId,
        string inputId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken = default)
    {
        EnsureAgentInputOwnerScope(agentSessionId, inputId);
        var ownerId = BuildAgentInputOwnerId(agentSessionId, inputId);
        var ids = await ValidateAgentInputBindCoreAsync(projectId, ownerId, attachmentIds, cancellationToken).ConfigureAwait(false);
        if (ids.Length == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Attachments.Where(a =>
                a.ProjectId == projectId
                && ids.Contains(a.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.OwnerKind = OwnerKindAgentInput;
            row.OwnerId = ownerId;
            row.OwnerIssueNumber = null;
            row.ExpiresAt = null;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AttachmentContentResult?> OpenAgentInputContentAsync(
        string projectId,
        string agentSessionId,
        string inputId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        EnsureAgentInputOwnerScope(agentSessionId, inputId);
        var ownerId = BuildAgentInputOwnerId(agentSessionId, inputId);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a =>
            a.ProjectId == projectId
            && a.Id == attachmentId
            && a.OwnerKind == OwnerKindAgentInput
            && a.OwnerId == ownerId,
            cancellationToken).ConfigureAwait(false);
        return row is null ? null : OpenContent(row);
    }

    public async Task ValidateAgentInputBindAsync(
        string projectId,
        string agentSessionId,
        string inputId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken = default) =>
        await ValidateAgentInputBindCoreAsync(projectId, BuildAgentInputOwnerId(agentSessionId, inputId), attachmentIds, cancellationToken).ConfigureAwait(false);

    public async Task UnbindAgentInputAsync(
        string projectId,
        string agentSessionId,
        string inputId,
        IReadOnlyCollection<string> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        EnsureAgentInputOwnerScope(agentSessionId, inputId);
        if (attachmentIds.Count == 0) return;

        var ownerId = BuildAgentInputOwnerId(agentSessionId, inputId);
        var pendingExpiry = _time.GetUtcNow().Add(PendingTtl);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Attachments
            .Where(row => row.ProjectId == projectId
                && attachmentIds.Contains(row.Id)
                && row.OwnerKind == OwnerKindAgentInput
                && row.OwnerId == ownerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.OwnerKind, (string?)null)
                .SetProperty(row => row.OwnerId, (string?)null)
                .SetProperty(row => row.OwnerIssueNumber, (int?)null)
                .SetProperty(row => row.ExpiresAt, (DateTimeOffset?)pendingExpiry),
                cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the launch attempt's reservation into the coordinator-owned
    /// phase before the coordinator writes its durable plan. A route can
    /// release only pending reservations, so a response-loss cleanup cannot
    /// remove an attachment that a plan may now adopt. If the process stops
    /// after this fence and before plan persistence, the expiry recovery path
    /// removes the coordinator-pending reservation.
    /// </summary>
    public async Task MarkAgentInputReservationCoordinatorPendingAsync(
        string projectId,
        string reservationId,
        string agentSessionId,
        string inputId,
        CancellationToken cancellationToken = default)
    {
        EnsureAgentInputOwnerScope(agentSessionId, inputId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);

        var ownerId = BuildAgentInputOwnerId(agentSessionId, inputId);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reservations = await db.AgentInputAttachmentReservations
                .Where(row => row.ProjectId == projectId
                    && row.ReservationId == reservationId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (reservations.Count == 0)
                throw new InvalidOperationException("Agent input attachment reservation no longer exists.");
            if (reservations.Any(row => !string.Equals(row.OwnerId, ownerId, StringComparison.Ordinal)))
                throw new InvalidOperationException("Agent input attachment reservation owner does not match the launch plan.");
            if (reservations.Any(row => row.Status is not AgentInputAttachmentReservationPending
                and not AgentInputAttachmentReservationCoordinatorPending
                and not AgentInputAttachmentReservationAdopted))
            {
                throw new InvalidOperationException("Agent input attachment reservation was already released.");
            }

            await db.AgentInputAttachmentReservations
                .Where(row => row.ProjectId == projectId
                    && row.ReservationId == reservationId
                    && row.OwnerId == ownerId
                    && row.Status == AgentInputAttachmentReservationPending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, AgentInputAttachmentReservationCoordinatorPending),
                    cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Adopts the launch attempt's coordinator-owned reservations after the
    /// coordinator has persisted its durable plan. A pending reservation is
    /// also accepted for recovery of plans created before the coordinator
    /// fence was introduced; an adopted reservation is released only by a
    /// coordinator rejection.
    /// </summary>
    public async Task AdoptAgentInputReservationAsync(
        string projectId,
        string reservationId,
        string agentSessionId,
        string inputId,
        CancellationToken cancellationToken = default)
    {
        EnsureAgentInputOwnerScope(agentSessionId, inputId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);

        var ownerId = BuildAgentInputOwnerId(agentSessionId, inputId);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var reservations = await db.AgentInputAttachmentReservations
            .Where(row => row.ProjectId == projectId
                && row.ReservationId == reservationId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (reservations.Count == 0)
            return;
        if (reservations.Any(row => !string.Equals(row.OwnerId, ownerId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Agent input attachment reservation owner does not match the launch plan.");
        if (reservations.Any(row => string.Equals(row.Status, AgentInputAttachmentReservationReleased, StringComparison.Ordinal)))
            throw new InvalidOperationException("Agent input attachment reservation was already released.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await db.AgentInputAttachmentReservations
                .Where(row => row.ProjectId == projectId
                    && row.ReservationId == reservationId
                    && row.OwnerId == ownerId
                    && (row.Status == AgentInputAttachmentReservationPending
                        || row.Status == AgentInputAttachmentReservationCoordinatorPending))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, AgentInputAttachmentReservationAdopted)
                    .SetProperty(row => row.ExpiresAt, (DateTimeOffset?)null),
                    cancellationToken).ConfigureAwait(false);

            // A same-key request may have left another pending reservation
            // behind before this plan won. Once this reservation is adopted,
            // those pre-plan attempts cannot become a second plan and can be
            // released without touching the adopted owner.
            var superseded = await db.AgentInputAttachmentReservations
                .Where(row => row.ProjectId == projectId
                    && row.OwnerId == ownerId
                    && row.ReservationId != reservationId
                    && (row.Status == AgentInputAttachmentReservationPending
                        || row.Status == AgentInputAttachmentReservationCoordinatorPending))
                .Select(row => new { row.ProjectId, row.AttachmentId, row.OwnerId })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (superseded.Count > 0)
            {
                var supersededIds = superseded
                    .Select(row => row.AttachmentId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                await db.AgentInputAttachmentReservations
                    .Where(row => row.ProjectId == projectId
                        && row.OwnerId == ownerId
                        && row.ReservationId != reservationId
                        && (row.Status == AgentInputAttachmentReservationPending
                            || row.Status == AgentInputAttachmentReservationCoordinatorPending))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

                var protectedIds = await db.AgentInputAttachmentReservations
                    .Where(row => row.ProjectId == projectId
                        && supersededIds.Contains(row.AttachmentId)
                        && row.Status != AgentInputAttachmentReservationReleased)
                    .Select(row => row.AttachmentId)
                    .Distinct()
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                var clearIds = supersededIds
                    .Except(protectedIds, StringComparer.Ordinal)
                    .ToArray();
                if (clearIds.Length > 0)
                {
                    await db.Attachments
                        .Where(row => row.ProjectId == projectId
                            && clearIds.Contains(row.Id)
                            && row.OwnerKind == OwnerKindAgentInput
                            && row.OwnerId == ownerId)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(row => row.OwnerKind, (string?)null)
                            .SetProperty(row => row.OwnerId, (string?)null)
                            .SetProperty(row => row.OwnerIssueNumber, (int?)null)
                            .SetProperty(row => row.ExpiresAt, _time.GetUtcNow().Add(PendingTtl)),
                            cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Releases only this launch attempt's pending reservation. The owner is
    /// cleared only when no other pending or adopted reservation protects the
    /// same attachment, so a cancelled request cannot remove a concurrent
    /// request's dispatch-readable input.
    /// </summary>
    public Task ReleaseAgentInputReservationAsync(
        string projectId,
        string reservationId,
        string agentSessionId,
        string inputId,
        IReadOnlyCollection<string> attachmentIds,
        CancellationToken cancellationToken = default) =>
        ReleaseAgentInputReservationCoreAsync(
            projectId,
            reservationId,
            agentSessionId,
            inputId,
            attachmentIds,
            releaseAdopted: false,
            cancellationToken);

    public Task ReleaseAdoptedAgentInputReservationAsync(
        string projectId,
        string reservationId,
        string agentSessionId,
        string inputId,
        CancellationToken cancellationToken = default) =>
        ReleaseAgentInputReservationCoreAsync(
            projectId,
            reservationId,
            agentSessionId,
            inputId,
            attachmentIds: null,
            releaseAdopted: true,
            cancellationToken);

    private async Task ReleaseAgentInputReservationCoreAsync(
        string projectId,
        string reservationId,
        string agentSessionId,
        string inputId,
        IReadOnlyCollection<string>? attachmentIds,
        bool releaseAdopted,
        CancellationToken cancellationToken)
    {
        EnsureAgentInputOwnerScope(agentSessionId, inputId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);

        var ownerId = BuildAgentInputOwnerId(agentSessionId, inputId);
        var ids = attachmentIds is null
            ? null
            : NormalizeAttachmentIds(attachmentIds);
        if (ids is { Length: 0 })
            return;

        var pendingExpiry = _time.GetUtcNow().Add(PendingTtl);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reservationQuery = db.AgentInputAttachmentReservations.Where(row =>
                row.ProjectId == projectId
                && row.ReservationId == reservationId
                && row.OwnerId == ownerId);
            if (ids is not null)
                reservationQuery = reservationQuery.Where(row => ids.Contains(row.AttachmentId));

            var reservationAttachmentIds = await reservationQuery
                .Select(row => row.AttachmentId)
                .Distinct()
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var releasableStatuses = releaseAdopted
                ? new[] { AgentInputAttachmentReservationPending, AgentInputAttachmentReservationAdopted }
                : new[] { AgentInputAttachmentReservationPending };
            await reservationQuery
                .Where(row => releasableStatuses.Contains(row.Status))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, AgentInputAttachmentReservationReleased),
                    cancellationToken).ConfigureAwait(false);

            if (reservationAttachmentIds.Length > 0)
            {
                var protectedAttachmentIds = await db.AgentInputAttachmentReservations
                    .Where(row => row.ProjectId == projectId
                        && reservationAttachmentIds.Contains(row.AttachmentId)
                        && row.Status != AgentInputAttachmentReservationReleased)
                    .Select(row => row.AttachmentId)
                    .Distinct()
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                var clearAttachmentIds = reservationAttachmentIds
                    .Except(protectedAttachmentIds, StringComparer.Ordinal)
                    .ToArray();
                if (clearAttachmentIds.Length > 0)
                {
                    await db.Attachments
                        .Where(row => row.ProjectId == projectId
                            && clearAttachmentIds.Contains(row.Id)
                            && row.OwnerKind == OwnerKindAgentInput
                            && row.OwnerId == ownerId)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(row => row.OwnerKind, (string?)null)
                            .SetProperty(row => row.OwnerId, (string?)null)
                            .SetProperty(row => row.OwnerIssueNumber, (int?)null)
                            .SetProperty(row => row.ExpiresAt, (DateTimeOffset?)pendingExpiry),
                            cancellationToken).ConfigureAwait(false);
                }
            }

            await reservationQuery
                .Where(row => row.Status == AgentInputAttachmentReservationReleased)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Validates every submitted attachment id individually, binds
    /// the accepted set to the owning agent-input, and returns a
    /// per-file verdict the API layer surfaces to the caller. The
    /// aggregate <c>MaxCountPerOwner</c> limit is enforced across
    /// already-bound + new submissions; an over-limit aggregate
    /// rejects the whole submission with
    /// <see cref="AttachmentLimitException"/> (matching the existing
    /// issue/comment helpers). Per-file rejections include:
    /// <list type="bullet">
    ///   <item><description><see cref="AgentInputAttachmentRejectionReason.NotFound"/>
    ///   — id not present in this project;</description></item>
    ///   <item><description><see cref="AgentInputAttachmentRejectionReason.Expired"/>
    ///   — pending TTL has elapsed;</description></item>
    ///   <item><description><see cref="AgentInputAttachmentRejectionReason.NotReadable"/>
    ///   — storage backend cannot serve the bytes;</description></item>
    ///   <item><description><see cref="AgentInputAttachmentRejectionReason.ExceedsSizeLimit"/>
    ///   — stored bytes exceed <c>MaxFileBytes</c>;</description></item>
    ///   <item><description><see cref="AgentInputAttachmentRejectionReason.UnsupportedType"/>
    ///   — content-type falls outside the accepted set;</description></item>
    ///   <item><description><see cref="AgentInputAttachmentRejectionReason.AlreadyBound"/>
    ///   — id is owned by another owner (issue/comment/agent-input).</description></item>
    /// </list>
    /// </summary>
    public async Task<AgentInputAttachmentAcceptanceBatch> ValidateAndBindAgentInputAsync(
        string projectId,
        string agentSessionId,
        string inputId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken = default,
        string? reservationId = null)
    {
        EnsureAgentInputOwnerScope(agentSessionId, inputId);
        if (reservationId is not null)
            reservationId = reservationId.Trim();
        if (reservationId is { Length: 0 })
            reservationId = null;
        var ownerId = BuildAgentInputOwnerId(agentSessionId, inputId);
        var ids = NormalizeAttachmentIds(attachmentIds);
        var results = new List<AgentInputAttachmentAcceptance>(ids.Length);
        if (ids.Length == 0)
        {
            return new AgentInputAttachmentAcceptanceBatch(results, 0);
        }

        var now = _time.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await db.Attachments.AsNoTracking()
            .Where(a => a.ProjectId == projectId && ids.Contains(a.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var rowsById = rows.ToDictionary(a => a.Id, StringComparer.Ordinal);

        var existingCount = await db.Attachments.CountAsync(a =>
                a.ProjectId == projectId
                && a.OwnerKind == OwnerKindAgentInput
                && a.OwnerId == ownerId,
            cancellationToken).ConfigureAwait(false);

        var accepted = new List<(int ResultIndex, AttachmentRow Row)>();
        var acceptedForReservation = new List<(int ResultIndex, AttachmentRow Row)>();
        var newlyBoundIds = new List<string>();
        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index];
            if (!rowsById.TryGetValue(id, out var row))
            {
                results.Add(new AgentInputAttachmentAcceptance(id, null, AgentInputAttachmentRejectionReason.NotFound, "Attachment id was not found for this project."));
                continue;
            }

            if (row.ExpiresAt is { } expires && expires <= now && row.OwnerKind is null)
            {
                results.Add(new AgentInputAttachmentAcceptance(id, null, AgentInputAttachmentRejectionReason.Expired, "Attachment pending upload has expired."));
                continue;
            }

            if (row.OwnerKind is not null
                && !(row.OwnerKind == OwnerKindAgentInput && row.OwnerId == ownerId))
            {
                results.Add(new AgentInputAttachmentAcceptance(id, null, AgentInputAttachmentRejectionReason.AlreadyBound, "Attachment is already bound to another owner."));
                continue;
            }

            if (row.Size > _options.MaxFileBytes)
            {
                results.Add(new AgentInputAttachmentAcceptance(id, null, AgentInputAttachmentRejectionReason.ExceedsSizeLimit, $"Attachment exceeds the configured size limit of {_options.MaxFileBytes} bytes."));
                continue;
            }

            if (!IsAcceptableAgentInputContentType(row.ContentType))
            {
                results.Add(new AgentInputAttachmentAcceptance(id, null, AgentInputAttachmentRejectionReason.UnsupportedType, $"Attachment content-type '{row.ContentType ?? string.Empty}' is not supported."));
                continue;
            }

            if (!await IsReadableAsync(row, cancellationToken).ConfigureAwait(false))
            {
                results.Add(new AgentInputAttachmentAcceptance(id, null, AgentInputAttachmentRejectionReason.NotReadable, "Attachment storage could not open the recorded content."));
                continue;
            }

            var descriptor = ToAgentInputDescriptor(row, now);
            results.Add(new AgentInputAttachmentAcceptance(
                id,
                descriptor,
                null,
                null));
            acceptedForReservation.Add((results.Count - 1, row));
            if (row.OwnerKind is null)
                accepted.Add((results.Count - 1, row));
        }

        // Enforce the aggregate per-input attachment cap (already-bound +
        // newly-accepted). The accepted list is the authoritative count;
        // the over-limit case rejects the whole submission with no
        // binding applied so the caller's per-file verdicts still report
        // the individual reasons and nothing is silently dropped.
        EnsureAttachmentLimit(existingCount, accepted.Count);

        if (acceptedForReservation.Count > 0)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var candidate in accepted)
                {
                    var claimed = await db.Attachments
                        .Where(row => row.ProjectId == projectId
                            && row.Id == candidate.Row.Id
                            && row.OwnerKind == null)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(row => row.OwnerKind, OwnerKindAgentInput)
                            .SetProperty(row => row.OwnerId, ownerId)
                            .SetProperty(row => row.OwnerIssueNumber, (int?)null)
                            .SetProperty(row => row.ExpiresAt, (DateTimeOffset?)null),
                            cancellationToken).ConfigureAwait(false);
                    if (claimed == 1)
                    {
                        newlyBoundIds.Add(candidate.Row.Id);
                        continue;
                    }

                    var isOwnedByThisInput = await db.Attachments.AsNoTracking().AnyAsync(row =>
                        row.ProjectId == projectId
                        && row.Id == candidate.Row.Id
                        && row.OwnerKind == OwnerKindAgentInput
                        && row.OwnerId == ownerId,
                        cancellationToken).ConfigureAwait(false);
                    if (isOwnedByThisInput) continue;

                    results[candidate.ResultIndex] = new AgentInputAttachmentAcceptance(
                        candidate.Row.Id,
                        null,
                        AgentInputAttachmentRejectionReason.AlreadyBound,
                        "Attachment is already bound to another owner.");
                }

                if (reservationId is not null)
                {
                    var reservedIds = results
                        .Where(result => result.IsAccepted)
                        .Select(result => result.Id)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    foreach (var id in reservedIds)
                    {
                        db.AgentInputAttachmentReservations.Add(new AgentInputAttachmentReservationRow
                        {
                            ReservationId = reservationId,
                            ProjectId = projectId,
                            AttachmentId = id,
                            OwnerId = ownerId,
                            Status = AgentInputAttachmentReservationPending,
                            CreatedAt = now,
                            ExpiresAt = now.Add(PendingTtl),
                        });
                    }
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        return new AgentInputAttachmentAcceptanceBatch(
            results,
            results.Count(result => result.IsAccepted),
            newlyBoundIds,
            results.Any(result => result.IsAccepted) ? reservationId : null);
    }

    private static AgentSessionInputAttachmentDescriptor ToAgentInputDescriptor(AttachmentRow row, DateTimeOffset acceptedAt) =>
        new(
            Id: row.Id,
            OriginalFileName: row.OriginalFileName,
            ContentType: row.ContentType,
            Size: row.Size,
            AcceptedAt: acceptedAt,
            Source: string.IsNullOrWhiteSpace(row.Source) ? DefaultUploadSource : row.Source,
            Availability: "usable");

    private async Task<bool> IsReadableAsync(AttachmentRow row, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _storage.ReadMetadataAsync(row.StoragePath, cancellationToken).ConfigureAwait(false);
            return metadata is not null;
        }
        catch (Exception ex) when (ex is AttachmentNotFoundException or AttachmentStorageException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAcceptableAgentInputContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return true;
        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
        if (contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)) return true;
        if (contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase)) return true;
        if (contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string BuildAgentInputOwnerId(string agentSessionId, string inputId) =>
        $"{agentSessionId}{AgentInputOwnerIdSeparator}{inputId}";

    private static void EnsureAgentInputOwnerScope(string agentSessionId, string inputId)
    {
        if (string.IsNullOrWhiteSpace(agentSessionId))
            throw new ArgumentException("Agent session id is required.", nameof(agentSessionId));
        if (string.IsNullOrWhiteSpace(inputId))
            throw new ArgumentException("Input id is required.", nameof(inputId));
    }

    public async Task<AttachmentContentResult?> OpenIssueContentAsync(
        string projectId,
        int issueNumber,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a =>
            a.ProjectId == projectId
            && a.Id == attachmentId
            && a.OwnerKind == OwnerKindIssue
            && a.OwnerIssueNumber == issueNumber,
            cancellationToken).ConfigureAwait(false);
        return row is null ? null : OpenContent(row);
    }

    public async Task<AttachmentContentResult?> OpenCommentContentAsync(
        string projectId,
        int issueNumber,
        string commentId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (await LoadCommentAsync(projectId, issueNumber, commentId, cancellationToken).ConfigureAwait(false) is null)
            return null;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a =>
            a.ProjectId == projectId
            && a.Id == attachmentId
            && a.OwnerKind == OwnerKindComment
            && a.OwnerId == commentId,
            cancellationToken).ConfigureAwait(false);
        return row is null ? null : OpenContent(row);
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
            && a.OwnerIssueNumber == issue.Number,
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
        if (!await IsIssueEditableAsync(projectId, comment.IssueNumber, cancellationToken).ConfigureAwait(false))
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

        var expiredReservations = await db.AgentInputAttachmentReservations
            .Where(row => (row.Status == AgentInputAttachmentReservationPending
                    || row.Status == AgentInputAttachmentReservationCoordinatorPending)
                && row.ExpiresAt != null)
            .Select(row => new { row.ProjectId, row.AttachmentId, row.OwnerId, row.ReservationId, row.ExpiresAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        expiredReservations = expiredReservations
            .Where(row => row.ExpiresAt is not null && row.ExpiresAt <= now)
            .ToList();
        if (expiredReservations.Count > 0)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var projectReservations in expiredReservations
                    .GroupBy(row => row.ProjectId, StringComparer.Ordinal))
                {
                    var reservationIds = projectReservations
                        .Select(row => row.ReservationId)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    await db.AgentInputAttachmentReservations
                        .Where(row => row.ProjectId == projectReservations.Key
                            && reservationIds.Contains(row.ReservationId)
                            && (row.Status == AgentInputAttachmentReservationPending
                                || row.Status == AgentInputAttachmentReservationCoordinatorPending))
                        .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                }

                foreach (var candidate in expiredReservations
                    .GroupBy(row => new { row.ProjectId, row.AttachmentId, row.OwnerId }))
                {
                    var protectedByAnotherReservation = await db.AgentInputAttachmentReservations
                        .AnyAsync(row => row.ProjectId == candidate.Key.ProjectId
                            && row.AttachmentId == candidate.Key.AttachmentId
                            && row.Status != AgentInputAttachmentReservationReleased,
                            cancellationToken).ConfigureAwait(false);
                    if (protectedByAnotherReservation)
                        continue;

                    await db.Attachments
                        .Where(row => row.ProjectId == candidate.Key.ProjectId
                            && row.Id == candidate.Key.AttachmentId
                            && row.OwnerKind == OwnerKindAgentInput
                            && row.OwnerId == candidate.Key.OwnerId)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(row => row.OwnerKind, (string?)null)
                            .SetProperty(row => row.OwnerId, (string?)null)
                            .SetProperty(row => row.OwnerIssueNumber, (int?)null)
                            .SetProperty(row => row.ExpiresAt, now),
                            cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

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

    private async Task<string[]> ValidateIssueBindCoreAsync(
        string projectId,
        int issueNumber,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeAttachmentIds(attachmentIds);
        if (ids.Length == 0) return [];

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existingCount = await db.Attachments.CountAsync(a =>
            a.ProjectId == projectId
            && a.OwnerKind == OwnerKindIssue
            && a.OwnerIssueNumber == issueNumber,
            cancellationToken).ConfigureAwait(false);
        EnsureAttachmentLimit(existingCount, ids.Length);
        await ValidateAvailableAsync(db, projectId, ids, cancellationToken).ConfigureAwait(false);
        return ids;
    }

    private async Task<string[]> ValidateCommentBindCoreAsync(
        string projectId,
        string commentId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeAttachmentIds(attachmentIds);
        if (ids.Length == 0) return [];

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existingCount = await db.Attachments.CountAsync(a =>
            a.ProjectId == projectId
            && a.OwnerKind == OwnerKindComment
            && a.OwnerId == commentId,
            cancellationToken).ConfigureAwait(false);
        EnsureAttachmentLimit(existingCount, ids.Length);
        await ValidateAvailableAsync(db, projectId, ids, cancellationToken).ConfigureAwait(false);
        return ids;
    }

    private async Task<string[]> ValidateAgentInputBindCoreAsync(
        string projectId,
        string ownerId,
        IReadOnlyCollection<string>? attachmentIds,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeAttachmentIds(attachmentIds);
        if (ids.Length == 0) return [];

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existingCount = await db.Attachments.CountAsync(a =>
            a.ProjectId == projectId
            && a.OwnerKind == OwnerKindAgentInput
            && a.OwnerId == ownerId,
            cancellationToken).ConfigureAwait(false);
        EnsureAttachmentLimit(existingCount, ids.Length);
        await ValidateAvailableAsync(db, projectId, ids, cancellationToken).ConfigureAwait(false);
        return ids;
    }

    private static string[] NormalizeAttachmentIds(IReadOnlyCollection<string>? attachmentIds) =>
        attachmentIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

    private void EnsureAttachmentLimit(int existingCount, int newCount)
    {
        if (existingCount + newCount > _options.MaxCountPerOwner)
            throw new AttachmentLimitException($"Attachment count exceeds the configured per-owner limit of {_options.MaxCountPerOwner}.");
    }

    private static async Task ValidateAvailableAsync(
        MohistDbContext db,
        string projectId,
        string[] ids,
        CancellationToken cancellationToken)
    {
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
    }

    private AttachmentContentResult OpenContent(AttachmentRow row)
    {
        var stream = _storage.OpenFileContent(row.StoragePath);
        var contentType = NormalizeContentType(row.ContentType) ?? "application/octet-stream";
        var dispositionType = InlineImageContentTypes.Contains(contentType) ? "inline" : "attachment";
        return new AttachmentContentResult(stream, contentType, BuildContentDisposition(dispositionType, row.OriginalFileName));
    }

    private async Task<bool> IsIssueEditableAsync(string projectId, int issueNumber, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Issues.AsNoTracking()
            .FirstOrDefaultAsync(i => i.ProjectId == projectId && i.Number == issueNumber, cancellationToken)
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
