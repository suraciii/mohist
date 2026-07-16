namespace Mohist.Server.Workflow.Storage;

/// <summary>
/// Storage for recorded workflow artifact content. Storage paths are generated
/// by the server; original source paths are preserved as metadata only.
/// </summary>
/// <remarks>
/// Default file layout:
/// <c>~/.mohist/artifacts/workflows/{workflowRunId}/tasks/{taskRunId}/artifacts/{artifactId}</c>.
/// </remarks>
public interface IWorkflowArtifactStorage
{
    /// <summary>
    /// Generates a storage-root-relative path safe to persist on the artifact row.
    /// </summary>
    string GenerateStoragePath(
        string workflowRunId,
        string taskRunId,
        string artifactId,
        WorkflowArtifactStorageKind kind);

    /// <summary>
    /// Persists a single file artifact, reading the stream exactly once.
    /// </summary>
    Task<WorkflowArtifactStorageWriteResult> WriteFileAsync(
        string storagePath,
        Stream content,
        WorkflowArtifactFileWrite write,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a directory artifact, rejecting symlinks, traversal, and
    /// limit breaches.
    /// </summary>
    Task<WorkflowArtifactStorageWriteResult> WriteDirectoryAsync(
        string storagePath,
        IReadOnlyList<WorkflowArtifactDirectoryEntryInput> entries,
        WorkflowArtifactFileWrite write,
        DateTimeOffset recordedAt,
        WorkflowArtifactDirectoryLimits? limits = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens recorded file content; the caller owns the returned stream.
    /// </summary>
    Stream OpenFileContent(string storagePath);

    /// <summary>
    /// Lists contained files for a directory artifact in stable order.
    /// </summary>
    Task<WorkflowArtifactDirectoryListing> ListDirectoryEntriesAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a contained file inside a directory artifact for reading.
    /// The relative path is normalized and validated against traversal.
    /// </summary>
    Stream OpenDirectoryEntry(string storagePath, string relativePath);

    /// <summary>
    /// Reads the stored artifact metadata, if present.
    /// </summary>
    Task<WorkflowArtifactStorageMetadata?> ReadMetadataAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a storage-root-relative path into an absolute filesystem path.
    /// </summary>
    string ResolveAbsolutePath(string storagePath);

    /// <summary>
    /// Storage root resolved from configuration.
    /// </summary>
    string StorageRoot { get; }
}

/// <summary>
/// A single contained file to persist inside a directory artifact.
/// </summary>
public sealed class WorkflowArtifactDirectoryEntryInput
{
    public string RelativePath { get; set; } = string.Empty;

    public long Size { get; set; }

    public string? ContentType { get; set; }

    /// <summary>
    /// Supplier invoked once during the write; returns a stream at position 0.
    /// </summary>
    public Func<Stream> OpenContent { get; set; } = static () => Stream.Null;
}

/// <summary>
/// Base exception for storage-level errors.
/// </summary>
public class WorkflowArtifactStorageException : Exception
{
    public WorkflowArtifactStorageException(string message) : base(message) { }
    public WorkflowArtifactStorageException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Raised when an artifact row exists but its recorded content is missing.
/// </summary>
public sealed class WorkflowArtifactNotFoundException : WorkflowArtifactStorageException
{
    public WorkflowArtifactNotFoundException(string message) : base(message) { }
}
