namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Filesystem-backed storage for recorded <c>WorkflowArtifact</c>
/// content. The service owns the on-disk layout rooted under
/// <c>~/.mohist/artifacts/</c> and exposes path generation, write,
/// and read primitives for the upload endpoint (T-005), the binding
/// flow (T-007), and the content/browsing endpoints (T-008).
/// </summary>
/// <remarks>
/// <para>
/// Storage paths are generated or sanitized by the server. The
/// original source artifact path is never used as a filesystem path
/// segment; it is preserved as metadata only.
/// </para>
/// <para>
/// The default storage root is
/// <c>~/.mohist/artifacts/workflows/{workflowRunId}/tasks/{taskRunId}/artifacts/{artifactId}</c>.
/// File artifacts persist <c>metadata.json</c> + <c>content</c>;
/// directory artifacts persist <c>metadata.json</c> + a <c>files/</c>
/// tree.
/// </para>
/// </remarks>
public interface IWorkflowArtifactStorage
{
    /// <summary>
    /// Generates the storage path for a recorded artifact under the
    /// configured storage root. The returned path is relative to the
    /// storage root and is safe to persist on
    /// <c>WorkflowArtifactRow.ArtifactStoragePath</c>.
    /// </summary>
    /// <param name="workflowRunId">Workflow run id from the upload/binding context.</param>
    /// <param name="taskRunId">Server-derived producing task run id.</param>
    /// <param name="artifactId">Mohist-generated artifact id.</param>
    /// <param name="kind">Storage shape (file or directory).</param>
    string GenerateStoragePath(
        string workflowRunId,
        string taskRunId,
        string artifactId,
        WorkflowArtifactStorageKind kind);

    /// <summary>
    /// Persists a single file artifact. The supplied stream is read
    /// exactly once and written atomically to the storage location's
    /// <c>content</c> file. <c>metadata.json</c> is written alongside.
    /// </summary>
    /// <exception cref="WorkflowArtifactStorageException">
    /// Thrown when the storage root cannot be created, the destination
    /// already exists, or the supplied size is negative.
    /// </exception>
    Task<WorkflowArtifactStorageWriteResult> WriteFileAsync(
        string storagePath,
        Stream content,
        WorkflowArtifactFileWrite write,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a directory artifact. Entries are written under
    /// <c>{storagePath}/files/</c> and validated against
    /// <paramref name="limits"/>. Symlinks and entries that escape the
    /// collection root are rejected.
    /// </summary>
    /// <exception cref="WorkflowArtifactStorageException">
    /// Thrown when an entry escapes the collection root, follows a
    /// symlink, exceeds <see cref="WorkflowArtifactDirectoryLimits.MaxFileCount"/>,
    /// or exceeds <see cref="WorkflowArtifactDirectoryLimits.MaxTotalBytes"/>.
    /// </exception>
    Task<WorkflowArtifactStorageWriteResult> WriteDirectoryAsync(
        string storagePath,
        IReadOnlyList<WorkflowArtifactDirectoryEntryInput> entries,
        WorkflowArtifactFileWrite write,
        DateTimeOffset recordedAt,
        WorkflowArtifactDirectoryLimits? limits = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a read-only stream over the recorded file artifact
    /// content. The caller is responsible for disposing the stream.
    /// </summary>
    /// <exception cref="WorkflowArtifactNotFoundException">
    /// Thrown when the storage directory or <c>content</c> file does
    /// not exist.
    /// </exception>
    /// <exception cref="WorkflowArtifactStorageException">
    /// Thrown when the storage path does not point at a file artifact.
    /// </exception>
    Stream OpenFileContent(string storagePath);

    /// <summary>
    /// Lists the recorded contained files for a directory artifact in
    /// stable, sorted order.
    /// </summary>
    /// <exception cref="WorkflowArtifactNotFoundException">
    /// Thrown when the storage directory does not exist.
    /// </exception>
    /// <exception cref="WorkflowArtifactStorageException">
    /// Thrown when the storage path does not point at a directory
    /// artifact.
    /// </exception>
    Task<WorkflowArtifactDirectoryListing> ListDirectoryEntriesAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a contained file inside a directory artifact for reading.
    /// The <paramref name="relativePath"/> is normalized and validated
    /// against traversal; it must match an entry previously written.
    /// </summary>
    /// <exception cref="WorkflowArtifactNotFoundException">
    /// Thrown when the contained file is missing.
    /// </exception>
    /// <exception cref="WorkflowArtifactStorageException">
    /// Thrown when the relative path escapes the collection.
    /// </exception>
    Stream OpenDirectoryEntry(string storagePath, string relativePath);

    /// <summary>
    /// Reads the <c>metadata.json</c> for a stored artifact. Returns
    /// <c>null</c> if no metadata file is present.
    /// </summary>
    Task<WorkflowArtifactStorageMetadata?> ReadMetadataAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a storage path (relative to the storage root) into an
    /// absolute filesystem path.
    /// </summary>
    string ResolveAbsolutePath(string storagePath);

    /// <summary>
    /// Storage root resolved from configuration. Exposed for callers
    /// that need it for diagnostics; the public API operates on
    /// relative paths.
    /// </summary>
    string StorageRoot { get; }
}

/// <summary>
/// A single contained file to be persisted inside a directory
/// artifact. The entry carries the relative path within the
/// collection and a content supplier; the supplier is read once,
/// during the directory write.
/// </summary>
public sealed class WorkflowArtifactDirectoryEntryInput
{
    public string RelativePath { get; set; } = string.Empty;

    public long Size { get; set; }

    public string? ContentType { get; set; }

    /// <summary>
    /// Supplier that yields the file content. Invoked exactly once
    /// during the write. Implementations should return a stream
    /// positioned at the start of the content.
    /// </summary>
    public Func<Stream> OpenContent { get; set; } = static () => Stream.Null;
}

/// <summary>
/// Base exception for storage-level errors. The HTTP layer is expected
/// to translate this into the appropriate status code.
/// </summary>
public class WorkflowArtifactStorageException : Exception
{
    public WorkflowArtifactStorageException(string message) : base(message) { }
    public WorkflowArtifactStorageException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Raised when the recorded storage content cannot be located. This
/// is distinct from a binding-time or validation error: the artifact
/// row exists but the on-disk content is gone (drift, manual cleanup,
/// partial write recovery).
/// </summary>
public sealed class WorkflowArtifactNotFoundException : WorkflowArtifactStorageException
{
    public WorkflowArtifactNotFoundException(string message) : base(message) { }
}
