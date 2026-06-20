using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Storage;
using Orleans;

namespace Mohist.Server.Workflow.Services.Artifacts;

/// <summary>
/// Domain service that turns runner-supplied artifact uploads into
/// hidden pending <c>WorkflowArtifactPendingUploadRow</c> records.
/// The service is invoked by the runner upload endpoint (T-005); it
/// will be reused by the binding flow (T-007) when pending uploads
/// are validated against the reporting workflow run and work item.
/// </summary>
/// <remarks>
/// <para>
/// Pending uploads are <em>not</em> user-visible <c>WorkflowArtifact</c>
/// records. They live only as rows under
/// <c>WorkflowArtifactPendingUploads</c> together with the underlying
/// storage content, and become visible only after
/// <c>WorkflowGrain.ReportResultAsync</c> binds them during task
/// result reporting.
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
public sealed class WorkflowArtifactUploadService
{
    /// <summary>
    /// Default TTL for a pending upload. Rows past expiry are eligible
    /// for cleanup by a hosted TTL job; this value is intentionally
    /// generous so retries across runner crashes are accepted.
    /// </summary>
    public static readonly TimeSpan DefaultPendingTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Content type used by the runner to signal a directory upload
    /// carried as a JSON envelope of base64-encoded contained files.
    /// The server decodes the envelope and persists the contained
    /// files through <c>WriteDirectoryAsync</c>.
    /// </summary>
    public const string DirectoryContentType = "application/x-mohist-artifact-directory";

    private static readonly TimeSpan CleanupWarningThreshold = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
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
        _dbFactory = dbFactory;
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

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var existing = await db.WorkflowArtifactPendingUploads
            .FirstOrDefaultAsync(p =>
                p.WorkflowRunId == context.WorkflowRunId
                && p.WorkId == context.WorkId
                && p.TaskRunId == context.TaskRunId
                && p.Path == request.Path,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (HashesMatch(existing.ContentHash, request.ContentHash))
            {
                _log.LogDebug(
                    "Pending artifact upload {UploadId} for {Path} already exists; returning existing id (idempotent retry)",
                    existing.UploadId, request.Path);
                return WorkflowArtifactUploadResult.Idempotent(ToInfo(existing));
            }

            return WorkflowArtifactUploadResult.ConflictResult(new WorkflowArtifactUploadConflict(
                UploadId: existing.UploadId,
                WorkflowRunId: existing.WorkflowRunId,
                WorkId: existing.WorkId,
                TaskRunId: existing.TaskRunId,
                Path: existing.Path,
                ExistingContentHash: existing.ContentHash,
                IncomingContentHash: request.ContentHash));
        }

        var now = _time.GetUtcNow();
        var uploadId = NewUploadId();
        var kind = IsDirectoryContentType(request.ContentType)
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
                var envelope = await ReadDirectoryEnvelopeAsync(content, request.Size, cancellationToken)
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

            db.WorkflowArtifactPendingUploads.Add(pending);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Lost a race to another concurrent upload with the
                // same idempotency key. Re-read and either return the
                // existing row (idempotent) or surface the conflict.
                db.ChangeTracker.Clear();
                var racer = await db.WorkflowArtifactPendingUploads
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p =>
                        p.WorkflowRunId == context.WorkflowRunId
                        && p.WorkId == context.WorkId
                        && p.TaskRunId == context.TaskRunId
                        && p.Path == request.Path,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (racer is null) throw;

                SafeRemoveStorageDirectory(writeResult.StoragePath);

                if (HashesMatch(racer.ContentHash, request.ContentHash))
                    return WorkflowArtifactUploadResult.Idempotent(ToInfo(racer));

                return WorkflowArtifactUploadResult.ConflictResult(new WorkflowArtifactUploadConflict(
                    UploadId: racer.UploadId,
                    WorkflowRunId: racer.WorkflowRunId,
                    WorkId: racer.WorkId,
                    TaskRunId: racer.TaskRunId,
                    Path: racer.Path,
                    ExistingContentHash: racer.ContentHash,
                    IncomingContentHash: request.ContentHash));
            }
        }
        catch
        {
            SafeRemoveStorageDirectory(storagePath);
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

    private static bool IsDirectoryContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        return string.Equals(contentType, DirectoryContentType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the directory envelope produced by the runner and
    /// converts it into a list of <see cref="WorkflowArtifactDirectoryEntryInput"/>.
    /// The envelope is a JSON object of the shape
    /// <c>{ kind: "directory", files: [{ path, size, data: &lt;base64&gt; }] }</c>.
    /// The runner encodes the directory as a single multipart file
    /// part for transport and the server decodes it here so the
    /// multipart endpoint remains file-only.
    /// </summary>
    private static async Task<DirectoryEnvelope> ReadDirectoryEnvelopeAsync(
        Stream content,
        long declaredSize,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            bytes = ms.ToArray();
        }
        if (declaredSize >= 0 && bytes.LongLength != declaredSize)
        {
            throw new InvalidDataException(
                $"Directory envelope size mismatch: declared {declaredSize} bytes, read {bytes.LongLength} bytes.");
        }
        DirectoryEnvelopeEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<DirectoryEnvelopeEnvelope>(bytes, JSON.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Directory upload content is not a valid artifact envelope: {ex.Message}", ex);
        }
        if (envelope is null || !string.Equals(envelope.Kind, "directory", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Directory upload envelope must declare kind: \"directory\".");
        }
        if (envelope.Files is null || envelope.Files.Count == 0)
        {
            throw new InvalidDataException(
                "Directory upload envelope must contain at least one contained file.");
        }
        var entries = new List<WorkflowArtifactDirectoryEntryInput>(envelope.Files.Count);
        foreach (var file in envelope.Files)
        {
            if (file is null) continue;
            if (string.IsNullOrWhiteSpace(file.Path))
                throw new InvalidDataException("Directory entry path is required.");
            byte[] data;
            try
            {
                data = Convert.FromBase64String(file.Data ?? string.Empty);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException(
                    $"Directory entry '{file.Path}' data is not valid base64: {ex.Message}", ex);
            }
            entries.Add(new WorkflowArtifactDirectoryEntryInput
            {
                RelativePath = file.Path,
                Size = file.Size ?? data.LongLength,
                ContentType = file.ContentType,
                OpenContent = () => new MemoryStream(data, writable: false),
            });
        }
        return new DirectoryEnvelope(entries);
    }

    private sealed record DirectoryEnvelope(IReadOnlyList<WorkflowArtifactDirectoryEntryInput> Entries);

    private sealed class DirectoryEnvelopeEnvelope
    {
        public string? Kind { get; set; }
        public List<DirectoryEnvelopeFile>? Files { get; set; }
    }

    private sealed class DirectoryEnvelopeFile
    {
        public string? Path { get; set; }
        public long? Size { get; set; }
        public string? ContentType { get; set; }
        public string? Data { get; set; }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("constraint", StringComparison.OrdinalIgnoreCase) == true;
    }

    private void SafeRemoveStorageDirectory(string storagePath)
    {
        if (string.IsNullOrEmpty(storagePath)) return;
        try
        {
            var absolute = _storage.ResolveAbsolutePath(storagePath);
            var directory = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
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
