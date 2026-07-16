using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Storage;
using Orleans;

namespace Mohist.Server.Workflow.Services.Artifacts;

/// <summary>
/// Turns runner-supplied artifact uploads into hidden pending artifact rows.
/// </summary>
/// <remarks>
/// <para>
/// Pending uploads are <em>not</em> user-visible <c>WorkflowArtifact</c>
/// records; they become visible only after task result reporting binds them.
/// </para>
/// <para>
/// Idempotency key:
/// <c>(workflowRunId, workId, taskRunId, path)</c>. Same key + same
/// <see cref="WorkflowArtifactUploadRequest.ContentHash"/> returns the
/// existing pending upload (idempotent retry). Same key + different
/// <see cref="WorkflowArtifactUploadRequest.ContentHash"/> returns a
/// conflict and leaves the original content untouched.
/// </para>
/// </remarks>
public sealed class WorkflowArtifactUploadService : IScopedService
{
    /// <summary>
    /// Default TTL for pending uploads; generous enough for runner crash retry.
    /// </summary>
    public static readonly TimeSpan DefaultPendingTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Directory upload envelope content type.
    /// </summary>
    public const string DirectoryContentType = WorkflowArtifactDirectoryEnvelopeReader.ContentType;

    private static readonly TimeSpan CleanupWarningThreshold = TimeSpan.FromMinutes(5);

    private readonly WorkflowArtifactPendingUploadRepository _pendingUploads;
    private readonly IWorkflowArtifactStorage _storage;
    private readonly IWorkflowArtifactUploadWorkContextResolver _workContextResolver;
    private readonly ILogger<WorkflowArtifactUploadService> _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _pendingTtl;

    public WorkflowArtifactUploadService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IWorkflowArtifactStorage storage,
        IGrainFactory grains,
        ILogger<WorkflowArtifactUploadService> log)
        : this(dbFactory, storage, new WorkflowGrainWorkContextResolver(grains), log, TimeProvider.System, DefaultPendingTtl)
    {
    }

    public WorkflowArtifactUploadService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IWorkflowArtifactStorage storage,
        IWorkflowArtifactUploadWorkContextResolver workContextResolver,
        ILogger<WorkflowArtifactUploadService> log)
        : this(dbFactory, storage, workContextResolver, log, TimeProvider.System, DefaultPendingTtl)
    {
    }

    public WorkflowArtifactUploadService(
        IDbContextFactory<MohistDbContext> dbFactory,
        IWorkflowArtifactStorage storage,
        IWorkflowArtifactUploadWorkContextResolver workContextResolver,
        ILogger<WorkflowArtifactUploadService> log,
        TimeProvider time,
        TimeSpan pendingTtl)
    {
        _pendingUploads = new WorkflowArtifactPendingUploadRepository(dbFactory);
        _storage = storage;
        _workContextResolver = workContextResolver;
        _log = log;
        _time = time;
        _pendingTtl = pendingTtl;
    }

    /// <summary>
    /// Persists a new pending upload or returns the existing
    /// idempotent match. Conflict on different content hash is
    /// surfaced as <see cref="WorkflowArtifactUploadResultKind.Conflict"/>
    /// without touching the original content.
    /// </summary>
    public async Task<WorkflowArtifactUploadResult> UploadAsync(
        WorkflowArtifactUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var validation = Validate(request);
        if (validation is not null) return validation;

        var work = await ResolveWorkContextAsync(request, cancellationToken).ConfigureAwait(false);
        if (work.IsMissing) return work.Result!;

        var context = work.Context!;

        var key = new WorkflowArtifactPendingUploadKey(
            context.WorkflowRunId,
            context.WorkId,
            context.TaskRunId,
            request.Path);
        var existing = await _pendingUploads.FindByKeyAsync(key, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
            return ExistingUploadResult(existing, request.ContentHash);

        var now = _time.GetUtcNow();
        var uploadId = NewUploadId();
        var kind = WorkflowArtifactDirectoryEnvelopeReader.IsDirectoryContentType(request.ContentType)
            ? "directory"
            : "file";
        var pending = new WorkflowArtifactPendingUploadRow
        {
            UploadId = uploadId,
            WorkflowRunId = context.WorkflowRunId,
            WorkId = context.WorkId,
            TaskRunId = context.TaskRunId,
            Path = request.Path,
            Kind = kind,
            ContentType = request.ContentType,
            ContentHash = request.ContentHash,
            Size = request.Size,
            StoragePath = string.Empty,
            CreatedAt = now,
            ExpiresAt = now.Add(_pendingTtl),
        };

        string storagePath = string.Empty;
        try
        {
            storagePath = _storage.GenerateStoragePath(
                context.WorkflowRunId,
                context.TaskRunId,
                uploadId,
                kind == "directory"
                    ? WorkflowArtifactStorageKind.Directory
                    : WorkflowArtifactStorageKind.File);

            await using var content = request.OpenContent();
            WorkflowArtifactStorageWriteResult writeResult;
            int? fileCount = null;
            if (kind == "directory")
            {
                var envelope = await WorkflowArtifactDirectoryEnvelopeReader
                    .ReadAsync(content, request.Size, cancellationToken)
                    .ConfigureAwait(false);
                fileCount = envelope.Entries.Count;
                writeResult = await _storage.WriteDirectoryAsync(
                    storagePath,
                    envelope.Entries,
                    new WorkflowArtifactFileWrite
                    {
                        SourcePath = request.Path,
                        Size = request.Size,
                        ContentType = request.ContentType,
                        ContentHash = request.ContentHash,
                    },
                    now,
                    limits: null,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                writeResult = await _storage.WriteFileAsync(
                    storagePath,
                    content,
                    new WorkflowArtifactFileWrite
                    {
                        SourcePath = request.Path,
                        Size = request.Size,
                        ContentType = request.ContentType,
                        ContentHash = request.ContentHash,
                    },
                    now,
                    cancellationToken).ConfigureAwait(false);
            }

            if (writeResult.Size != request.Size && request.Size >= 0)
            {
                _log.LogWarning(
                    "Pending upload {UploadId} declared size {Declared} but wrote {Actual} bytes",
                    uploadId, request.Size, writeResult.Size);
            }

            pending.StoragePath = writeResult.StoragePath;
            pending.FileCount = fileCount;
            if (kind == "directory")
            {
                pending.Size = writeResult.Size;
            }

            var committed = await _pendingUploads.TryCreateAsync(pending, cancellationToken)
                .ConfigureAwait(false);
            if (!committed.WasCreated)
            {
                await SafeRemoveStorageDirectoryAsync(writeResult.StoragePath, cancellationToken).ConfigureAwait(false);
                return ExistingUploadResult(committed.Row, request.ContentHash);
            }
        }
        catch (InvalidDataException ex)
        {
            // Malformed directory envelope (bad JSON, size mismatch,
            // invalid base64, ...). Surface as a client-visible 400 so
            // the runner gets a diagnosable error instead of a 500.
            await SafeRemoveStorageDirectoryAsync(storagePath, cancellationToken).ConfigureAwait(false);
            return WorkflowArtifactUploadResult.Invalid(ex.Message);
        }
        catch (WorkflowArtifactStorageException ex)
        {
            // Storage rejected the content (limit breach, path
            // traversal, duplicate entry, ...). Same: surface as 400.
            await SafeRemoveStorageDirectoryAsync(storagePath, cancellationToken).ConfigureAwait(false);
            return WorkflowArtifactUploadResult.Invalid(ex.Message);
        }
        catch
        {
            await SafeRemoveStorageDirectoryAsync(storagePath, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (_pendingTtl < CleanupWarningThreshold)
        {
            _log.LogWarning(
                "Pending artifact upload {UploadId} has a TTL of {Ttl} which is below the recommended minimum",
                uploadId, _pendingTtl);
        }

        return WorkflowArtifactUploadResult.Created(ToInfo(pending));
    }

    private static WorkflowArtifactPendingUploadInfo ToInfo(WorkflowArtifactPendingUploadRow row) =>
        new(
            UploadId: row.UploadId,
            WorkflowRunId: row.WorkflowRunId,
            WorkId: row.WorkId,
            TaskRunId: row.TaskRunId,
            Path: row.Path,
            Kind: row.Kind,
            ContentType: row.ContentType,
            ContentHash: row.ContentHash,
            Size: row.Size,
            FileCount: row.FileCount,
            CreatedAt: row.CreatedAt,
            ExpiresAt: row.ExpiresAt);

    private static string NewUploadId() => $"artup_{Guid.NewGuid():N}";

    private static bool HashesMatch(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private WorkflowArtifactUploadResult ExistingUploadResult(
        WorkflowArtifactPendingUploadRow existing,
        string? incomingContentHash)
    {
        if (HashesMatch(existing.ContentHash, incomingContentHash))
        {
            _log.LogDebug(
                "Pending artifact upload {UploadId} for {Path} already exists; returning existing id (idempotent retry)",
                existing.UploadId, existing.Path);
            return WorkflowArtifactUploadResult.Idempotent(ToInfo(existing));
        }

        return WorkflowArtifactUploadResult.ConflictResult(new WorkflowArtifactUploadConflict(
            UploadId: existing.UploadId,
            WorkflowRunId: existing.WorkflowRunId,
            WorkId: existing.WorkId,
            TaskRunId: existing.TaskRunId,
            Path: existing.Path,
            ExistingContentHash: existing.ContentHash,
            IncomingContentHash: incomingContentHash));
    }

    private async Task SafeRemoveStorageDirectoryAsync(
        string storagePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(storagePath)) return;
        try
        {
            await _storage.DeleteAsync(storagePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Failed to remove rolled-back pending artifact storage at {Path}",
                storagePath);
        }
    }

    private WorkflowArtifactUploadResult? Validate(WorkflowArtifactUploadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkflowRunId))
            return WorkflowArtifactUploadResult.Invalid("workflowRunId is required");
        if (string.IsNullOrWhiteSpace(request.WorkId))
            return WorkflowArtifactUploadResult.Invalid("workId is required");
        if (string.IsNullOrWhiteSpace(request.Path))
            return WorkflowArtifactUploadResult.Invalid("path is required");
        if (request.Size < 0)
            return WorkflowArtifactUploadResult.Invalid("size must be zero or positive");
        if (request.OpenContent is null)
            return WorkflowArtifactUploadResult.Invalid("content stream supplier is required");

        return null;
    }

    private async Task<ResolvedWork> ResolveWorkContextAsync(
        WorkflowArtifactUploadRequest request,
        CancellationToken cancellationToken)
    {
        var active = await _workContextResolver
            .ResolveAsync(request.WorkflowRunId, request.WorkId, cancellationToken)
            .ConfigureAwait(false);
        if (active is null)
        {
            return ResolvedWork.Missing(WorkflowArtifactUploadResult.WorkItemNotFound(
                $"Workflow '{request.WorkflowRunId}' has no active work item for workId '{request.WorkId}'"));
        }

        if (string.IsNullOrWhiteSpace(active.TaskRunId))
        {
            return ResolvedWork.Missing(WorkflowArtifactUploadResult.Invalid(
                $"Active work item '{request.WorkId}' has no server-derived taskRunId"));
        }

        return ResolvedWork.Present(new ResolvedWorkContext(
            WorkflowRunId: request.WorkflowRunId,
            WorkId: request.WorkId,
            TaskRunId: active.TaskRunId,
            ProjectId: active.ProjectId,
            IssueId: active.IssueId));
    }

    private readonly struct ResolvedWork
    {
        public ResolvedWorkContext? Context { get; }
        public WorkflowArtifactUploadResult? Result { get; }
        public bool IsMissing => Context is null;

        private ResolvedWork(ResolvedWorkContext? context, WorkflowArtifactUploadResult? result)
        {
            Context = context;
            Result = result;
        }

        public static ResolvedWork Present(ResolvedWorkContext context) => new(context, null);
        public static ResolvedWork Missing(WorkflowArtifactUploadResult result) => new(null, result);
    }

    private sealed record ResolvedWorkContext(
        string WorkflowRunId,
        string WorkId,
        string TaskRunId,
        string? ProjectId,
        string? IssueId);
}

/// <summary>
/// Resolves the producing task run id for a runner upload by asking
/// the workflow grain for the active work context. Extracted into a
/// small interface so service tests can drive the resolution
/// table-driven without spinning up an Orleans silo.
/// </summary>
public interface IWorkflowArtifactUploadWorkContextResolver
{
    Task<WorkflowActiveWorkView?> ResolveAsync(
        string workflowRunId,
        string workId,
        CancellationToken cancellationToken = default);
}

/// <summary>Default resolver backed by the workflow grain.</summary>
public sealed class WorkflowGrainWorkContextResolver : IWorkflowArtifactUploadWorkContextResolver
{
    private readonly IGrainFactory _grains;

    public WorkflowGrainWorkContextResolver(IGrainFactory grains)
    {
        _grains = grains;
    }

    public Task<WorkflowActiveWorkView?> ResolveAsync(
        string workflowRunId,
        string workId,
        CancellationToken cancellationToken = default)
    {
        var grain = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        return grain.GetActiveWorkAsync(workId);
    }
}

/// <summary>
/// Inputs to <see cref="WorkflowArtifactUploadService.UploadAsync"/>.
/// The request carries every metadata field that the runner contract
/// documents for a pending artifact upload; the content stream
/// supplier is invoked exactly once.
/// </summary>
public sealed class WorkflowArtifactUploadRequest
{
    public string WorkflowRunId { get; init; } = string.Empty;
    public string WorkId { get; init; } = string.Empty;

    /// <summary>Original source path captured by the runner. Stored as display metadata only.</summary>
    public string Path { get; init; } = string.Empty;

    public string? ContentType { get; init; }

    /// <summary>
    /// Declared content hash, e.g. <c>sha256:&lt;hex&gt;</c>. Used as
    /// the idempotency discriminator against the existing pending
    /// upload; <c>null</c>/empty disables hash-based matching.
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>Logical size in bytes. <c>0</c> is allowed (empty file).</summary>
    public long Size { get; init; }

    /// <summary>
    /// Supplier that yields a stream positioned at the start of the
    /// content. Invoked exactly once. The caller owns the stream
    /// returned by the supplier; the service consumes and disposes it.
    /// </summary>
    public Func<Stream> OpenContent { get; init; } = static () => Stream.Null;
}

/// <summary>Result envelope returned by the upload service.</summary>
public sealed record WorkflowArtifactUploadResult(
    WorkflowArtifactUploadResultKind Kind,
    WorkflowArtifactPendingUploadInfo? Pending = null,
    WorkflowArtifactUploadConflict? Conflict = null,
    string? Error = null)
{
    public static WorkflowArtifactUploadResult Created(WorkflowArtifactPendingUploadInfo info) =>
        new(WorkflowArtifactUploadResultKind.Created, Pending: info);

    public static WorkflowArtifactUploadResult Idempotent(WorkflowArtifactPendingUploadInfo info) =>
        new(WorkflowArtifactUploadResultKind.Idempotent, Pending: info);

    public static WorkflowArtifactUploadResult ConflictResult(WorkflowArtifactUploadConflict conflict) =>
        new(WorkflowArtifactUploadResultKind.Conflict, Conflict: conflict);

    public static WorkflowArtifactUploadResult Invalid(string error) =>
        new(WorkflowArtifactUploadResultKind.Invalid, Error: error);

    public static WorkflowArtifactUploadResult WorkItemNotFound(string error) =>
        new(WorkflowArtifactUploadResultKind.WorkItemNotFound, Error: error);
}

public enum WorkflowArtifactUploadResultKind
{
    Created,
    Idempotent,
    Conflict,
    Invalid,
    WorkItemNotFound,
}

/// <summary>Public view of a pending artifact upload record.</summary>
public sealed record WorkflowArtifactPendingUploadInfo(
    string UploadId,
    string WorkflowRunId,
    string WorkId,
    string TaskRunId,
    string Path,
    string Kind,
    string? ContentType,
    string? ContentHash,
    long? Size,
    int? FileCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Detail payload returned when an upload key collides with an
/// existing pending upload carrying a different content hash.
/// </summary>
public sealed record WorkflowArtifactUploadConflict(
    string UploadId,
    string WorkflowRunId,
    string WorkId,
    string TaskRunId,
    string Path,
    string? ExistingContentHash,
    string? IncomingContentHash);
